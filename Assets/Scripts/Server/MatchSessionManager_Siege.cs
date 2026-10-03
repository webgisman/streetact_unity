using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Novgov.Network;
using UnityEngine;
using UnityEngine.Networking;

namespace Novgov.Server
{
    /// <summary>
    /// Sièges de quartier — données et RÉSOLUTION AUTOMATIQUE. Un siège se joue normalement comme une
    /// bataille au tour par tour entre l'attaquant et le défenseur (MatchSessionManager_SiegeBattle.cs).
    /// Ce fichier gère le cas où ils ne se sont pas retrouvés avant l'échéance (6 h) :
    /// SiegeResolutionLoop détecte le siège échu et ResolveSiegeNow joue le combat SANS joueur, avec
    /// les troupes des casernes des deux camps (garnison du défenseur renforcée par le niveau de son
    /// bâtiment), spawnées en formation de choc serrée (SpawnClashForce) pour que le combat
    /// auto-engage (portée/ligne de vue, aucun ordre nécessaire). ApplySiegeOutcome applique les
    /// conséquences (prise, pillage, bouclier, rapports) dans les deux cas. Schéma : schema.sql §11
    /// (zones.building_level/shield_until, public.zone_sieges, start_siege()/upgrade_building()).
    /// </summary>
    public partial class MatchSessionManager
    {
        // =====================================================================
        // DTOs réseau/DB
        // =====================================================================

        [Serializable] private class DeployedUnitList { public DeployedUnit[] units; }

        [Serializable]
        private class SiegeRowDto
        {
            public long id;
            public int tile_x;
            public int tile_y;
            public int zoom;
            public string attacker_user_id;
            public string defender_user_id;
            public string status;
            public string deadline;
            public DeployedUnitList attacker_deployment_json;
            public DeployedUnitList defender_deployment_json;
        }
        [Serializable] private class SiegeRowQueryResult { public SiegeRowDto[] items; }

        private const string SiegeRowSelect = "id,tile_x,tile_y,zoom,attacker_user_id,defender_user_id,status,deadline,attacker_deployment_json,defender_deployment_json";

        private IEnumerator FetchSiegeRow(long siegeId, Action<SiegeRowDto> onResult)
        {
            string url = $"{GameServerBootstrap.RestUrl}/zone_sieges?id=eq.{siegeId}&select={SiegeRowSelect}";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) { onResult(null); yield break; }

            try
            {
                string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                var parsed = JsonUtility.FromJson<SiegeRowQueryResult>(wrapped);
                onResult(parsed?.items != null && parsed.items.Length > 0 ? parsed.items[0] : null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Siège] Parsing du siège #{siegeId} échoué : {ex.Message}");
                onResult(null);
            }
        }

        // =====================================================================
        // Résolution automatique (échéance dépassée) — boucle de fond
        // =====================================================================

        private const float SiegeResolutionPollSeconds = 60f;

        private IEnumerator SiegeResolutionLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(SiegeResolutionPollSeconds);
                // Un déploiement en cours (attaquant OU défenseur) ou une résolution déjà en cours
                // tient déjà matchInProgress — réessaie simplement au prochain passage plutôt que de
                // faire la queue : au pire un siège échu attend une minute de plus, sans conséquence.
                if (matchInProgress) continue;

                long dueSiegeId = 0;
                yield return FetchDueSiegeId(id => dueSiegeId = id);
                if (dueSiegeId <= 0) continue;

