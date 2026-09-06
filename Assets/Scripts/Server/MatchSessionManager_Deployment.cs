using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Novgov.Network;
using Novgov.TacticalCore;
using UnityEngine;

namespace Novgov.Server
{
    public partial class MatchSessionManager
    {
        // Mêmes points d'ancrage que UnitSpawnerUI.AutoDeployBattlefield/AutoDeployTeamFallback —
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

        public static Vector3 DeploymentZoneCenterForTeam(int team) => team == 1 ? Team1DeploymentZoneCenter : Team2DeploymentZoneCenter;

        // Budget de déploiement manuel : jusqu'à 4 unités de combat (n'importe quel mélange parmi
        // Fantassin/CharLeopard/VehiculeCanon/Mortier) + jusqu'à 8 barricades (même stock que le
        // dock solo, voir UnitSpawnerUI.maxBarricadesPerTeam) — au-delà, ou un type d'unité hors de
        // l'enum, la soumission ENTIÈRE est rejetée et ce camp reçoit le repli automatique.
        private const int MaxDeployedCombatUnits = 6;
        private const int MaxDeployedBarricades = 8;
        // Budget en points (voir UnitTypeStats.DeploymentCost) : plafonne la PUISSANCE
        // totale déployée, pas seulement le nombre d'unités — sans ça, déployer le nombre max
        // d'unités les plus lourdes (CharLeopard) était toujours strictement supérieur à toute
        // composition mixte, tuant toute variété tactique (voir rapport d'audit jouabilité, défaut
        // bloquant #1). 8 points permet par ex. 2 CharLeopard + 2 Fantassin, ou 4 VehiculeCanon, ou
        // 1 CharLeopard + 1 Mortier + 1 VehiculeCanon + 1 Fantassin — mais jamais 4 CharLeopard (12).
        private const int CombatPointBudget = 8;

        /// <summary>Vrai si le multi-ensemble de placements respecte le budget autorisé (voir
        /// MaxDeployedCombatUnits/MaxDeployedBarricades/CombatPointBudget) et ne contient que des
        /// types/coordonnées valides — sinon la soumission ENTIÈRE est rejetée (repli automatique
        /// pour tout ce camp), plutôt que d'essayer de n'en garder qu'une partie.</summary>
        private static bool IsRosterValid(UnitPlacement[] placements)
        {
            if (placements == null || placements.Length == 0) return false;
            if (placements.Length > MaxDeployedCombatUnits + MaxDeployedBarricades) return false;

            int combatCount = 0, barricadeCount = 0, totalCost = 0;
            foreach (var p in placements)
            {
                if (!Enum.IsDefined(typeof(UnitSpawnerUI.UnitType), p.unit_type)) return false;
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) return false;
                if (float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)) return false;

