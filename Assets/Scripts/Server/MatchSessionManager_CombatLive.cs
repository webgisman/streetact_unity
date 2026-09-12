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
        private string currentMatchMode = "deathmatch";

        private IEnumerator RunPlanningPhase(PlayerConnection p1, PlayerConnection p2, int turnNumber)
        {
            p1.HasSubmittedThisTurn = false;
            p2.HasSubmittedThisTurn = false;

            float remaining = PlanningSeconds;
            int lastTick = -1;

            // Instrumentation temps réel (voir RunDeploymentPhase) : horodatage UTC réel au début du
            // tour et à chaque envoi de "turn_timer", pour pouvoir vérifier depuis un vrai log que le
            // compte à rebours annoncé aux clients correspond bien au temps RÉELLEMENT écoulé côté
            // serveur — pas seulement à une valeur théorique qui pourrait dériver.
            DateTime planningStartUtc = DateTime.UtcNow;
            Debug.Log($"[Timing] Tour {turnNumber} — RunPlanningPhase démarré à {planningStartUtc:O} (max {PlanningSeconds}s).");

            while (remaining > 0f && !(p1.HasSubmittedThisTurn && p2.HasSubmittedThisTurn))
            {
                DrainMessages(p1, turnNumber);
                DrainMessages(p2, turnNumber);

                if (p1.IsDisconnected && p2.IsDisconnected) yield break;

                int secondsLeft = Mathf.CeilToInt(remaining);
                if (secondsLeft != lastTick)
                {
                    lastTick = secondsLeft;
                    double realElapsed = (DateTime.UtcNow - planningStartUtc).TotalSeconds;
                    Debug.Log($"[Timing] Tour {turnNumber} turn_timer: secondsLeft={secondsLeft} (théorique) après {realElapsed:F1}s réelles écoulées (théorique attendu ~{PlanningSeconds - secondsLeft}s).");
                    var tick = new NetMessage { type = "turn_timer", seconds_remaining = secondsLeft };
                    if (!p1.IsDisconnected) p1.Send(tick);
                    if (!p2.IsDisconnected) p2.Send(tick);
                }

                remaining -= Time.deltaTime;
                yield return null;
            }

            double planningElapsedSec = (DateTime.UtcNow - planningStartUtc).TotalSeconds;
            bool planningTimedOut = !(p1.HasSubmittedThisTurn && p2.HasSubmittedThisTurn);
            Debug.Log($"[Timing] Tour {turnNumber} — RunPlanningPhase terminé après {planningElapsedSec:F1}s réelles (attendu max {PlanningSeconds}s) — p1.HasSubmittedThisTurn={p1.HasSubmittedThisTurn}, p2.HasSubmittedThisTurn={p2.HasSubmittedThisTurn}, expiré par timeout={planningTimedOut}.");

            ApplyForPlayer(p1, p2);
            ApplyForPlayer(p2, p1);
        }

        /// <summary><paramref name="msForCityVerify"/> est optionnel (null partout sauf
        /// RunDeploymentPhasePure) : seule cette phase a besoin de répondre à "city_verify", et lui
        /// seul dispose d'un MatchState sous la main (RunPlanningPhase/RunConquestPlanningPhase ne
        /// travaillent qu'avec des PlayerConnection, architecture Live plus ancienne) — un paramètre
        /// optionnel évite de changer la signature des 9 autres appels existants pour une seule
        /// phase concernée.</summary>
        private void DrainMessages(PlayerConnection conn, int turnNumber, MatchState msForCityVerify = null)
        {
            while (conn.TryDequeueMessage(out NetMessage msg))
            {
                if (msg.type == "submit_turn" && msg.turn_number == turnNumber)
                {
                    conn.PendingOrders = msg.orders ?? Array.Empty<UnitOrder>();
                    conn.HasSubmittedThisTurn = true;
                }
                else if (msg.type == "heartbeat")
                {
                    conn.LastHeartbeat = DateTime.UtcNow;
                }
                else if (msg.type == "submit_deployment")
                {
                    conn.PendingDeployment = msg.placements ?? Array.Empty<UnitPlacement>();
                    conn.HasSubmittedDeployment = true;
                }
                else if (msg.type == "deployment_ready")
                {
                    conn.MapReady = true;
                }
                else if (msg.type == "city_verify" && msForCityVerify != null)
                {
                    HandleCityVerify(msForCityVerify, conn, msg);
                }
            }
        }

        private void ApplyForPlayer(PlayerConnection conn, PlayerConnection opponent)
        {
            var myUnits = UnitAI.AllLivingUnits.Where(u => u.teamID == conn.TeamId).ToList();
            bool shouldGhost = conn.IsDisconnected || !conn.HasSubmittedThisTurn;

            if (shouldGhost)
            {
                // Substitut IA (réutilise TacticalAIPlanner, exactement comme un ennemi Solo — voir
                // UnitAI.isGhosted) pour un joueur absent/déconnecté/en retard : sans ça, ses unités
                // restaient des mannequins totalement passifs pour le reste du match dès la première
                // absence, ce qui rendait la fin de partie triviale et peu satisfaisante pour
                // l'adversaire resté en ligne (voir rapport d'audit §1.3/§D).
                foreach (var u in myUnits)
                {
                    u.isGhosted = true;
                    u.PlanifierTourIA();
                }

                if (!opponent.IsDisconnected)
                {
                    opponent.Send(new NetMessage
                    {
                        type = "opponent_ghosted",
                        team_id = conn.TeamId,
                        reason = conn.IsDisconnected ? "disconnected" : "timeout"
                    });
                }
            }
            else
            {
                // Le joueur est de retour : ses unités reprennent immédiatement le contrôle humain,
                // isGhosted ne doit pas rester collé à true indéfiniment (voir UnitAI.isGhosted).
                foreach (var u in myUnits) u.isGhosted = false;
                ApplyOrdersToUnits(myUnits, conn.PendingOrders);
            }
        }

        private void ApplyOrdersToUnits(List<UnitAI> myUnits, UnitOrder[] orders)
        {
            if (orders == null) return;

            foreach (var order in orders)
            {
                UnitAI unit = myUnits.FirstOrDefault(u => u.gameObject.name == order.unit_id);
                if (unit == null) continue;

                unit.ClearTacticalPath();
                if (order.path == null) continue;
                // Un client modifié pourrait soumettre un chemin de milliers de points (toujours
                // sous la limite de 8 Mo du protocole) pour faire tourner la simulation de
                // mouvement en boucle inutilement, ou des coordonnées NaN/Infinity/une valeur
                // d'action hors de l'enum pour déclencher un comportement indéfini plus loin dans
                // TacticalPathManager/NavMesh — on rejette silencieusement ce qui est invalide
                // plutôt que de faire confiance au client.
                if (order.path.Length > MaxOrderPathNodes) continue;

                foreach (var node in order.path)
                {
                    if (float.IsNaN(node.x) || float.IsNaN(node.y) || float.IsNaN(node.z)) continue;
                    if (float.IsInfinity(node.x) || float.IsInfinity(node.y) || float.IsInfinity(node.z)) continue;
                    if (!Enum.IsDefined(typeof(TacticalPathManager.NodeAction), node.action)) continue;

                    unit.AddTacticalNode(new TacticalPathManager.TacticalNode
                    {
                        position = new Vector3(node.x, node.y, node.z),
                        action = (TacticalPathManager.NodeAction)node.action
                    });
                }
            }
        }

        // =====================================================================
        // Résolution de tour — moteur logique déterministe (Novgov.TacticalCore), 2026-08-30.
        // REMPLACE ENTIÈREMENT l'ancienne méthode (simulation Unity réelle : unit.ExecuterOrdres()
        // pilotant NavMeshAgent + Physics.RaycastAll pendant plusieurs secondes de temps réel,
        // échantillonnée en Snapshots). Le résultat officiel (positions, dégâts, morts, murs
        // détruits) est maintenant calculé UNE FOIS, instantanément, par TacticalResolver.Resolve()
        // — une fonction pure sans dépendance à un moteur physique, donc garantie déterministe
        // sur n'importe quel appareil. Le NavMeshAgent/PhysX ne servent plus qu'au rendu visuel
        // solo (mode Hors-Ligne) : ils n'influencent plus jamais un résultat multijoueur.
        //
        // Voir Assets/Scripts/TacticalCore/ pour le moteur lui-même, et la discussion
        // d'architecture (session du 2026-08-30) pour le pourquoi de ce choix.
        // =====================================================================
        private const int TickDurationMs = 250; // durée visuelle d'un pas de résolution (1m de déplacement), pour le rythme de lecture côté client

        private IEnumerator RunExecutionPhase(int turnNumber, PlayerConnection p1, PlayerConnection p2)
        {
            // Instrumentation temps réel + trajectoire (2026-08-30, test grandeur nature) : horodatage
            // UTC réel du début de la résolution (le calcul lui-même est une fonction pure, donc quasi
            // instantané — utile pour confirmer que ce n'est PAS là que le temps "de checkpoint" se
            // perd, contrairement aux phases de planification/déploiement qui, elles, attendent
            // volontairement un vrai timer).
            DateTime executionStartUtc = DateTime.UtcNow;

            var allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude).Where(u => !u.isDead).ToList();
            // Photo stable de BuildingStructure.AllBuildings au même instant que
            // TacticalGridBuilder.BuildFromScene() (qui énumère cette MÊME liste statique) : sert de
            // table id<->référence immunisée contre un retrait en cours de route (un bâtiment
            // détruit PENDANT cette méthode est retiré de la liste VIVANTE par DestructibleEnvironment,
            // ce qui décalerait les index restants si on relisait la liste statique après coup).
            var buildingsSnapshot = new List<BuildingStructure>(BuildingStructure.AllBuildings);
            var buildingIndex = new Dictionary<BuildingStructure, int>();
            for (int i = 0; i < buildingsSnapshot.Count; i++) buildingIndex[buildingsSnapshot[i]] = i;

            TacticalWorldState worldState = TacticalGridBuilder.BuildFromScene();
            double gridBuildMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds;
            Debug.Log($"[Timing] Tour {turnNumber} : TacticalGridBuilder.BuildFromScene() en {gridBuildMs:F1}ms réelles ({worldState.buildings.Count} bâtiment(s), {worldState.wallSegments.Count} mur(s), {worldState.barricades.Count} barricade(s)).");
            var originalBuildingHealth = worldState.buildings.ToDictionary(b => b.id, b => b.health);

            var unitById = new Dictionary<string, UnitAI>();
            // Référence RÉELLE de la fenêtre visée par un ordre GarnisonFenetre, par unité — la
            // normale de fenêtre (float2) transitant par TacticalCore ne suffit pas à elle seule à
            // retrouver quel BuildingStructure.BuildingWindow occuper pour de vrai (deux fenêtres
            // symétriques peuvent partager la même normale) : voir l'application après résolution.
            var pendingWindowByUnitId = new Dictionary<string, BuildingStructure.BuildingWindow>();
            var mortarStrikes = new List<Vector2>();
            var ordersTeam1 = new List<UnitOrders>();
            var ordersTeam2 = new List<UnitOrders>();

            foreach (var unit in allUnits)
            {
                string id = unit.gameObject.name;
                unitById[id] = unit;
                worldState.units.Add(BuildTacticalUnit(unit, buildingIndex));

                UnitOrders orders = BuildUnitOrders(unit, mortarStrikes, buildingIndex, pendingWindowByUnitId);
                if (orders.checkpoints.Count > 0)
                {
                    (unit.teamID == 1 ? ordersTeam1 : ordersTeam2).Add(orders);
                }
            }

            // Trajectoires envoyées au résolveur pour ce tour — vérifie depuis les logs que chaque
            // unité avec un ordre a bien un chemin de la bonne longueur (voir MaxOrderPathNodes côté
            // validation réseau) avant même de lancer le calcul.
            Debug.Log($"[Trajectoire] Tour {turnNumber} : équipe1={ordersTeam1.Count} unité(s) avec ordres ({string.Join(", ", ordersTeam1.Select(o => $"{o.unitId}:{o.checkpoints.Count}pt"))}), équipe2={ordersTeam2.Count} unité(s) avec ordres ({string.Join(", ", ordersTeam2.Select(o => $"{o.unitId}:{o.checkpoints.Count}pt"))}), {mortarStrikes.Count} frappe(s) de mortier.");

            double preResolveMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds;
            List<TacticalEvent> tacticalEvents = TacticalResolver.Resolve(worldState, ordersTeam1, ordersTeam2, mortarStrikes);
            double resolveOnlyMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds - preResolveMs;

            // Décomposition du temps réel (2026-08-30, test grandeur nature — la mesure globale
            // précédente ("2s pour une résolution soi-disant instantanée") mélangeait la construction
            // de la géométrie ET le calcul pur ; on isole maintenant les deux pour savoir lequel coûte
            // réellement cher.
            Debug.Log($"[Timing] Tour {turnNumber} : préparation (grille+ordres) {preResolveMs:F1}ms réelles, TacticalResolver.Resolve() SEUL {resolveOnlyMs:F1}ms réelles ({tacticalEvents.Count} événement(s) générés).");

            // Applique le résultat officiel aux VRAIES UnitAI EN MÊME TEMPS que la capture des
            // snapshots (tick par tick), pas en un seul bloc à la toute fin : CaptureZone.Tick()
            // (mode Zone de Contrôle) lit les positions RÉELLES des UnitAI de la scène à chaque
            // pas — les appliquer seulement après coup lui aurait fait lire des positions figées
            // en début de tour pour la totalité de la résolution.
            var snapshots = BuildSnapshotsFromEvents(tacticalEvents, allUnits, unitById);

            // États persistants (garnison/guet/camouflage/bâtiment/hauteur) : contrairement à la
            // position/PV, ils n'influencent pas CaptureZone.Tick(), donc un seul passage en fin
            // de résolution suffit — voir le tableau de persistance du rapport d'audit §8.2
            // (isGuarding/isGarrisoned ne sont JAMAIS remis à false automatiquement).
            foreach (var tu in worldState.units)
            {
                if (!unitById.TryGetValue(tu.id, out UnitAI unit) || unit == null) continue;
                unit.isGuarding = tu.isGuarding;
                unit.isCamouflaged = tu.isCamouflaged;

                // Garnison de fenêtre : occupe/libère la VRAIE fenêtre (état partagé avec
                // BuildingStructure, lu par d'autres systèmes via BuildingWindow.isOccupied) plutôt
                // que de ne poser que le booléen isGarrisoned — sans ça, unit.currentWindow resterait
                // toujours null et la restriction de cône de tir (CanEngageTarget) disparaîtrait
                // silencieusement dès le tour suivant (voir BuildTacticalUnit qui relit currentWindow).
                pendingWindowByUnitId.TryGetValue(tu.id, out var newWindow);
                bool wantsWindow = tu.isGarrisoned && newWindow != null;
                if (unit.currentWindow != null && (!wantsWindow || unit.currentWindow != newWindow))
                {
                    unit.LeaveGarrison(); // libère l'ancienne fenêtre proprement (isGarrisoned repasse à false ici)
                }
                if (wantsWindow && unit.currentWindow != newWindow)
                {
                    BuildingStructure.FindBuildingAt(new Vector3(newWindow.position.x, 0f, newWindow.position.z))?.OccupyWindow(newWindow, unit);
                    unit.currentWindow = newWindow;
                }
                unit.isGarrisoned = tu.isGarrisoned;

                BuildingStructure newBuilding = tu.currentBuildingId >= 0 && tu.currentBuildingId < buildingsSnapshot.Count ? buildingsSnapshot[tu.currentBuildingId] : null;
                if (unit.currentBuilding != newBuilding)
                {
                    unit.currentBuilding?.UnregisterUnitInside(unit);
                    newBuilding?.RegisterUnitInside(unit);
                    unit.currentBuilding = newBuilding;
                }

                if (tu.outputY.HasValue) unit.transform.position = new Vector3(unit.transform.position.x, tu.outputY.Value, unit.transform.position.z);
            }

            // Répercute les dégâts de zone sur les VRAIS composants DestructibleEnvironment (§7 du
            // rapport) — TakeDamage() gère lui-même le passage à isDestroyed et la vraie
            // destruction de scène (colliders désactivés, retrait de AllBuildings, etc.) ; on ne
            // duplique jamais cette logique, seul le calcul du montant de dégâts vient de
            // TacticalResolver.
            foreach (var tb in worldState.buildings)
            {
                float dealt = originalBuildingHealth[tb.id] - tb.health;
                if (dealt <= 0f) continue;
                if (tb.id < 0 || tb.id >= buildingsSnapshot.Count) continue;
                DestructibleEnvironment env = buildingsSnapshot[tb.id]?.GetComponent<DestructibleEnvironment>();
                env?.TakeDamage(dealt);
            }

            foreach (var unit in allUnits)
            {
                unit.ResetOrderState();
            }

            // Brouillard de guerre réseau (2026-08-30, voir rapport d'audit) : jusqu'ici le serveur
            // envoyait l'état COMPLET des deux camps, identique, aux deux clients à chaque tick — un
            // joueur voyait donc la position exacte de chaque unité ennemie en permanence, repérée ou
            // non. On construit maintenant un message DISTINCT par destinataire, filtré sur ce que
            // son camp peut réellement voir à cet instant (ComputeVisibleUnitIds, même calcul que
            // IsUnitSpottedByTeam en mode Solo — voir LineOfSight.CanBeSpotted).
            var unitMetaById = worldState.units.ToDictionary(u => u.id);
            var teamById = unitById.ToDictionary(kv => kv.Key, kv => kv.Value.teamID);

            Snapshot[] snapshotsForTeam1 = FilterSnapshotsForTeam(snapshots, worldState, unitMetaById, teamById, 1);
            Snapshot[] snapshotsForTeam2 = FilterSnapshotsForTeam(snapshots, worldState, unitMetaById, teamById, 2);

            // Résumé de visibilité par tour (brouillard de guerre) : nombre MAX d'ennemis vus sur un
            // seul tick de ce tour, par équipe — volontairement agrégé (pas un log par paire
            // observateur/cible par tick comme le diagnostic temporaire du 2026-08-30 déjà retiré)
            // pour rester lisible sur un vrai test grandeur nature avec beaucoup de tours.
            int maxEnemiesVisibleToTeam1 = snapshotsForTeam1.Length == 0 ? 0 : snapshotsForTeam1.Max(s => s.units.Count(u => u.team_id == 2));
            int maxEnemiesVisibleToTeam2 = snapshotsForTeam2.Length == 0 ? 0 : snapshotsForTeam2.Max(s => s.units.Count(u => u.team_id == 1));
            Debug.Log($"[Vision] Tour {turnNumber} : {snapshots.Count} tick(s) — équipe1 a vu au maximum {maxEnemiesVisibleToTeam1} ennemi(s) sur un tick, équipe2 a vu au maximum {maxEnemiesVisibleToTeam2} ennemi(s) sur un tick.");

            if (!p1.IsDisconnected)
            {
                p1.Send(new NetMessage
                {
                    type = "turn_result",
                    turn_number = turnNumber,
                    snapshot_interval_ms = TickDurationMs,
                    snapshots = snapshotsForTeam1
                });
            }
            if (p2 != null && !p2.IsDisconnected)
            {
                p2.Send(new NetMessage
                {
                    type = "turn_result",
                    turn_number = turnNumber,
                    snapshot_interval_ms = TickDurationMs,
                    snapshots = snapshotsForTeam2
                });
            }

            double totalPhaseElapsedMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds;
            Debug.Log($"[Timing] Tour {turnNumber} : RunExecutionPhase entièrement terminé (calcul + envoi réseau) en {totalPhaseElapsedMs:F1}ms réelles.");

            yield break; // résolution instantanée : plus d'attente en temps réel côté serveur
        }

        /// <summary>Construit, pour UNE équipe destinataire, une copie de <paramref name="fullSnapshots"/>
        /// dont chaque tick ne contient plus que les unités que cette équipe peut réellement voir à
        /// cet instant (voir ComputeVisibleUnitIds) — ne recalcule PAS zone_progress_team1/2 (déjà
        /// figés sur chaque Snapshot source, un seul calcul par tick suffit, voir
        /// CaptureTacticalSnapshot/CaptureZone.Tick appelé une seule fois pendant BuildSnapshotsFromEvents).</summary>
        private static Snapshot[] FilterSnapshotsForTeam(List<Snapshot> fullSnapshots, TacticalWorldState staticGeometry,
            Dictionary<string, TacticalUnit> unitMetaById, Dictionary<string, int> teamById, int observingTeam)
        {
            var result = new Snapshot[fullSnapshots.Count];
            for (int i = 0; i < fullSnapshots.Count; i++)
            {
                Snapshot src = fullSnapshots[i];
                HashSet<string> visibleIds = ComputeVisibleUnitIds(staticGeometry, src.units, unitMetaById, teamById, observingTeam);
                result[i] = new Snapshot
                {
                    t = src.t,
                    zone_progress_team1 = src.zone_progress_team1,
                    zone_progress_team2 = src.zone_progress_team2,
                    units = src.units.Where(u => visibleIds.Contains(u.unit_id)).ToArray()
                };
            }
            return result;
        }

        /// <summary>Ensemble des identifiants d'unités visibles par <paramref name="observingTeam"/> à
        /// UN tick donné : ses propres unités toujours, plus toute unité adverse VIVANTE repérée par
        /// au moins une de ses unités vivantes (LineOfSight.CanBeSpotted — même calcul
        /// qu'IsUnitSpottedByTeam en Solo). Un adversaire mort reste visible (le résultat d'un combat
        /// ne doit pas disparaître). Approximation assumée : la géométrie des murs/barricades utilisée
        /// ici est celle de FIN de tour (staticGeometry, capturée après TacticalResolver.Resolve),
        /// même pour les premiers ticks de la relecture — un mur détruit PENDANT ce tour est donc
        /// traité comme déjà détruit pour TOUTE la relecture, jamais l'inverse : ça ne peut donc que
        /// masquer une unité un peu plus longtemps que la réalité, jamais révéler une position qui
        /// aurait dû rester cachée (l'erreur reste toujours du côté prudent).</summary>
        private static HashSet<string> ComputeVisibleUnitIds(TacticalWorldState staticGeometry, UnitState[] tickUnits,
            Dictionary<string, TacticalUnit> unitMetaById, Dictionary<string, int> teamById, int observingTeam)
        {
            var visible = new HashSet<string>();
            var observers = new List<TacticalUnit>();
            var enemies = new List<(string id, TacticalUnit unit)>();

            foreach (var us in tickUnits)
            {
                if (!teamById.TryGetValue(us.unit_id, out int team)) continue;
                if (!unitMetaById.TryGetValue(us.unit_id, out TacticalUnit meta)) continue;

                var tickUnit = new TacticalUnit
                {
                    id = us.unit_id,
                    team = team,
                    position = new Vector2(us.x, us.z),
                    zStrata = us.y > 2.2f ? ZStrata.Toit : ZStrata.Sol,
                    isDead = us.dead,
                    isCamouflaged = meta.isCamouflaged,
                    isMortar = meta.isMortar,
                };

                if (team == observingTeam)
                {
                    visible.Add(us.unit_id); // sa propre équipe est toujours visible pour elle-même
                    if (!tickUnit.isDead) observers.Add(tickUnit);
                }
                else
                {
                    enemies.Add((us.unit_id, tickUnit));
                }
            }

            // Vérifié en simulant une partie complète contre le serveur en production (2026-08-30,
            // deux escouades convergeant vers le même point) : le repérage se déclenche correctement
            // dès que la ligne de vue est réellement dégagée (voir LineOfSight.CanBeSpotted), et
            // reste correctement bloqué par un mur/bâtiment réel entre les deux camps sinon — ce
            // n'est PAS un bug, c'est le même comportement que le mode Solo (audit du 2026-08-30).
            foreach (var (id, enemy) in enemies)
            {
                if (enemy.isDead) { visible.Add(id); continue; } // un corps ne "réapparaît" pas soudainement, il reste visible
                foreach (var observer in observers)
                {
                    if (LineOfSight.CanBeSpotted(staticGeometry, observer, enemy)) { visible.Add(id); break; }
                }
            }

            return visible;
        }

        /// <summary>Extrait l'état logique (Novgov.TacticalCore.TacticalUnit) d'une UnitAI vivante
        /// — TOUTES les valeurs viennent de l'audit exhaustif du 2026-08-30 (UnitAI_Combat.cs,
        /// UnitAI.cs), rien n'est approximé.</summary>
        private static TacticalUnit BuildTacticalUnit(UnitAI unit, Dictionary<BuildingStructure, int> buildingIndex)
        {
            int buildingId = unit.currentBuilding != null && buildingIndex.TryGetValue(unit.currentBuilding, out int bid) ? bid : -1;

            return new TacticalUnit
            {
                id = unit.gameObject.name,
                team = unit.teamID,
                position = new Vector2(unit.transform.position.x, unit.transform.position.z),
                // Seuil exact de UnitAI.isRooftopSniper (UnitAI.cs:48-53) : Y > 2.2m = toit.
                zStrata = unit.transform.position.y > 2.2f ? ZStrata.Toit : ZStrata.Sol,
                health = unit.health,
                isDead = unit.isDead,
                isCamouflaged = unit.isCamouflaged,
                isGarrisoned = unit.isGarrisoned,
                isGuarding = unit.isGuarding,
                isMortar = unit.isMortar,
                isTank = unit.isTank,
                currentBuildingId = buildingId,
                windowNormal = unit.currentWindow != null ? new Vector2(unit.currentWindow.outwardNormal.x, unit.currentWindow.outwardNormal.z) : (Vector2?)null,
                // Repérage (IsUnitSpottedByTeam, ligne 207) : 55m toit / 25m mortier / 35m sinon.
                spottingRange = unit.transform.position.y > 2.2f ? 55f : (unit.isMortar ? 25f : 35f),
                // Même source unique que le moteur pur (UnitTypeStats.MovementBudget) plutôt que le
                // champ d'instance : les deux moteurs appliquent ainsi rigoureusement la même limite,
                // quel que soit le mode joué.
                movementBudget = UnitTypeStats.MovementBudget(InferUnitTypeEnum(unit)),
                // Engagement personnel (GetVisibleEnemy, ligne 245) : porteeDetection (le bonus
                // toit +20m est appliqué dans LineOfSight.CanEngageTarget selon zStrata, jamais ici).
                engagementRange = unit.porteeDetection,
                // UnitAI_Combat.cs:409 — 15 infanterie / 75 canon-véhicule / 150 char lourd. Le
                // mortier n'a pas de tir direct (0) : ses dégâts viennent uniquement des frappes de
                // zone (mortarStrikes/TirMortier).
                weaponDamage = unit.isMortar ? 0 : (unit.isTank ? (unit.isCanonVehicle ? 75 : 150) : 15),
                // UnitAI_Combat.cs:87-91 — cadence de tir exacte par type.
                weaponCooldownSeconds = unit.isMortar ? 0f : (unit.isTank ? (unit.isCanonVehicle ? 1.4f : 1.8f) : 0.35f),
            };
        }

        // Cône d'Overwatch par défaut (NOUVELLE fonctionnalité, pas dans l'original — voir
        // TacticalTypes.cs en-tête) : 120° de large (cos(60°) = 0.5, précalculé — TacticalCore
        // n'appelle jamais de fonction trigonométrique), portée 30m.
        private const float OverwatchConeCosHalfAngle = 0.5f;
        private const float OverwatchRange = 30f;

        /// <summary>Convertit le chemin tactique déjà posé sur l'UnitAI (unit.tacticalPath, rempli
        /// par ApplyOrdersToUnits depuis les ordres réseau — inchangé) en UnitOrders pour
        /// TacticalResolver — miroir fidèle de chaque NodeAction (rapport d'audit §2.6-2.11).
        /// Simplification assumée : seule la DERNIÈRE action du chemin est appliquée (cas
        /// quasi-systématique en pratique — un ordre = un déplacement puis une posture finale) ;
        /// plusieurs actions à des points intermédiaires différents du même chemin ne sont pas
        /// toutes conservées, seule la dernière l'est.</summary>
        private static UnitOrders BuildUnitOrders(UnitAI unit, List<Vector2> mortarStrikesOut, Dictionary<BuildingStructure, int> buildingIndex, Dictionary<string, BuildingStructure.BuildingWindow> pendingWindowByUnitId)
        {
            var orders = new UnitOrders { unitId = unit.gameObject.name };
            Vector2 previousPos = new Vector2(unit.transform.position.x, unit.transform.position.z);

            foreach (var node in unit.tacticalPath)
            {
                Vector2 pos2D = new Vector2(node.position.x, node.position.z);

                if (node.action == TacticalPathManager.NodeAction.TirMortier)
                {
                    // Validation anti-triche (2026-09-12) : voir le commentaire jumeau dans
                    // BuildUnitOrdersPure (MatchSessionManager_CombatPure.cs) — même règle, même
                    // portée (120m, UnitAI_Combat.cs "isMortar ? 120f"), symétrique entre les deux
                    // moteurs pour ne pas laisser un exploit valide dans l'un et pas l'autre.
                    const float MortarMaxRange = 120f;
                    if (unit.isMortar && Vector2.Distance(previousPos, pos2D) <= MortarMaxRange)
                    {
                        mortarStrikesOut.Add(pos2D);
                    }
                    continue;
                }

                var checkpoint = new PathCheckpoint { position = pos2D };

                switch (node.action)
                {
                    case TacticalPathManager.NodeAction.Guetter:
                    case TacticalPathManager.NodeAction.Embuscade:
                        // NOUVEAU (Overwatch actif) EN PLUS de l'effet original (isGuarding, -50%
                        // dégâts reçus, jamais remis à false automatiquement — rapport §2.10/§8.2).
                        checkpoint.setGuarding = true;
                        checkpoint.enterOverwatchAtEnd = true;
                        checkpoint.overwatchToSet = BuildOverwatchTrigger(pos2D, previousPos, node.action);
                        break;

                    case TacticalPathManager.NodeAction.GuetterPorte:
                        // Garnison de porte (rapport §2.9) : -75% dégâts (comme une fenêtre), mais
                        // SANS restriction de cône (windowNormal reste null). Overwatch (nouveau)
                        // en plus, sur la porte elle-même.
                        checkpoint.setGarrisonDoor = true;
                        checkpoint.enterOverwatchAtEnd = true;
                        checkpoint.overwatchToSet = BuildOverwatchTrigger(pos2D, previousPos, node.action);
                        // Rattachement au bâtiment gardé — voir BuildUnitOrdersPure : sans lui, l'unité
                        // en garnison tirait sans jamais pouvoir être touchée en retour.
                        {
                            // Tolérant à la position de porte (toujours hors empreinte) — voir
                            // MatchGeometry.FindBuildingAtOrNear : sans ça, ce rattachement échouait
                            // systématiquement et la garnison de porte restait intouchable.
                            BuildingStructure doorBuilding = BuildingStructure.FindBuildingAtOrNear(
                                new Vector3(pos2D.x, 0f, pos2D.y), MatchGeometry.DoorAttachToleranceMeters);
                            // attachBuildingId, PAS enterBuildingId — voir le jumeau pur.
                            if (doorBuilding != null && buildingIndex.TryGetValue(doorBuilding, out int doorBid)) checkpoint.attachBuildingId = doorBid;
                        }
                        break;

                    case TacticalPathManager.NodeAction.SeCacher:
                        checkpoint.setCamouflaged = true;
                        break;

                    // Correctif 2026-09-05 (voir PathCheckpoint.waitSeconds) : même correctif que
                    // BuildUnitOrdersPure ci-dessus, pour le moteur "vivant" (chemin historique).
                    case TacticalPathManager.NodeAction.Attendre30s:
                        checkpoint.waitSeconds = 30f;
                        break;

                    case TacticalPathManager.NodeAction.GarnisonFenetre:
                        {
                            BuildingStructure building = BuildingStructure.FindBuildingAt(new Vector3(pos2D.x, 0f, pos2D.y));
                            BuildingStructure.BuildingWindow window = building?.GetClosestWindow(new Vector3(pos2D.x, 0f, pos2D.y));
                            if (window != null)
                            {
                                checkpoint.setGarrisonWindow = true;
                                checkpoint.windowNormalToSet = new Vector2(window.outwardNormal.x, window.outwardNormal.z);
                                pendingWindowByUnitId[unit.gameObject.name] = window;
                                // Voir BuildUnitOrdersPure : c'est ce rattachement qui rend la garnison
                                // vulnérable en retour, au lieu d'invulnérable.
                                if (buildingIndex.TryGetValue(building, out int garrisonBid)) checkpoint.enterBuildingId = garrisonBid;
                            }
                            break;
                        }

                    case TacticalPathManager.NodeAction.EntrerBatiment:
                        {
                            // Tolérant à la position de porte, sinon l'entrée ne se produisait JAMAIS
                            // (voir MatchGeometry.FindBuildingAtOrNear).
                            BuildingStructure building = BuildingStructure.FindBuildingAtOrNear(
                                new Vector3(pos2D.x, 0f, pos2D.y), MatchGeometry.DoorAttachToleranceMeters);
                            if (building != null && buildingIndex.TryGetValue(building, out int bid)) checkpoint.enterBuildingId = bid;
                            break;
                        }

                    case TacticalPathManager.NodeAction.SortirBatiment:
                        checkpoint.exitBuilding = true;
                        break;

                    case TacticalPathManager.NodeAction.Escalade:
                        {
                            // Hauteur exacte du parapet = hauteur du bâtiment escaladé (même
                            // ancrage que CreateRoofAccess côté rendu).
                            BuildingStructure building = BuildingStructure.FindBuildingAt(new Vector3(pos2D.x, 0f, pos2D.y));
                            // Voir BuildUnitOrdersPure : sans bâtiment, on reste au sol — jamais un Y=3
                            // arbitraire qui laissait l'unité suspendue au-dessus de la rue.
                            checkpoint.setPositionY = building != null ? building.height : 0f;
                            // Rattachement au bâtiment gravi — voir BuildUnitOrdersPure : c'est ce qui
                            // permet à LineOfSight de reconnaître "son" parapet.
                            if (building != null && buildingIndex.TryGetValue(building, out int climbedIdx))
                            {
                                checkpoint.enterBuildingId = climbedIdx;
                            }
                            break;
                        }

                    case TacticalPathManager.NodeAction.Descendre:
                        checkpoint.setPositionY = 0f;
                        checkpoint.exitBuilding = true;
                        break;
                }

                orders.checkpoints.Add(checkpoint);
                previousPos = pos2D;
            }

            // Descente implicite (rapport §2.3/§2.5) : l'ancien code déclenche ExecuteClimbDown dès
            // que le prochain point demandé est nettement plus bas QUE la position actuelle, sans
            // action dédiée dans l'enum — reproduit ici en ramenant au sol toute unité déjà sur un
            // toit qui reçoit un nouvel ordre sans ré-escalader dans le même ordre. Appliquée une
            // fois pour tout l'ordre (pas liée à un checkpoint précis), voir UnitOrders.implicitDescentY.
            // Plus de "descente implicite" posée ici (voir TacticalResolver.ExpandOrder) : la strate
            // est maintenant déduite de la géométrie pas à pas. L'ancien drapeau faisait redescendre
            // TOUTE unité perchée dont l'ordre ne contenait pas d'Escalade — donc aussi celle qui se
            // déplaçait simplement SUR son toit, que le client laissait pourtant en haut.
            return orders;
        }

        private static OverwatchTrigger BuildOverwatchTrigger(Vector2 finalPos, Vector2 previousPos, TacticalPathManager.NodeAction action)
        {
            if (action == TacticalPathManager.NodeAction.GuetterPorte)
            {
                BuildingStructure building = BuildingStructure.FindBuildingAt(new Vector3(finalPos.x, 0f, finalPos.y));
                BuildingStructure.BuildingDoor door = building?.GetClosestDoor(new Vector3(finalPos.x, 0f, finalPos.y));
                if (door != null)
                {
                    Vector2 doorPos = new Vector2(door.position.x, door.position.z);
                    // Perpendiculaire à la normale de la porte SANS trigonométrie (rotation 90° d'un
                    // vecteur 2D = simple permutation de composantes).
                    Vector2 tangent = new Vector2(-door.entryDirection.z, door.entryDirection.x);
                    float halfWidth = Mathf.Max(1f, door.width);
                    return new OverwatchTrigger { linePointA = doorPos - tangent * halfWidth, linePointB = doorPos + tangent * halfWidth };
                }
                // Repli sur un cône classique si aucune porte n'est trouvée à proximité.
            }

            Vector2 facing = finalPos - previousPos;
            facing = facing.sqrMagnitude < 0.0001f ? Vector2.up : facing.normalized;
            return new OverwatchTrigger { origin = finalPos, facing = facing, cosHalfAngle = OverwatchConeCosHalfAngle, range = OverwatchRange };
        }

        /// <summary>Rejoue le journal d'événements de TacticalResolver en une série de Snapshots —
        /// même format réseau qu'avant (NetMessage.Snapshot/UnitState), donc AUCUN changement côté
        /// client : MultiplayerMatchController.PlaySnapshotsCoroutine continue de simplement
        /// afficher ce qu'on lui envoie, sans jamais recalculer quoi que ce soit lui-même. Seule la
        /// SOURCE des positions change (TacticalResolver au lieu d'une simulation Unity live).</summary>
        private List<Snapshot> BuildSnapshotsFromEvents(List<TacticalEvent> events, List<UnitAI> unitsBeforeResolution, Dictionary<string, UnitAI> unitById)
        {
            var pos = new Dictionary<string, Vector2>();
            var rotation = new Dictionary<string, float>();
            var health = new Dictionary<string, int>();
            var dead = new Dictionary<string, bool>();
            var shooting = new Dictionary<string, bool>();
            var shootTarget = new Dictionary<string, string>();

            // Hauteur suivie PAS À PAS, exactement comme dans BuildSnapshotsFromEventsPure. Ce moteur
            // "vivant" (Conquête et Entraînement contre l'IA) lisait la hauteur depuis
            // unit.transform.position.y, qui n'est mis à jour qu'APRÈS la construction des snapshots :
            // tout le tour était donc rejoué à la hauteur de fin du tour PRÉCÉDENT. Une unité qui
            // descendait d'un toit traversait la rue en l'air pendant tout le rejeu, et une unité qui
            // montait marchait au sol tout le tour puis se téléportait verticalement sur le toit.
            var yByUnit = new Dictionary<string, float>();

            foreach (var u in unitsBeforeResolution)
            {
                string id = u.gameObject.name;
                pos[id] = new Vector2(u.transform.position.x, u.transform.position.z);
                rotation[id] = u.transform.eulerAngles.y;
                health[id] = u.health;
                dead[id] = u.isDead;
                shooting[id] = false;
                shootTarget[id] = null;
                yByUnit[id] = u.transform.position.y;
            }

            var snapshots = new List<Snapshot> { CaptureTacticalSnapshot(0, pos, rotation, health, dead, shooting, shootTarget, unitsBeforeResolution, yByUnit, null) };

            var ticks = events.Select(e => e.tick).Distinct().OrderBy(t => t).ToList();
            foreach (int tick in ticks)
            {
                foreach (var e in events.Where(e => e.tick == tick))
                {
                    switch (e.kind)
                    {
                        case TacticalEvent.Kind.Move:
                            // Rotation cosmétique uniquement (voir en-tête de méthode) : dérivée de
                            // la direction de déplacement via Atan2 — jamais utilisé pour décider
                            // du résultat officiel, donc aucun souci de déterminisme inter-appareil ici.
                            if (pos.TryGetValue(e.unitId, out Vector2 prev) && (e.position - prev).sqrMagnitude > 0.0001f)
                            {
                                Vector2 dir = e.position - prev;
                                rotation[e.unitId] = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
                            }
                            pos[e.unitId] = e.position;
                            break;
                        case TacticalEvent.Kind.Shot:
                        case TacticalEvent.Kind.OverwatchTriggered:
                            if (e.targetUnitId != null && health.ContainsKey(e.targetUnitId)) health[e.targetUnitId] -= e.damage;
                            if (shooting.ContainsKey(e.unitId))
                            {
                                shooting[e.unitId] = true;
                                shootTarget[e.unitId] = e.targetUnitId;
                            }
                            break;
                        case TacticalEvent.Kind.Death:
                            if (dead.ContainsKey(e.unitId)) dead[e.unitId] = true;
                            break;
                        case TacticalEvent.Kind.Elevation:
                            if (yByUnit.ContainsKey(e.unitId)) yByUnit[e.unitId] = e.y;
                            break;
                    }
                }

                // Applique le résultat de CE pas aux vraies UnitAI AVANT de capturer le snapshot :
                // SetNetworkHealth/ApplyNetworkDeath (jamais TakeDamage, voir RunExecutionPhase),
                // pour que CaptureZone.Tick() (mode Zone de Contrôle, appelé depuis
                // CaptureTacticalSnapshot) lise des positions à jour.
                foreach (var id in pos.Keys)
                {
                    if (!unitById.TryGetValue(id, out UnitAI unit) || unit == null) continue;
                    unit.transform.position = new Vector3(pos[id].x, unit.transform.position.y, pos[id].y);
                    unit.SetNetworkHealth(health[id]);
                    if (dead[id] && !unit.isDead) unit.ApplyNetworkDeath();
                }

                // Bâtiments détruits à CE tick précis (voir NetMessage.Snapshot.destroyed_building_ids
                // et son équivalent dans BuildSnapshotsFromEventsPure, même logique). Le VRAI
                // DestructibleEnvironment server-side est déjà correctement mis à jour plus bas (voir
                // "Répercute les dégâts de zone sur les VRAIS composants DestructibleEnvironment") —
                // ceci ne fait qu'informer le CLIENT, qui n'a par ailleurs aucun accès à cet état.
                int[] destroyedThisTick = events.Where(e => e.kind == TacticalEvent.Kind.WallDestroyed && e.tick == tick)
                    .Select(e => e.buildingId).Distinct().ToArray();

                snapshots.Add(CaptureTacticalSnapshot(tick * TickDurationMs, pos, rotation, health, dead, shooting, shootTarget, unitsBeforeResolution, yByUnit, destroyedThisTick));
                foreach (var id in shooting.Keys.ToList()) shooting[id] = false; // le "flash" de tir ne dure qu'un instant visuel
            }

            // Capture comptée UNE FOIS par tour, sur les positions de fin de tour (voir le correctif
            // détaillé dans BuildSnapshotsFromEventsPure), puis répartie sur les snapshots pour que la
            // barre s'anime quand même pendant le rejeu.
            if (currentMatchMode == "zone_control" && CaptureZone.Instance != null)
            {
                float progressBefore1 = CaptureZone.Instance.ProgressTeam1;
                float progressBefore2 = CaptureZone.Instance.ProgressTeam2;

                CaptureZone.Instance.Tick();

                for (int s = 0; s < snapshots.Count; s++)
                {
                    float ratio = (snapshots.Count > 1) ? (float)s / (snapshots.Count - 1) : 1f;
                    snapshots[s].zone_progress_team1 = Mathf.Lerp(progressBefore1, CaptureZone.Instance.ProgressTeam1, ratio);
                    snapshots[s].zone_progress_team2 = Mathf.Lerp(progressBefore2, CaptureZone.Instance.ProgressTeam2, ratio);
                }
            }

            return snapshots;
        }

        private Snapshot CaptureTacticalSnapshot(int t, Dictionary<string, Vector2> pos, Dictionary<string, float> rotation,
            Dictionary<string, int> health, Dictionary<string, bool> dead, Dictionary<string, bool> shooting, Dictionary<string, string> shootTarget,
            List<UnitAI> units, Dictionary<string, float> yByUnit, int[] destroyedBuildingIdsThisTick)
        {
            var states = new UnitState[units.Count];
            for (int i = 0; i < units.Count; i++)
            {
                string id = units[i].gameObject.name;
                Vector2 p = pos[id];
                states[i] = new UnitState
                {
                    unit_id = id,
                    // 2026-09-07 : cette ligne avait été PERDUE lors du découpage de
                    // MatchSessionManager.cs en 7 fichiers partial le 2026-09-06 (commit f458e65,
                    // annoncé comme « aucun changement de comportement »). Sans elle, x valait 0 pour
                    // TOUTES les unités de tous les snapshots du chemin « vivant » : en Conquête et en
                    // Entraînement contre l'IA, le rejeu du tour ramenait donc chaque unité sur la
                    // ligne x=0 de la carte, quelle que soit sa vraie position. L'équivalent pur
                    // (CaptureTacticalSnapshotPure) l'avait conservée, d'où l'asymétrie.
                    x = p.x,
                    // Hauteur DE CE PAS (voir yByUnit dans BuildSnapshotsFromEvents), et non plus
                    // unit.transform.position.y, qui n'est réactualisé qu'après la construction des
                    // snapshots et rejouait donc tout le tour à la hauteur du tour précédent.
                    y = yByUnit.TryGetValue(id, out float yVal) ? yVal : units[i].transform.position.y,
                    z = p.y,
                    ry = rotation[id],
                    health = health[id],
                    dead = dead[id],
                    shooting = shooting[id],
                    shoot_target_id = shootTarget.TryGetValue(id, out string tgt) ? tgt : null,
                    unit_type = InferUnitType(units[i]),
                    team_id = units[i].teamID
                };
            }

            // Comme pour le chemin en donnée pure : la capture n'avance plus à chaque PAS de
            // résolution (20m de marche de n'importe qui = 100% = match gagné dans le tour), elle est
            // comptée une seule fois par tour dans BuildSnapshotsFromEvents.
            float zoneProgress1 = 0f, zoneProgress2 = 0f;
            if (currentMatchMode == "zone_control" && CaptureZone.Instance != null)
            {
                zoneProgress1 = CaptureZone.Instance.ProgressTeam1;
                zoneProgress2 = CaptureZone.Instance.ProgressTeam2;
            }

            return new Snapshot { t = t, units = states, zone_progress_team1 = zoneProgress1, zone_progress_team2 = zoneProgress2, destroyed_building_ids = destroyedBuildingIdsThisTick };
        }
    }
}