                matchInProgress = true;
                StartCoroutine(ReportInstanceStatus());
                StartCoroutine(ResolveSiegeNowGuarded(dueSiegeId));
            }
        }

        [Serializable] private class DueSiegeEntry { public long id; }
        [Serializable] private class DueSiegeQueryResult { public DueSiegeEntry[] items; }

        private IEnumerator FetchDueSiegeId(Action<long> onResult)
        {
            string nowIso = UnityWebRequest.EscapeURL(DateTime.UtcNow.ToString("o"));
            string url = $"{GameServerBootstrap.RestUrl}/zone_sieges?status=eq.pending&deadline=lt.{nowIso}&select=id&order=deadline.asc&limit=1";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) { onResult(0); yield break; }

            try
            {
                string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                var parsed = JsonUtility.FromJson<DueSiegeQueryResult>(wrapped);
                onResult(parsed?.items != null && parsed.items.Length > 0 ? parsed.items[0].id : 0);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Siège] Parsing des sièges échus échoué : {ex.Message}");
                onResult(0);
            }
        }

        private IEnumerator ResolveSiegeNowGuarded(long siegeId)
        {
            return SafeCoroutineRunner.Run(
                ResolveSiegeNow(siegeId),
                onComplete: () =>
                {
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                },
                onException: (Exception e) =>
                {
                    Debug.LogError($"[Siège] Exception pendant la résolution du siège #{siegeId} : {e}");
                    matchInProgress = false;
                    StartCoroutine(ReportInstanceStatus());
                }
            );
        }

        // =====================================================================
        // Résolution automatique du combat — HEADLESS (aucun des deux joueurs connecté)
        // =====================================================================

        // Formation de choc : les deux forces sont volontairement spawnées à ~12m l'une de l'autre
        // (pas aux zones de déploiement PvP habituelles, ~70m d'écart) — voir tête de fichier pour
        // pourquoi (aucun ordre de mouvement n'existe côté serveur pendant une résolution headless).
        private static readonly Vector3 SiegeClashAnchorAttacker = new Vector3(-6f, 0f, 0f);
        private static readonly Vector3 SiegeClashAnchorDefender = new Vector3(6f, 0f, 0f);

        private void SpawnClashForce(int team, DeployedUnit[] units, Vector3 anchor)
        {
            if (units == null) return;
            int i = 0;
            foreach (var u in units)
            {
                if (!Enum.IsDefined(typeof(UnitSpawnerUI.UnitType), u.unit_type)) continue;
                var type = (UnitSpawnerUI.UnitType)u.unit_type;
                if (type == UnitSpawnerUI.UnitType.BarricadeRoutiere) continue; // immobile, inutile dans un choc frontal
                float angle = i * 47f;
                float radius = 2f + i * 0.6f;
                Vector3 offset = Quaternion.Euler(0f, angle, 0f) * new Vector3(radius, 0f, 0f);
                UnitSpawnerUI.Instance.SpawnUnitAt(type, anchor + offset, team);
                i++;
            }
        }

        private static List<DeployedUnit> ExpandRosterToDeployedUnits(Novgov.Auth.PlayerRosterItem[] roster, int team = 2)
        {
            var list = new List<DeployedUnit>();
            if (roster == null) return list;
            foreach (var item in roster)
            {
                if (item.quantity <= 0) continue;
                UnitSpawnerUI.UnitType ut = UnitSpawnerUI.UnitType.Fantassin;
                if (item.unit_type.Equals("CharLeopard", StringComparison.OrdinalIgnoreCase)) ut = UnitSpawnerUI.UnitType.CharLeopard;
                else if (item.unit_type.Equals("VehiculeCanon", StringComparison.OrdinalIgnoreCase)) ut = UnitSpawnerUI.UnitType.VehiculeCanon;
                else if (item.unit_type.Equals("Mortier", StringComparison.OrdinalIgnoreCase)) ut = UnitSpawnerUI.UnitType.Mortier;
                for (int i = 0; i < item.quantity; i++) list.Add(new DeployedUnit { unit_type = (int)ut, team_id = team });
            }
            return list;
        }

        private IEnumerator FetchPlayerRoster(string userId, Action<Novgov.Auth.PlayerRosterItem[]> onResult)
        {
            string url = $"{GameServerBootstrap.RestUrl}/player_roster?user_id=eq.{userId}";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            Novgov.Auth.PlayerRosterItem[] roster = null;
            if (req.result == UnityWebRequest.Result.Success)
            {
                try { roster = Novgov.Auth.JsonHelper.FromJson<Novgov.Auth.PlayerRosterItem>(req.downloadHandler.text); }
                catch (Exception ex) { Debug.LogWarning($"[Siège] Parsing roster ({userId}) échoué : {ex.Message}"); }
            }
            onResult(roster ?? Array.Empty<Novgov.Auth.PlayerRosterItem>());
        }

        [Serializable] private class ZoneLevelEntry { public int building_level; }
        [Serializable] private class ZoneLevelQueryResult { public ZoneLevelEntry[] items; }

        private IEnumerator FetchZoneBuildingLevel(int tileX, int tileY, int zoom, Action<int> onResult)
        {
            string url = $"{GameServerBootstrap.RestUrl}/zones?tile_x=eq.{tileX}&tile_y=eq.{tileY}&zoom=eq.{zoom}&select=building_level";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            int level = 1;
            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                    var parsed = JsonUtility.FromJson<ZoneLevelQueryResult>(wrapped);
                    if (parsed?.items != null && parsed.items.Length > 0) level = Mathf.Max(1, parsed.items[0].building_level);
                }
                catch (Exception ex) { Debug.LogWarning($"[Siège] Parsing building_level ({tileX},{tileY}) échoué : {ex.Message}"); }
            }
            onResult(level);
        }

        private IEnumerator FetchUsernameById(string userId, Action<string> onResult)
        {
            string url = $"{GameServerBootstrap.RestUrl}/profiles?id=eq.{userId}&select=username";
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("apikey", GameServerBootstrap.ServiceRoleKey);
            req.SetRequestHeader("Authorization", "Bearer " + GameServerBootstrap.ServiceRoleKey);
            yield return req.SendWebRequest();

            string username = null;
            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
                    var parsed = JsonUtility.FromJson<UsernameQueryResult>(wrapped);
                    if (parsed?.items != null && parsed.items.Length > 0) username = parsed.items[0].username;
                }
                catch (Exception ex) { Debug.LogWarning($"[Siège] Parsing username ({userId}) échoué : {ex.Message}"); }
            }
            onResult(username);
        }

        private static string EscapeJsonString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private IEnumerator WriteSiegeNotification(string userId, string type, string message)
        {
            string json = "{\"user_id\":\"" + userId + "\",\"type\":\"" + type + "\",\"message\":\"" + EscapeJsonString(message) + "\"}";
            yield return PostgrestPost("/notifications", json);
        }

        /// <summary>Résout ENTIÈREMENT un siège : charge la Zone, spawn les deux forces en formation de
        /// choc, lance UN vrai tour de combat (vrai moteur Unity), détermine le vainqueur, met à jour
        /// zones/zone_sieges, notifie les deux joueurs. Appelable aussi bien juste après le second
        /// déploiement soumis (résolution immédiate) que par SiegeResolutionLoop (échéance dépassée,
        /// défense auto-générée depuis le roster actuel du défenseur).</summary>
        private IEnumerator ResolveSiegeNow(long siegeId)
        {
            SiegeRowDto siege = null;
            yield return FetchSiegeRow(siegeId, s => siege = s);
            if (siege == null || siege.status != "pending") yield break; // déjà résolu ailleurs ou disparu

            // RÉSERVATION ATOMIQUE (rapport d'audit §1, course de résolution inter-instances) :
            // l'attaquant et le défenseur peuvent soumettre leur déploiement à quelques instances
            // DIFFÉRENTES du pool (voir RunSiegeAttackDeploy/RunSiegeDefendDeploy) au même instant —
            // chacune relit alors la même ligne encore "pending" (l'écriture de l'autre est déjà
            // commitée) et déclencherait sinon sa PROPRE résolution en parallèle : deux simulations
            // indépendantes (PhysX/NavMesh non déterministe), deux écritures concurrentes de
            // status/winner_user_id, et des notifications potentiellement CONTRADICTOIRES pour les
            // deux joueurs. Le PATCH conditionnel ci-dessous (même principe que CaptureZoneInDb pour
            // zones.owner_user_id) ne peut réussir que pour UNE SEULE instance : la perdante voit 0
            // ligne affectée et abandonne immédiatement.
            bool reserved = false;
            yield return PostgrestPatchChecked($"/zone_sieges?id=eq.{siegeId}&status=eq.pending", "{\"status\":\"resolving\"}", ok => reserved = ok);
            if (!reserved)
            {
                Debug.Log($"[Siège] #{siegeId} déjà réservé par une autre instance pour résolution — abandon ici.");
                yield break;
            }

            yield return LoadZoneOnServer(siege.tile_x, siege.tile_y);
            UnitSpawnerUI.Instance.ClearAllUnits();
            yield return null;

            DeployedUnit[] attackerUnits = siege.attacker_deployment_json?.units;
            DeployedUnit[] defenderUnits = siege.defender_deployment_json?.units;
            if (attackerUnits == null || attackerUnits.Length == 0)
            {
                // 2026-10-03 : avec la bataille au tour par tour (MatchSessionManager_SiegeBattle.cs),
                // l'attaquant ne dépose plus de déploiement à l'avance — si les deux joueurs ne se sont
                // jamais retrouvés avant l'échéance, ce sont les troupes de SA caserne qui attaquent
                // (symétrique de la garnison du défenseur ci-dessous).
                Novgov.Auth.PlayerRosterItem[] attackerRoster = null;
                yield return FetchPlayerRoster(siege.attacker_user_id, r => attackerRoster = r);
                attackerUnits = ExpandRosterToDeployedUnits(attackerRoster, team: 1).ToArray();
            }

            if (defenderUnits == null || defenderUnits.Length == 0)
            {
                // Défenseur jamais venu répondre avant l'échéance : garnison auto-générée à partir de
                // SON roster ACTUEL, renforcée selon le niveau de bâtiment de la Zone (schema.sql §11,
                // "renforcer son économie" rend concrètement la défense plus dure).
                Novgov.Auth.PlayerRosterItem[] roster = null;
                yield return FetchPlayerRoster(siege.defender_user_id, r => roster = r);
                int buildingLevel = 1;
                yield return FetchZoneBuildingLevel(siege.tile_x, siege.tile_y, siege.zoom, lvl => buildingLevel = lvl);

                var expanded = ExpandRosterToDeployedUnits(roster);
                for (int i = 0; i < buildingLevel - 1; i++)
                    expanded.Add(new DeployedUnit { unit_type = (int)UnitSpawnerUI.UnitType.Fantassin, team_id = 2 });
                defenderUnits = expanded.ToArray();
            }

            Vector3 attackerAnchor = UnitSpawnerUI.FindGroundLevelNavPoint(SiegeClashAnchorAttacker, 40f);
            Vector3 defenderAnchor = UnitSpawnerUI.FindGroundLevelNavPoint(SiegeClashAnchorDefender, 40f);
            SpawnClashForce(1, attackerUnits, attackerAnchor);
            SpawnClashForce(2, defenderUnits, defenderAnchor);

            bool attackerHasUnits = UnitAI.AllLivingUnits.Any(u => u.teamID == 1);
            bool defenderHasUnits = UnitAI.AllLivingUnits.Any(u => u.teamID == 2);
            if (attackerHasUnits && defenderHasUnits)
            {
                // Voir la doc de RunExecutionPhaseRealEngine : aucune UnitAI des deux camps ne doit
                // rester isPlayerControlled=false ici — ni l'un ni l'autre n'a de planification IA
                // (TacticalAIPlanner) à ce stade, le combat auto-engage tout seul (portée/ligne de vue).
                foreach (var unit in UnitAI.AllLivingUnits) unit.isPlayerControlled = true;
                yield return RunExecutionPhaseRealEngine(1, null, null);
            }

            int attackerAlive = UnitAI.AllLivingUnits.Count(u => u.teamID == 1);
            int defenderAlive = UnitAI.AllLivingUnits.Count(u => u.teamID == 2);
            int winnerTeam;
            if (defenderAlive == 0 && attackerAlive > 0) winnerTeam = 1;
            else if (attackerAlive == 0) winnerTeam = 2; // anéantissement mutuel OU défenseur seul survivant -> il garde sa Zone
            else
            {
                int attackerHealth = UnitAI.AllLivingUnits.Where(u => u.teamID == 1).Sum(u => u.health);
                int defenderHealth = UnitAI.AllLivingUnits.Where(u => u.teamID == 2).Sum(u => u.health);
                winnerTeam = attackerHealth > defenderHealth ? 1 : 2; // égalité -> avantage défenseur
            }

            bool attackerWins = false;
            yield return ApplySiegeOutcome(siege, winnerTeam == 1, captured => attackerWins = captured);

            UnitSpawnerUI.Instance.ClearAllUnits();
            Debug.Log($"[Siège] #{siegeId} résolu : {(attackerWins ? "attaquant vainqueur" : "défenseur tient")} — Zone ({siege.tile_x},{siege.tile_y}).");
        }

        /// <summary>Conséquences d'un siège tranché — partagé par la résolution automatique
        /// (ResolveSiegeNow, échéance) et la bataille jouée au tour par tour (MatchSessionManager_
        /// SiegeBattle.OnSiegeBattleOver) : prise du quartier (si l'attaquant a gagné ET que la
        /// capture est confirmée en base), pillage de PA, bouclier de 6 h, siège clos, rapports aux
        /// deux joueurs. <paramref name="onDone"/> reçoit vrai si le quartier a réellement changé de mains.</summary>
        private IEnumerator ApplySiegeOutcome(SiegeRowDto siege, bool attackerWonBattle, Action<bool> onDone)
        {
            bool attackerWins = attackerWonBattle;
            bool captureConfirmed = false;
            if (attackerWins)
            {
                yield return CaptureZoneInDb(siege.tile_x, siege.tile_y, siege.attacker_user_id, siege.defender_user_id, ok => captureConfirmed = ok);
                if (captureConfirmed)
                    yield return LootActionPoints(siege.attacker_user_id, siege.defender_user_id);
            }
            attackerWins = attackerWins && captureConfirmed;

            string nowIso = DateTime.UtcNow.ToString("o");
            string shieldUntilIso = DateTime.UtcNow.AddHours(6).ToString("o");
            yield return PostgrestPatch($"/zones?tile_x=eq.{siege.tile_x}&tile_y=eq.{siege.tile_y}&zoom=eq.{siege.zoom}", "{\"shield_until\":\"" + shieldUntilIso + "\"}");

            string winnerUserId = attackerWins ? siege.attacker_user_id : siege.defender_user_id;
            yield return PostgrestPatch($"/zone_sieges?id=eq.{siege.id}", "{\"status\":\"resolved\",\"winner_user_id\":\"" + winnerUserId + "\",\"resolved_at\":\"" + nowIso + "\"}");

            string attackerName = null, defenderName = null;
            yield return FetchUsernameById(siege.attacker_user_id, n => attackerName = n);
            yield return FetchUsernameById(siege.defender_user_id, n => defenderName = n);
            attackerName = attackerName ?? "Un joueur";
            defenderName = defenderName ?? "Un joueur";

            if (attackerWins)
            {
                yield return WriteSiegeNotification(siege.attacker_user_id, "siege_won", $"Vous avez remporté votre siège sur la Zone ({siege.tile_x},{siege.tile_y}) — elle est à vous !");
                yield return WriteSiegeNotification(siege.defender_user_id, "siege_lost", $"{attackerName} a pris votre territoire ({siege.tile_x},{siege.tile_y}) lors d'un siège.");
            }
            else
            {
                yield return WriteSiegeNotification(siege.attacker_user_id, "siege_lost", $"Votre siège sur la Zone ({siege.tile_x},{siege.tile_y}) a échoué face à la garnison de {defenderName}.");
                yield return WriteSiegeNotification(siege.defender_user_id, "siege_won", $"Vous avez défendu avec succès votre territoire ({siege.tile_x},{siege.tile_y}) contre {attackerName}.");
            }

            onDone?.Invoke(attackerWins);
        }
    }
}
