using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Novgov.Network;
using UnityEngine;

namespace Novgov.Server
{
    public partial class MatchSessionManager
    {
        // Mêmes points d'ancrage que UnitSpawnerUI.AutoDeployTeamFallback —
        // le rayon délimite la zone légale où un placement manuel soumis via "submit_deployment"
        // est accepté (voir ClampToDeploymentZone) ; au-delà, la position est ramenée sur le bord
        // de la zone plutôt que rejetée en bloc, pour rester tolérant à une imprécision de tap tout
        // en empêchant un client modifié de déployer au contact immédiat de l'adversaire.
        // PUBLICS depuis le 2026-09-03 : le client doit pouvoir MONTRER cette zone et refuser sur
        // place un placement hors zone. Jusque-là il l'ignorait totalement — un placement à 80m était
        // accepté par le dock (anneau vert), puis ramené sur le bord d'un cercle invisible par
        // ClampToDeploymentZone à la confirmation. Le joueur voyait donc ses unités "sauter" ailleurs
        // sans qu'aucun élément d'interface n'ait jamais mentionné l'existence d'une zone de départ.
        // Une seule définition, partagée, évite que les deux côtés divergent.
        public static readonly Vector3 Team1DeploymentZoneCenter = new Vector3(-25f, 0f, -25f);
        public static readonly Vector3 Team2DeploymentZoneCenter = new Vector3(25f, 0f, 25f);
        public const float DeploymentZoneRadius = 22f;

        // Plafonds de comptage du déploiement manuel : jusqu'à 6 unités de combat (n'importe quel
        // mélange parmi Fantassin/CharLeopard/VehiculeCanon/Mortier) + jusqu'à 8 barricades (même
        // stock que le dock solo, voir UnitSpawnerUI.maxBarricadesPerTeam) — un placement qui
        // dépasserait un plafond, ou un type d'unité hors de l'enum, est écarté INDIVIDUELLEMENT
        // (voir FilterRosterToBudget), jamais la soumission entière. Publics depuis le 2026-09-08,
        // même raison que Team1/2DeploymentZoneCenter ci-dessus : UnitSpawnerUI en a besoin pour
        // afficher l'effectif réel PENDANT le déploiement.
        //
        // 2026-09-19 (demande explicite, retour joueur en pleine partie) : le budget en POINTS
        // (CombatPointBudget, ajouté le 2026-09-06) a été entièrement retiré, y compris son affichage
        // client — seuls les plafonds de comptage bruts ci-dessous subsistent.
        public const int MaxDeployedCombatUnits = 6;
        public const int MaxDeployedBarricades = 8;
        // 2026-09-12 (demande explicite) : sans plafond dédié, un camp pouvait aligner jusqu'à 6
        // Mortiers — même règle appliquée côté client (voir UnitSpawnerUI.MaxMortarsPerTeam) pour un
        // message immédiat, mais l'AUTORITÉ reste ici : un client modifié qui soumettrait
        // `submit_deployment` directement sans jamais passer par le dock doit être bloqué pareil.
        public const int MaxMortarsPerTeam = 2;

        /// <summary>Garde EXACTEMENT les placements que le joueur a lui-même choisis (même type, même
        /// position) qui tiennent dans les plafonds de comptage — dans l'ORDRE de soumission — et ne
        /// rejette qu'un placement individuellement invalide (type hors énum, coordonnée NaN/Infinity)
        /// ou celui qui ferait dépasser un plafond (unités de combat, barricades, mortiers).
        /// 2026-09-19 (demande explicite) : le budget en POINTS (CombatPointBudget) a été retiré —
        /// seuls les plafonds de comptage bruts subsistent. <paramref name="anyDropped"/> est vrai si
        /// au moins un placement a été écarté.</summary>
        private static List<UnitPlacement> FilterRosterToBudget(UnitPlacement[] placements, out bool anyDropped)
        {
            var kept = new List<UnitPlacement>();
            anyDropped = false;
            if (placements == null) return kept;

            int combatCount = 0, barricadeCount = 0, mortarCount = 0;
            foreach (var p in placements)
            {
                if (!Enum.IsDefined(typeof(UnitSpawnerUI.UnitType), p.unit_type)) { anyDropped = true; continue; }
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) { anyDropped = true; continue; }
                if (float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)) { anyDropped = true; continue; }

                var type = (UnitSpawnerUI.UnitType)p.unit_type;
                bool isBarricade = type == UnitSpawnerUI.UnitType.BarricadeRoutiere;
                bool isMortar = type == UnitSpawnerUI.UnitType.Mortier;

                bool fitsCount = isBarricade ? barricadeCount + 1 <= MaxDeployedBarricades : combatCount + 1 <= MaxDeployedCombatUnits;
                bool fitsMortarCap = !isMortar || mortarCount + 1 <= MaxMortarsPerTeam;
                if (!fitsCount || !fitsMortarCap) { anyDropped = true; continue; }