                var type = (UnitSpawnerUI.UnitType)p.unit_type;
                if (type == UnitSpawnerUI.UnitType.BarricadeRoutiere) barricadeCount++;
                else combatCount++;
                totalCost += UnitTypeStats.DeploymentCost(type);
            }
            return combatCount <= MaxDeployedCombatUnits && barricadeCount <= MaxDeployedBarricades
                && totalCost <= CombatPointBudget;
        }

        /// <summary>Ramène (x, z) dans la zone de déploiement légale du camp (cercle centré sur le
        /// même point d'ancrage qu'AutoDeployBattlefield/AutoDeployTeamFallback) — jamais un rejet
        /// en bloc pour une simple imprécision de tap, mais impossible de déployer au contact
        /// immédiat de l'adversaire en soumettant volontairement une position lointaine.</summary>
        // 2026-09-06 : restriction de rayon (22m) désactivée sur demande explicite — le placement
        // manuel doit maintenant être accepté n'importe où sur la carte, pas seulement près du point
        // d'ancrage de l'équipe. Compromis assumé : perd la protection anti-triche qui empêchait un
        // client modifié de soumettre une position au contact immédiat de l'adversaire (voir
        // l'ancienne doc dans 03-network-protocol.md). Les constantes/centres restent utilisés par
        // AutoDeployBattlefield (déploiement automatique), non concerné par ce changement.
        private static Vector3 ClampToDeploymentZone(Vector3 pos, int team) => pos;

        private static int InferUnitType(UnitAI u) => (int)InferUnitTypeEnum(u);

        /// <summary>Type d'unité déduit des drapeaux de l'UnitAI. Version typée, pour pouvoir
        /// interroger UnitTypeStats (voir movementBudget dans BuildTacticalUnit).</summary>
        private static UnitSpawnerUI.UnitType InferUnitTypeEnum(UnitAI u)
        {
            if (u.isMortar) return UnitSpawnerUI.UnitType.Mortier;
            if (u.isCanonVehicle) return UnitSpawnerUI.UnitType.VehiculeCanon;
            if (u.isTank) return UnitSpawnerUI.UnitType.CharLeopard;
            return UnitSpawnerUI.UnitType.Fantassin;
        }

        /// <summary>Spawn réellement les unités d'UN camp (placement manuel validé+recadré, ou repli
        /// automatique) et renvoie la liste des unités effectivement posées, pour le broadcast
        /// "deployment_result".</summary>
        private List<DeployedUnit> ResolveDeployment(PlayerConnection conn, int team)
        {
            bool valid = conn.HasSubmittedDeployment && IsRosterValid(conn.PendingDeployment);

            if (valid)
            {
                foreach (var p in conn.PendingDeployment)
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

        // =====================================================================
        // "Option B" (2026-08-30) — équivalents en donnée pure des méthodes ci-dessus, pour les
        // parties Deathmatch/Zone de Contrôle (voir MatchState.cs). Les méthodes ci-dessus
        // (RunPlanningPhase, RunExecutionPhase, ApplyForPlayer, ApplyOrdersToUnits, BuildUnitOrders,
        // BuildSnapshotsFromEvents, CaptureTacticalSnapshot, ResolveDeployment) restent INTACTES et
        // continuent de servir EXCLUSIVEMENT le mode Conquête (RunConquestSkirmish, toujours basé sur
        // de vraies UnitAI/BuildingStructure, hors scope de ce chantier) — aucune des deux familles
        // de méthodes n'appelle jamais l'autre. (RunDeploymentPhase, l'équivalent "vivant" de
        // RunDeploymentPhasePure ci-dessous, n'avait plus aucun appelant nulle part dans le projet —
        // reliquat d'avant "Option B" — et a été supprimée le 2026-09-06.)
        // =====================================================================

        /// <summary>Attend jusqu'à DeploymentSeconds que les DEUX joueurs soumettent
        /// "submit_deployment", puis construit les unités/barricades des deux camps directement en
        /// TacticalUnit/Barricade (voir MatchState.World) — soit à partir du placement manuel soumis
        /// (recadré dans la zone légale, voir ResolveDeploymentPure), soit via le repli automatique si
        /// rien de valide n'a été reçu à temps — et diffuse le résultat final aux deux clients via
        /// "deployment_result" (voir 03-network-protocol.md).</summary>
        private IEnumerator RunDeploymentPhasePure(MatchState ms)
        {
            PlayerConnection p1 = ms.P1, p2 = ms.P2;
            p1.HasSubmittedDeployment = false;
            p2.HasSubmittedDeployment = false;
            p1.PendingDeployment = null;
            p2.PendingDeployment = null;
            p1.MapReady = false;
            p2.MapReady = false;

            DateTime deploymentPhaseStartUtc = DateTime.UtcNow;
            Debug.Log($"[Timing] [{ms.MatchId}] RunDeploymentPhasePure démarré à {deploymentPhaseStartUtc:O} — attente MapReady (max {MapReadyMaxWaitSeconds}s) puis déploiement (max {DeploymentSeconds}s).");

            // 2026-09-06 : sans ce signal de vie périodique, un vrai Deathmatch/Zone de Contrôle
            // (premier test réel de ce chemin, jamais atteignable avant l'ajout du bouton client) se
            // déconnectait dès que la génération de carte dépassait les 15s de ReceiveTimeout du
            // client, pendant que cette boucle patientait en silence jusqu'à 60s sans rien envoyer.
            const float ServerKeepaliveIntervalSeconds = 15f;
            float keepaliveTimer = 0f;

            // 2026-09-06 : bug plus ancien retrouvé (racine du symptôme "des unités apparaissent sans
            // que j'aie pu les placer", déjà vu et partiellement corrigé le 2026-08-30 — voir
            // 08-known-issues-and-todo.md §13). Avant, cette boucle sortait dès mapWait ≤ 0
            // (60s), QUELLE QUE SOIT la valeur de MapReady, puis démarrait le compte à rebours de
            // déploiement de 45s IDENTIQUE pour les deux joueurs — y compris celui dont la carte
            // n'avait pas fini de charger et dont le dock n'était même pas encore affiché. Ses 45s
            // s'écoulaient donc en partie ou en totalité avant même qu'il ait pu voir son écran de
            // placement, aboutissant à un repli automatique (AutoDeployTeamFallbackPure) qu'il
            // n'avait aucune chance d'éviter. Corrigé : chaque joueur reçoit maintenant ses propres
            // 45 secondes à partir du moment où SA PROPRE carte est prête (p1ReadyAtUtc/
            // p2ReadyAtUtc), jamais à partir d'un instant partagé arbitraire. Un joueur dont la carte
            // n'est jamais prête dans les 60s est traité à part : repli automatique immédiat pour lui
            // seul, sans faire attendre ni pénaliser l'autre.
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

                keepaliveTimer += Time.deltaTime;
                if (keepaliveTimer >= ServerKeepaliveIntervalSeconds)
                {
                    keepaliveTimer = 0f;
                    if (!p1.IsDisconnected) p1.Send(new NetMessage { type = "heartbeat" });
                    if (!p2.IsDisconnected) p2.Send(new NetMessage { type = "heartbeat" });
                }

                mapWait -= Time.deltaTime;
                yield return null;
            }

            double mapReadyElapsedSec = (DateTime.UtcNow - deploymentPhaseStartUtc).TotalSeconds;
            Debug.Log($"[Timing] [{ms.MatchId}] MapReady terminé après {mapReadyElapsedSec:F1}s réelles (p1.MapReady={p1.MapReady}, p2.MapReady={p2.MapReady}).");

            DateTime deploymentCountdownStartUtc = DateTime.UtcNow;
            // Un joueur dont MapReady n'est toujours pas passé à vrai après les 60s de filet de
            // sécurité n'aura jamais son dock affiché : son côté est résolu tout de suite (repli
            // automatique), il n'a pas de compte à rebours à attendre.
            bool p1Done = p1ReadyAtUtc == null;
            bool p2Done = p2ReadyAtUtc == null;
            // Compte à rebours envoyé pendant le DÉPLOIEMENT aussi (correctif 2026-09-03) : "turn_timer"
            // n'était émis que pendant les phases de planification, si bien que le joueur n'avait aucune
            // idée qu'une échéance de 45s existait. Passé ce délai, son placement en cours était jeté et
            // le serveur lui déployait d'office une escouade standard — d'où le symptôme "des unités
            // apparaissent d'un coup sans que j'aie pu placer les miennes".
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
            Debug.Log($"[Timing] [{ms.MatchId}] Compte à rebours de déploiement terminé après {deploymentElapsedSec:F1}s réelles (attendu {DeploymentSeconds}s).");

            var team1Units = ResolveDeploymentPure(ms, p1, 1);
            var team2Units = ResolveDeploymentPure(ms, p2, 2);
            Debug.Log($"[Trajectoire] [{ms.MatchId}] Déploiement résolu : équipe1={team1Units.Count} unité(s), équipe2={team2Units.Count} unité(s).");

            var team1Barricades = team1Units.Where(u => u.unit_type == (int)UnitSpawnerUI.UnitType.BarricadeRoutiere);
            var team2Barricades = team2Units.Where(u => u.unit_type == (int)UnitSpawnerUI.UnitType.BarricadeRoutiere);
            if (!p1.IsDisconnected) p1.Send(new NetMessage { type = "deployment_result", deployed_units = team1Units.Concat(team2Barricades).ToArray() });
            if (!p2.IsDisconnected) p2.Send(new NetMessage { type = "deployment_result", deployed_units = team2Units.Concat(team1Barricades).ToArray() });
        }

        /// <summary>Équivalent pur de ResolveDeployment — place directement dans ms.World (units ou
        /// barricades), sans jamais passer par UnitSpawnerUI.SpawnUnitAt (qui instancierait de vrais
        /// prefabs, exactement ce que ce chantier élimine pour ces deux modes).</summary>
        private List<DeployedUnit> ResolveDeploymentPure(MatchState ms, PlayerConnection conn, int team)
        {
            bool valid = conn.HasSubmittedDeployment && IsRosterValid(conn.PendingDeployment);
            var placements = new List<UnitPlacement>();

            if (valid)
            {
                foreach (var p in conn.PendingDeployment)
                {
                    Vector3 clamped = ClampToDeploymentZone(new Vector3(p.x, p.y, p.z), team);
                    placements.Add(new UnitPlacement { unit_type = p.unit_type, x = clamped.x, y = clamped.y, z = clamped.z });
                }
            }
            else
            {
                placements.AddRange(AutoDeployTeamFallbackPure(ms, team));
            }

            var placed = new List<DeployedUnit>();
            foreach (var p in placements)
            {
                var type = (UnitSpawnerUI.UnitType)p.unit_type;
                if (type == UnitSpawnerUI.UnitType.BarricadeRoutiere)
                {
                    string id = $"Barricade_{team}_{ms.NextUnitSequence++}";
                    // Toujours Quaternion.identity côté original (UnitSpawnerUI.SpawnUnitAt,
                    // BarricadeRoutiere) : l'orientation n'est jamais transmise par
                    // "submit_deployment" (UnitPlacement n'a pas de champ rotation), donc toujours
                    // alignée sur l'axe X monde ici aussi.
                    // Recalage au sol AVANT usage (2026-09-02) — jusqu'ici seule PlaceCombatUnitPure
                    // (unités de combat) passait par MatchGeometry.FindGroundLevelInGrid ; une
                    // barricade manuelle utilisait p.x/p.z BRUTS, ce qui pouvait la poser en pleine
                    // empreinte de bâtiment (même bug que UnitSpawnerUI.SpawnUnitAt côté moteur
                    // "vivant" de la Conquête, voir son commentaire — corrigé là aussi le même jour).
                    Vector2 grounded = MatchGeometry.FindGroundLevelInGrid(ms.World.grid, new Vector2(p.x, p.z), 5f);
                    Vector2 dir = Vector2.right;
                    float halfWidth = MatchState.BarricadeHalfWidthMeters;
                    ms.World.barricades.Add(new Barricade
                    {
                        p1 = grounded - dir * halfWidth,
                        p2 = grounded + dir * halfWidth,
                        ownerTeam = team,
                        hp = 250f // RoadBarrier.cs:17-18 — PV par défaut exacts
                    });
                    placed.Add(new DeployedUnit { unit_id = id, unit_type = p.unit_type, team_id = team, x = grounded.x, y = GroundLevelY, z = grounded.y });
                }
                else
                {
                    placed.Add(PlaceCombatUnitPure(ms, type, new Vector3(p.x, p.y, p.z), team));
                }
            }
            return placed;
        }

        // Hauteur Y cosmétique constante pour une unité au sol — le terrain d'une tuile est plat
        // (seuls les bâtiments ont du relief, voir TacticalUnit.zStrata), cohérente avec les valeurs
        // déjà observées en production via le vrai NavMesh (ex. y=0.25).
        private const float GroundLevelY = 0.25f;

        /// <summary>Construit une TacticalUnit directement (pas de UnitAI réelle) et l'ajoute à
        /// ms.World.units — repositionnement au sol via MatchGeometry.FindGroundLevelInGrid (grille de
        /// marche déjà en cache pour CETTE partie, voir MatchState.World.grid) au lieu du vrai NavMesh
        /// vivant (UnitSpawnerUI.FindGroundLevelNavPoint) — 2026-08-30, "des milliers de cartes" :
        /// nécessaire dès qu'une AUTRE tuile que celle de cette partie peut être chargée en scène au
        /// même instant (voir MatchGeometry). Appliqué qu'il s'agisse d'un placement manuel ou d'un
        /// repli automatique, exactement comme l'original.</summary>
        private DeployedUnit PlaceCombatUnitPure(MatchState ms, UnitSpawnerUI.UnitType type, Vector3 rawPos, int team)
        {
            Vector2 grounded = MatchGeometry.FindGroundLevelInGrid(ms.World.grid, new Vector2(rawPos.x, rawPos.z), 5f);
            UnitTypeStats.Get(type, out int health, out float porteeDetection, out int weaponDamage, out float weaponCooldownSeconds, out bool isMortar, out bool isTank);

            string id = $"{UnitTypeStats.TypeName(type)}_{team}_{ms.NextUnitSequence++}";
            var tu = new TacticalUnit
            {
                id = id,
                team = team,
                position = grounded,
                zStrata = ZStrata.Sol,
                health = health,
                isDead = false,
                spottingRange = isMortar ? 25f : 35f, // seuil "toit" non pertinent au déploiement (zStrata=Sol)
                movementBudget = UnitTypeStats.MovementBudget(type), // source unique, partagée avec le moteur vivant
                engagementRange = porteeDetection,
                weaponDamage = weaponDamage,
                weaponCooldownSeconds = weaponCooldownSeconds,
                isMortar = isMortar,
                isTank = isTank,
            };
            ms.World.units.Add(tu);
            ms.UnitTypeById[id] = (int)type;
            ms.CurrentYById[id] = GroundLevelY;

            return new DeployedUnit { unit_id = id, unit_type = (int)type, team_id = team, x = grounded.x, y = GroundLevelY, z = grounded.y };
        }

        /// <summary>Équivalent pur de UnitSpawnerUI.AutoDeployTeamFallback (mêmes points d'ancrage et
        /// décalages exacts, extraInfantry jamais utilisé ici — réservé à la Conquête) — renvoie des
        /// UnitPlacement plutôt que de spawner directement, pour repasser par le même chemin
        /// (PlaceCombatUnitPure, avec son repositionnement au sol) qu'un placement manuel.</summary>
        private static List<UnitPlacement> AutoDeployTeamFallbackPure(MatchState ms, int team)
        {
            Vector2 anchor2D = MatchGeometry.FindGroundLevelInGrid(ms.World.grid, team == 1 ? new Vector2(-25f, -25f) : new Vector2(25f, 25f), 40f);
            Vector3 anchor = new Vector3(anchor2D.x, 0f, anchor2D.y);
            var list = new List<UnitPlacement>();

            void Add(UnitSpawnerUI.UnitType type, Vector3 offset)
            {
                Vector3 pos = anchor + offset;
                list.Add(new UnitPlacement { unit_type = (int)type, x = pos.x, y = pos.y, z = pos.z });
            }

            if (team == 1)
            {
                Add(UnitSpawnerUI.UnitType.Fantassin, new Vector3(-2f, 0, -2f));
                Add(UnitSpawnerUI.UnitType.Fantassin, new Vector3(2f, 0, 2f));
                Add(UnitSpawnerUI.UnitType.CharLeopard, new Vector3(5f, 0, -3f));
                Add(UnitSpawnerUI.UnitType.Mortier, new Vector3(-5f, 0, -4f));
            }
            else
            {
                Add(UnitSpawnerUI.UnitType.Fantassin, new Vector3(-3f, 0, 3f));
                Add(UnitSpawnerUI.UnitType.Fantassin, new Vector3(3f, 0, -3f));
                Add(UnitSpawnerUI.UnitType.CharLeopard, new Vector3(6f, 0, 4f));
                Add(UnitSpawnerUI.UnitType.Mortier, new Vector3(-6f, 0, 5f));
            }
            return list;
        }
    }
}
