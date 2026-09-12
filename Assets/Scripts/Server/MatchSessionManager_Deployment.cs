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
        // Publics depuis le 2026-09-08, même raison que Team1/2DeploymentZoneCenter ci-dessus :
        // UnitSpawnerUI en a besoin pour afficher le budget réel PENDANT le déploiement (voir
        // GetTeamDeploymentPointCost) au lieu de le laisser invisible jusqu'au rejet côté serveur.
        public const int MaxDeployedCombatUnits = 6;
        public const int MaxDeployedBarricades = 8;
        // 2026-09-12 (demande explicite) : sans plafond dédié, un camp pouvait aligner jusqu'à 4
        // Mortiers (limité seulement par CombatPointBudget=8, coût 2 chacun) — même règle appliquée
        // côté client (voir UnitSpawnerUI.MaxMortarsPerTeam) pour un message immédiat, mais l'AUTORITÉ
        // reste ici : un client modifié qui soumettrait `submit_deployment` directement sans jamais
        // passer par le dock doit être bloqué pareil.
        public const int MaxMortarsPerTeam = 2;
        // Budget en points (voir UnitTypeStats.DeploymentCost) : plafonne la PUISSANCE
        // totale déployée, pas seulement le nombre d'unités — sans ça, déployer le nombre max
        // d'unités les plus lourdes (CharLeopard) était toujours strictement supérieur à toute
        // composition mixte, tuant toute variété tactique (voir rapport d'audit jouabilité, défaut
        // bloquant #1). 8 points permet par ex. 2 CharLeopard + 2 Fantassin, ou 4 VehiculeCanon, ou
        // 1 CharLeopard + 1 Mortier + 1 VehiculeCanon + 1 Fantassin — mais jamais 4 CharLeopard (12).
        public const int CombatPointBudget = 8;

        /// <summary>Garde EXACTEMENT les placements que le joueur a lui-même choisis (même type, même
        /// position) qui tiennent dans le budget — dans l'ORDRE de soumission — et ne rejette qu'un
        /// placement individuellement invalide (type hors énum, coordonnée NaN/Infinity) ou celui qui
        /// ferait dépasser un plafond. <paramref name="anyDropped"/> est vrai si au moins un placement
        /// a été écarté.
        ///
        /// POURQUOI CE N'EST PLUS UN "TOUT OU RIEN" (2026-09-08). L'ancienne version
        /// (`IsRosterValid`, booléenne) rejetait la soumission ENTIÈRE au moindre dépassement, et
        /// `ResolveDeployment(Pure)` remplaçait alors TOUT le camp par `AutoDeployTeamFallback`(Pure) —
        /// une escouade FIXE (2 Fantassin + 1 CharLeopard + 1 Mortier) à des positions FIXES ancrées
        /// sur un coin de la carte, sans le moindre rapport avec ce que le joueur avait réellement
        /// tapé. Or le dock de déploiement (`UnitSpawnerUI`/`MultiplayerMatchController.
        /// OpenDeploymentDock`) ne plafonne QUE le nombre d'unités (`maxUnitsPerTeam = 4`, sous le
        /// vrai plafond serveur de 6) — il n'a JAMAIS connu ni affiché le budget en points
        /// (`CombatPointBudget = 8`, voir `UnitTypeStats.DeploymentCost`). Un joueur qui privilégiait
        /// des unités lourdes (2 CharLeopard + 1 Mortier + 1 Fantassin = 6+2+1 = 9 points, un choix
        /// parfaitement raisonnable et sous la limite de 4 unités affichée) voyait donc TOUT son
        /// déploiement jeté et remplacé par l'escouade fixe — vécu comme "mes unités se sont mises
        /// toutes seules ailleurs" et "mes mortiers ont disparu", sans le moindre message d'erreur.
        /// Rapporté par le joueur (2026-09-08) : "toutes les unités se mettent tout seules dans des
        /// endroits bizarres après déploiement alors que le joueur avait choisi d'autres endroits".
        /// Le repli fixe reste utilisé, mais seulement si RIEN du tout ne peut être conservé (aucune
        /// soumission, ou soumission entièrement malformée) — jamais pour un simple dépassement de
        /// budget sur une soumission par ailleurs légitime.</summary>
        private static List<UnitPlacement> FilterRosterToBudget(UnitPlacement[] placements, out bool anyDropped)
        {
            var kept = new List<UnitPlacement>();
            anyDropped = false;
            if (placements == null) return kept;

            int combatCount = 0, barricadeCount = 0, mortarCount = 0, totalCost = 0;
            foreach (var p in placements)
            {
                if (!Enum.IsDefined(typeof(UnitSpawnerUI.UnitType), p.unit_type)) { anyDropped = true; continue; }
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) { anyDropped = true; continue; }
                if (float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)) { anyDropped = true; continue; }

                var type = (UnitSpawnerUI.UnitType)p.unit_type;
                bool isBarricade = type == UnitSpawnerUI.UnitType.BarricadeRoutiere;
                bool isMortar = type == UnitSpawnerUI.UnitType.Mortier;
                int cost = UnitTypeStats.DeploymentCost(type);

                bool fitsCount = isBarricade ? barricadeCount + 1 <= MaxDeployedBarricades : combatCount + 1 <= MaxDeployedCombatUnits;
                bool fitsMortarCap = !isMortar || mortarCount + 1 <= MaxMortarsPerTeam;
                bool fitsBudget = totalCost + cost <= CombatPointBudget;
                if (!fitsCount || !fitsMortarCap || !fitsBudget) { anyDropped = true; continue; }

                if (isBarricade) barricadeCount++; else combatCount++;
                if (isMortar) mortarCount++;
                totalCost += cost;
                kept.Add(p);
            }
            return kept;
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

        /// <summary>ÉQUITÉ GÉOMÉTRIQUE, 2ème étage (2026-09-12) — voir MatchState.AuthoritativeCityHash
        /// pour le contexte complet. Compare le hash que CE client vient de calculer sur SA propre
        /// génération à la référence figée pour cette partie ; en cas d'écart, envoie la structure de
        /// bâtiments AUTORITAIRE du serveur (jamais un différentiel partiel) pour que ce client
        /// reconstruise sa ville à l'identique avant que le déploiement ne s'ouvre réellement.
        /// Journalisé dans TOUS les cas (pas seulement l'échec) : un écart qui ne se reproduit jamais
        /// dans les logs serait aussi suspect qu'un écart qui apparaît sans cesse.</summary>
        private void HandleCityVerify(MatchState ms, PlayerConnection conn, NetMessage msg)
        {
            bool matches = msg.city_building_hash == ms.AuthoritativeCityHash;
            Debug.Log($"[CityVerify] [{ms.MatchId}] {conn.Username} (équipe {conn.TeamId}) : " +
                      $"hash client={msg.city_building_hash} ({msg.city_building_count} bâtiments), " +
                      $"hash serveur={ms.AuthoritativeCityHash} ({ms.World.buildings.Count} bâtiments) -> " +
                      $"{(matches ? "IDENTIQUE" : "DIVERGENT")}.");

            if (matches)
            {
                conn.Send(new NetMessage { type = "city_verify_result", success = true });
                return;
            }

            Debug.LogWarning($"[CityVerify] [{ms.MatchId}] Géométrie divergente pour {conn.Username} (équipe {conn.TeamId}) — " +
                              $"envoi de la structure autoritaire ({ms.World.buildings.Count} bâtiments) pour resynchronisation.");
            conn.Send(new NetMessage
            {
                type = "city_verify_result",
                success = false,
                city_buildings = SerializeBuildingsForNetwork(ms.World.buildings)
            });
        }

        /// <summary>Copie intégrale (jamais un différentiel) de <paramref name="buildings"/> vers le
        /// format réseau — voir NetMessage.BuildingGeometryDto pour le format exact et pourquoi la
        /// hauteur de fenêtre (Y) n'est volontairement pas transmise.</summary>
        private static BuildingGeometryDto[] SerializeBuildingsForNetwork(List<TacticalBuilding> buildings)
        {
            return buildings.Select(b => new BuildingGeometryDto
            {
                id = b.id,
                height = b.height,
                footprint = b.footprint.Select(p => new Vector2Data { x = p.x, y = p.y }).ToArray(),
                doors = b.doors.Select(d => new DoorGeometryDto
                {
                    position = new Vector2Data { x = d.position.x, y = d.position.y },
                    entry_direction = new Vector2Data { x = d.entryDirection.x, y = d.entryDirection.y },
                    width = d.width
                }).ToArray(),
                windows = b.windows.Select(w => new WindowGeometryDto
                {
                    id = w.id,
                    position = new Vector2Data { x = w.position.x, y = w.position.y },
                    outward_normal = new Vector2Data { x = w.outwardNormal.x, y = w.outwardNormal.y },
                    floor_level = w.floorLevel
                }).ToArray()
            }).ToArray();
        }

        private static int InferUnitType(UnitAI u) => (int)InferUnitTypeEnum(u);

        /// <summary>Type d'unité déduit des drapeaux de l'UnitAI. Version typée, pour pouvoir
        /// interroger UnitTypeStats (voir movementBudget dans BuildTacticalUnit).</summary>
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

            // 2026-09-06 : sans un signal de vie périodique, un vrai Deathmatch/Zone de Contrôle
            // (premier test réel de ce chemin, jamais atteignable avant l'ajout du bouton client) se
            // déconnectait dès que la génération de carte dépassait le ReceiveTimeout du client,
            // pendant que cette boucle patientait en silence sans rien envoyer.
            // 2026-09-07 : ce signal de vie, alors LOCAL à cette phase, a été remplacé par un
            // mécanisme central au niveau de la connexion elle-même (PlayerConnection.PumpKeepalives,
            // appelé par MatchSessionManager.Update) — précisément parce qu'un keepalive par phase
            // oblige chaque nouvelle phase à y penser, et que trois d'entre elles ne l'avaient pas
            // fait (file d'attente, génération de tuile avant match_found, Conquête). Il n'y a donc
            // plus rien à envoyer ici : la connexion s'en charge toute seule.

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
                DrainMessages(p1, 0, ms);
                DrainMessages(p2, 0, ms);
                if (p1.IsDisconnected && p2.IsDisconnected) yield break;

                if (p1ReadyAtUtc == null && p1.MapReady) p1ReadyAtUtc = DateTime.UtcNow;
                if (p2ReadyAtUtc == null && p2.MapReady) p2ReadyAtUtc = DateTime.UtcNow;

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
                DrainMessages(p1, 0, ms);
                DrainMessages(p2, 0, ms);
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

            var team1Units = ResolveDeploymentPure(ms, p1, 1, out bool team1Trimmed);
            var team2Units = ResolveDeploymentPure(ms, p2, 2, out bool team2Trimmed);
            Debug.Log($"[Trajectoire] [{ms.MatchId}] Déploiement résolu : équipe1={team1Units.Count} unité(s), équipe2={team2Units.Count} unité(s).");

            var team1Barricades = team1Units.Where(u => u.unit_type == (int)UnitSpawnerUI.UnitType.BarricadeRoutiere);
            var team2Barricades = team2Units.Where(u => u.unit_type == (int)UnitSpawnerUI.UnitType.BarricadeRoutiere);
            // "roster_trimmed" : voir FilterRosterToBudget — au moins un placement soumis par CE
            // joueur dépassait le budget et a été écarté individuellement, le reste est conservé.
            if (!p1.IsDisconnected) p1.Send(new NetMessage { type = "deployment_result", deployed_units = team1Units.Concat(team2Barricades).ToArray(), reason = team1Trimmed ? "roster_trimmed" : null });
            if (!p2.IsDisconnected) p2.Send(new NetMessage { type = "deployment_result", deployed_units = team2Units.Concat(team1Barricades).ToArray(), reason = team2Trimmed ? "roster_trimmed" : null });
        }

        /// <summary>Équivalent pur de ResolveDeployment — place directement dans ms.World (units ou
        /// barricades), sans jamais passer par UnitSpawnerUI.SpawnUnitAt (qui instancierait de vrais
        /// prefabs, exactement ce que ce chantier élimine pour ces deux modes).</summary>
        private List<DeployedUnit> ResolveDeploymentPure(MatchState ms, PlayerConnection conn, int team, out bool rosterTrimmed)
        {
            rosterTrimmed = false;
            var kept = conn.HasSubmittedDeployment
                ? FilterRosterToBudget(conn.PendingDeployment, out rosterTrimmed)
                : new List<UnitPlacement>();
            // Voir ResolveDeployment (chemin "vivant") pour le pourquoi de cette ligne.
            rosterTrimmed &= kept.Count > 0;
            var placements = new List<UnitPlacement>();

            if (kept.Count > 0)
            {
                foreach (var p in kept)
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