                if (isBarricade) barricadeCount++; else combatCount++;
                if (isMortar) mortarCount++;
                kept.Add(p);
            }
            return kept;
        }

        /// <summary>Garde seulement (x, z) dans les limites du monde généré : depuis le 2026-09-06
        /// (demande explicite), le placement manuel est accepté n'importe où sur la carte.</summary>
        private static Vector3 ClampToDeploymentZone(Vector3 pos, int team)
        {
            // Borne la position aux limites RÉELLES du monde généré (voir
            // TacticalGridBuilder.DefaultWorldRadius, le même rayon utilisé pour construire la grille
            // tactique) pour éviter qu'un client modifié déploie hors du monde. Un ±25 en dur ici
            // aurait à tort recadré des placements légitimes bien à l'intérieur de la vraie carte
            // (le rayon réel est 120, pas 25) — voir l'historique git (ancien journal 08).
            float r = Novgov.TacticalCore.TacticalGridBuilder.DefaultWorldRadius;
            return new Vector3(Mathf.Clamp(pos.x, -r, r), pos.y, Mathf.Clamp(pos.z, -r, r));
        }

        private static int InferUnitType(UnitAI u) => (int)InferUnitTypeEnum(u);

        /// <summary>Type d'unité déduit des drapeaux de l'UnitAI. Version typée, pour pouvoir
        /// interroger UnitTypeStats.</summary>
        private static UnitSpawnerUI.UnitType InferUnitTypeEnum(UnitAI u) => UnitTypeStats.InferType(u);

        /// <summary>Spawn réellement les unités d'UN camp (placement manuel — filtré au budget mais
        /// jamais remplacé tant qu'AU MOINS UN placement du joueur peut être conservé, voir
        /// FilterRosterToBudget — ou repli automatique si rien n'est utilisable) et renvoie la liste
        /// des unités effectivement posées, pour le broadcast "deployment_result".</summary>
        private List<DeployedUnit> ResolveDeployment(PlayerConnection conn, int team, out bool rosterTrimmed)
        {
            rosterTrimmed = false;
            var kept = conn.HasSubmittedDeployment
                ? FilterRosterToBudget(conn.PendingDeployment, out rosterTrimmed)
                : new List<UnitPlacement>();
            // "trimmed" ne veut dire quelque chose pour le joueur QUE si une partie de ce qu'il a
            // choisi a effectivement survécu — si tout a été écarté (kept vide), c'est le repli fixe
            // ci-dessous qui s'applique en totalité, pas un simple recadrage : le message affiché au
            // joueur ("le reste a été posé tel quel") serait faux dans ce cas.
            rosterTrimmed &= kept.Count > 0;

            if (kept.Count > 0)
            {
                foreach (var p in kept)
                {
                    Vector3 clamped = ClampToDeploymentZone(new Vector3(p.x, p.y, p.z), team);
                    UnitSpawnerUI.Instance.SpawnUnitAt((UnitSpawnerUI.UnitType)p.unit_type, clamped, team);
                }
            }
            else
            {
                UnitSpawnerUI.Instance.AutoDeployTeamFallback(team);
            }

            var placed = new List<DeployedUnit>();
            foreach (var u in UnitAI.AllLivingUnits)
            {
                if (u.teamID != team) continue;
                placed.Add(new DeployedUnit
                {
                    unit_id = u.gameObject.name,
                    unit_type = InferUnitType(u),
                    team_id = team,
                    x = u.transform.position.x,
                    y = u.transform.position.y,
                    z = u.transform.position.z
                });
            }
            foreach (var b in RoadBarrier.AllBarriers)
            {
                if (b == null || b.teamID != team) continue;
                placed.Add(new DeployedUnit
                {
                    unit_id = b.gameObject.name,
                    unit_type = (int)UnitSpawnerUI.UnitType.BarricadeRoutiere,
                    team_id = team,
                    x = b.transform.position.x,
                    y = b.transform.position.y,
                    z = b.transform.position.z
                });
            }
            return placed;
        }

        /// <summary>Attend jusqu'à DeploymentSeconds que les DEUX joueurs soumettent
        /// "submit_deployment" (chacun avec ses propres MapReadyMaxWaitSeconds/DeploymentSeconds à
        /// partir du moment où SA propre carte est prête), puis spawn réellement les unités des deux
        /// camps via ResolveDeployment et diffuse le résultat final aux deux clients via
        /// "deployment_result" (voir 03-network-protocol.md). Appelée par RunMatchLive
        /// (MatchSessionManager_MatchLive.cs). Les "city_verify" reçus pendant l'attente des cartes
        /// sont traités par DrainMessages (voir AnswerCityVerify).</summary>
        private IEnumerator RunDeploymentPhaseLive(string matchId, PlayerConnection p1, PlayerConnection p2)
        {
            p1.HasSubmittedDeployment = false;
            p2.HasSubmittedDeployment = false;
            p1.PendingDeployment = null;
            p2.PendingDeployment = null;
            p1.MapReady = false;
            p2.MapReady = false;

            DateTime deploymentPhaseStartUtc = DateTime.UtcNow;
            Debug.Log($"[Timing] [{matchId}] RunDeploymentPhaseLive démarré à {deploymentPhaseStartUtc:O} — attente MapReady (max {MapReadyMaxWaitSeconds}s) puis déploiement (max {DeploymentSeconds}s).");

            DateTime? p1ReadyAtUtc = p1.MapReady ? deploymentPhaseStartUtc : (DateTime?)null;
            DateTime? p2ReadyAtUtc = p2.MapReady ? deploymentPhaseStartUtc : (DateTime?)null;

            float mapWait = MapReadyMaxWaitSeconds;
            while (mapWait > 0f && !(p1.MapReady && p2.MapReady))
            {
                DrainMessages(p1, 0);
                DrainMessages(p2, 0);
                if (p1.IsDisconnected && p2.IsDisconnected) yield break;

                if (p1ReadyAtUtc == null && p1.MapReady) p1ReadyAtUtc = DateTime.UtcNow;
                if (p2ReadyAtUtc == null && p2.MapReady) p2ReadyAtUtc = DateTime.UtcNow;

                mapWait -= Time.deltaTime;
                yield return null;
            }

            DateTime deploymentCountdownStartUtc = DateTime.UtcNow;
            bool p1Done = p1ReadyAtUtc == null;
            bool p2Done = p2ReadyAtUtc == null;
            int lastTickP1 = -1, lastTickP2 = -1;
            while (!(p1Done && p2Done))
            {
                DrainMessages(p1, 0);
                DrainMessages(p2, 0);
                if (p1.IsDisconnected && p2.IsDisconnected) yield break;

                if (!p1Done)
                {
                    if (p1.HasSubmittedDeployment) p1Done = true;
                    else
                    {
                        float p1Remaining = DeploymentSeconds - (float)(DateTime.UtcNow - p1ReadyAtUtc.Value).TotalSeconds;
                        if (p1Remaining <= 0f) p1Done = true;
                        else
                        {
                            int secondsLeft = Mathf.CeilToInt(p1Remaining);
                            if (secondsLeft != lastTickP1 && !p1.IsDisconnected)
                            {
                                lastTickP1 = secondsLeft;
                                p1.Send(new NetMessage { type = "turn_timer", seconds_remaining = secondsLeft });
                            }
                        }
                    }
                }

                if (!p2Done)
                {
                    if (p2.HasSubmittedDeployment) p2Done = true;
                    else
                    {
                        float p2Remaining = DeploymentSeconds - (float)(DateTime.UtcNow - p2ReadyAtUtc.Value).TotalSeconds;
                        if (p2Remaining <= 0f) p2Done = true;
                        else
                        {
                            int secondsLeft = Mathf.CeilToInt(p2Remaining);
                            if (secondsLeft != lastTickP2 && !p2.IsDisconnected)
                            {
                                lastTickP2 = secondsLeft;
                                p2.Send(new NetMessage { type = "turn_timer", seconds_remaining = secondsLeft });
                            }
                        }
                    }
                }

                yield return null;
            }

            double deploymentElapsedSec = (DateTime.UtcNow - deploymentCountdownStartUtc).TotalSeconds;
            Debug.Log($"[Timing] [{matchId}] Compte à rebours de déploiement terminé après {deploymentElapsedSec:F1}s réelles (attendu {DeploymentSeconds}s).");

            var team1Units = ResolveDeployment(p1, 1, out bool team1Trimmed);
            var team2Units = ResolveDeployment(p2, 2, out bool team2Trimmed);
            Debug.Log($"[Trajectoire] [{matchId}] Déploiement résolu : équipe1={team1Units.Count} unité(s), équipe2={team2Units.Count} unité(s).");

            var team1Barricades = team1Units.Where(u => u.unit_type == (int)UnitSpawnerUI.UnitType.BarricadeRoutiere);
            var team2Barricades = team2Units.Where(u => u.unit_type == (int)UnitSpawnerUI.UnitType.BarricadeRoutiere);
            if (!p1.IsDisconnected) p1.Send(new NetMessage { type = "deployment_result", deployed_units = team1Units.Concat(team2Barricades).ToArray(), reason = team1Trimmed ? "roster_trimmed" : null });
            if (!p2.IsDisconnected) p2.Send(new NetMessage { type = "deployment_result", deployed_units = team2Units.Concat(team1Barricades).ToArray(), reason = team2Trimmed ? "roster_trimmed" : null });
        }
    }
}
