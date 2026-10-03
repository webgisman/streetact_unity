using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Novgov.Network;
using UnityEngine;

namespace Novgov.Server
{
    /// <summary>
    /// BATAILLE DE SIÈGE AU TOUR PAR TOUR (2026-10-03, demande joueur : "c'est un jeu tour par tour où
    /// je planifie mes trajectoires et je fais fin de tour, l'autre joueur aussi, puis le serveur
    /// vérifie, simule et rend la simulation aux joueurs pour qu'ils la regardent, et ainsi de suite
    /// jusqu'à la fin"). Remplace, pour les clients à jour, le siège "en différé" de
    /// MatchSessionManager_Siege.cs (chaque camp déployait séparément, puis UN seul tour de combat
    /// headless tranchait sans que personne ne joue).
    ///
    /// Règle choisie par le joueur pour un adversaire absent : LE PREMIER ARRIVÉ ATTEND L'AUTRE.
    ///   1. L'attaquant déclare le siège (RPC start_siege, côté client) puis se connecte avec
    ///      mode="siege_battle" + siege_id ; le défenseur fait de même via DÉFENDRE. Chacun entre dans
    ///      la "salle d'attente" de CE siège (siegeLobbies) et y patiente (signal de vie centralisé de
    ///      PlayerConnection, il peut annuler à tout moment — le siège reste alors ouvert).
    ///   2. Dès que l'attaquant ET le défenseur sont dans la salle (et que la scène est libre), la
    ///      bataille démarre : RunMatchLive, EXACTEMENT le moteur au tour par tour de Match à mort
    ///      (déploiement des deux camps, puis à chaque tour planification -> fin de tour -> simulation
    ///      serveur au vrai moteur -> rejeu synchronisé chez les deux joueurs), sur la carte du quartier
    ///      assiégé, attaquant = équipe 1, défenseur = équipe 2.
    ///   3. Fin de partie : victoire de l'attaquant = le quartier change de propriétaire (et pillage de
    ///      PA, bouclier 6 h) ; égalité ou victoire du défenseur = il garde son quartier. Voir
    ///      ApplySiegeOutcome, partagé avec la résolution automatique.
    ///   4. S'ils ne se retrouvent pas avant l'échéance (6 h), SiegeResolutionLoop joue la bataille
    ///      automatiquement avec les troupes de leurs casernes (MatchSessionManager_Siege.ResolveSiegeNow).
    ///
    /// Les DEUX joueurs doivent se connecter à la MÊME instance du pool : le client la choisit de façon
    /// déterministe à partir du siege_id (voir MultiplayerMatchController.ConnectToGameServerCoroutine).
    /// </summary>
    public partial class MatchSessionManager
    {
        /// <summary>Statut zone_sieges.status pendant qu'une bataille au tour par tour se joue — la
        /// boucle d'échéance ne prend que 'pending', elle ne résoudra donc jamais un siège en pleine
        /// bataille. Remis à 'pending' si la bataille s'interrompt sans vainqueur (deux départs).</summary>
        private const string SiegeStatusBattle = "battle";

        private class SiegeLobby
        {
            public SiegeRowDto Siege;
            public PlayerConnection Attacker;
            public PlayerConnection Defender;
        }

        private readonly Dictionary<long, SiegeLobby> siegeLobbies = new Dictionary<long, SiegeLobby>();

        private void HandleSiegeBattleMessage(PlayerConnection conn, NetMessage msg)
        {
            StartCoroutine(JoinSiegeLobby(conn, msg.siege_id));
        }

        private IEnumerator JoinSiegeLobby(PlayerConnection conn, long siegeId)
        {
            SiegeRowDto siege = null;
            yield return FetchSiegeRow(siegeId, s => siege = s);
            bool isParticipant = siege != null && (siege.attacker_user_id == conn.UserId || siege.defender_user_id == conn.UserId);
            if (siege == null || siege.status != "pending" || !isParticipant || IsSiegeExpired(siege))
            {
                conn.Send(new NetMessage { type = "siege_deploy_ack", success = false, reason = siege != null && IsSiegeExpired(siege) ? "siege_expired" : "siege_invalid" });
                conn.Close();
                yield break;
            }

            if (!siegeLobbies.TryGetValue(siegeId, out SiegeLobby lobby))
            {
                lobby = new SiegeLobby();
                siegeLobbies[siegeId] = lobby;
            }
            lobby.Siege = siege;

            bool isAttacker = siege.attacker_user_id == conn.UserId;
            PlayerConnection previous = isAttacker ? lobby.Attacker : lobby.Defender;
            // Même joueur reconnecté (ex: a annulé puis est revenu) : la nouvelle connexion remplace
            // l'ancienne, qui ne recevrait plus jamais rien.
            if (previous != null && previous != conn && !previous.IsDisconnected) previous.Close();
            if (isAttacker) lobby.Attacker = conn; else lobby.Defender = conn;
            conn.Mode = "siege";

            Debug.Log($"[SiègeBataille] #{siegeId} : {(isAttacker ? "attaquant" : "défenseur")} {conn.UserId} en salle d'attente " +
                      $"(attaquant {(lobby.Attacker != null ? "présent" : "absent")}, défenseur {(lobby.Defender != null ? "présent" : "absent")}).");
        }

        private static bool IsSiegeExpired(SiegeRowDto siege) =>
            DateTime.TryParse(siege.deadline, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime deadline)
            && deadline.ToUniversalTime() <= DateTime.UtcNow;

        /// <summary>Appelé à chaque frame par Update : purge les départs, ferme les salles dont le
        /// siège a expiré, et démarre la bataille dès que les deux camps sont présents ET que la scène
        /// partagée est libre (un seul match à la fois sur ce processus, voir TryStartMatchLive).</summary>
        private void PumpSiegeLobbies()
        {
            if (siegeLobbies.Count == 0) return;

            foreach (long siegeId in siegeLobbies.Keys.ToList())
            {
                SiegeLobby lobby = siegeLobbies[siegeId];

                // Les messages reçus pendant l'attente (heartbeat client toutes les 5s) sont consommés
                // ici : DrainMessages ne tourne qu'une fois la bataille commencée.
                DiscardLobbyMessages(lobby.Attacker);
                DiscardLobbyMessages(lobby.Defender);

                if (lobby.Attacker != null && lobby.Attacker.IsDisconnected) lobby.Attacker = null;
                if (lobby.Defender != null && lobby.Defender.IsDisconnected) lobby.Defender = null;

                if (lobby.Attacker == null && lobby.Defender == null)
                {
                    siegeLobbies.Remove(siegeId);
                    continue;
                }

                if (lobby.Siege != null && IsSiegeExpired(lobby.Siege))
                {
                    // Échéance passée : la résolution automatique (avec les casernes) va s'en charger.
                    foreach (var waiting in new[] { lobby.Attacker, lobby.Defender })
                    {
                        if (waiting == null) continue;
                        waiting.Send(new NetMessage { type = "siege_deploy_ack", success = false, reason = "siege_expired" });
                        waiting.Close();
                    }
                    siegeLobbies.Remove(siegeId);
                    continue;
                }

                if (lobby.Attacker != null && lobby.Defender != null && !matchInProgress)
                {
                    siegeLobbies.Remove(siegeId);
                    matchInProgress = true;
                    activeMatchCount++;
                    StartCoroutine(ReportInstanceStatus());
                    StartCoroutine(RunSiegeBattleGuarded(lobby));
                }
            }
        }

        private static void DiscardLobbyMessages(PlayerConnection conn)
        {
            if (conn == null) return;
            while (conn.TryDequeueMessage(out NetMessage msg))
            {
                if (msg.type == "heartbeat") conn.LastHeartbeat = DateTime.UtcNow;
            }
        }

        private IEnumerator RunSiegeBattleGuarded(SiegeLobby lobby)
        {
            return SafeCoroutineRunner.Run(
                RunSiegeBattle(lobby),
                onComplete: () => { activeMatchCount--; },
                onException: (Exception e) =>
                {
                    Debug.LogError($"[SiègeBataille] Exception pendant la bataille du siège #{lobby.Siege?.id} — abandon en match nul, siège rouvert : {e}");
                    AbortMatchSafely(lobby.Attacker);
                    AbortMatchSafely(lobby.Defender);
                    if (lobby.Siege != null)
                        StartCoroutine(PostgrestPatch($"/zone_sieges?id=eq.{lobby.Siege.id}&status=eq.{SiegeStatusBattle}", "{\"status\":\"pending\"}"));
                    UnitSpawnerUI.Instance?.ClearAllUnits();
                    matchInProgress = false;
                    activeMatchCount--;
                    StartCoroutine(ReportInstanceStatus());
                }
            );
        }

        private IEnumerator RunSiegeBattle(SiegeLobby lobby)
        {
            SiegeRowDto siege = lobby.Siege;

            // Réservation atomique (même principe que ResolveSiegeNow) : une seule instance, une seule
            // bataille, et la boucle d'échéance ne touche plus ce siège tant qu'il est en 'battle'.
            bool reserved = false;
            yield return PostgrestPatchChecked($"/zone_sieges?id=eq.{siege.id}&status=eq.pending", "{\"status\":\"" + SiegeStatusBattle + "\"}", ok => reserved = ok);
            if (!reserved)
            {
                Debug.Log($"[SiègeBataille] #{siege.id} n'est plus 'pending' (déjà résolu ou en bataille ailleurs) — abandon.");
                foreach (var p in new[] { lobby.Attacker, lobby.Defender })
                {
                    p.Send(new NetMessage { type = "siege_deploy_ack", success = false, reason = "siege_invalid" });
                    p.Close();
                }
                matchInProgress = false;
                StartCoroutine(ReportInstanceStatus());
                yield break;
            }

            Debug.Log($"[SiègeBataille] #{siege.id} : bataille au tour par tour lancée sur le quartier ({siege.tile_x},{siege.tile_y}).");
            string cacheKey = $"Z{siege.zoom}_{siege.tile_x}_{siege.tile_y}";
            yield return RunMatchLive(lobby.Attacker, lobby.Defender, cacheKey,
                (winnerTeam, bothLeft, extras) => OnSiegeBattleOver(siege, winnerTeam, bothLeft, extras));
        }

        /// <summary>Appliqué par RunMatchLive juste AVANT "match_over" (pour que le message dise déjà si
        /// le quartier a changé de mains). Deux départs sans vainqueur : le siège est rouvert plutôt
        /// que tranché sur une partie que personne n'a jouée jusqu'au bout.</summary>
        private IEnumerator OnSiegeBattleOver(SiegeRowDto siege, int winnerTeam, bool bothLeft, MatchOverExtras extras)
        {
            if (bothLeft)
            {
                yield return PostgrestPatch($"/zone_sieges?id=eq.{siege.id}", "{\"status\":\"pending\"}");
                extras.Reason = "siege_reopened";
                yield break;
            }

            bool captured = false;
            // Égalité (winnerTeam 0) = avantage défenseur, même règle que la résolution automatique.
            yield return ApplySiegeOutcome(siege, winnerTeam == 1, c => captured = c);
            extras.Success = captured;
            if (winnerTeam == 1 && !captured) extras.Reason = "zone_lost_race";
        }
    }
}
