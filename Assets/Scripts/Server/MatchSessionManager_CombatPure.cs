using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Novgov.Network;
using Novgov.TacticalCore;
using UnityEngine;

namespace Novgov.Server
{
    public partial class MatchSessionManager
    {
        /// <summary>Copie PAR VALEUR de l'état d'une unité au DÉBUT d'un tour — le point de départ à
        /// partir duquel le rejeu du tour est reconstruit (voir BuildSnapshotsFromEventsPure).
        ///
        /// Une struct, et pas une simple référence vers la TacticalUnit : celle-ci est une classe que
        /// TacticalResolver.Resolve mute EN PLACE, donc toute référence conservée à travers la
        /// résolution finit par décrire la FIN du tour, pas son début. Voir le commentaire détaillé au
        /// site de capture, dans RunExecutionPhasePure.</summary>
        private readonly struct UnitTurnStart
        {
            public readonly string Id;
            public readonly int Team;
            public readonly Vector2 Position;
            public readonly int Health;
            public readonly bool IsDead;

            public UnitTurnStart(TacticalUnit u)
            {
                Id = u.id;
                Team = u.team;
                Position = u.position;
                Health = u.health;
                IsDead = u.isDead;
            }
        }

        /// <summary>Version en donnée pure de RunPlanningPhase — même timing exactement.</summary>
        private IEnumerator RunPlanningPhasePure(MatchState ms, int turnNumber)
        {
            PlayerConnection p1 = ms.P1, p2 = ms.P2;
            p1.HasSubmittedThisTurn = false;
            p2.HasSubmittedThisTurn = false;

            float remaining = PlanningSeconds;
            int lastTick = -1;
            DateTime planningStartUtc = DateTime.UtcNow;
            Debug.Log($"[Timing] [{ms.MatchId}] Tour {turnNumber} — RunPlanningPhasePure démarré à {planningStartUtc:O} (max {PlanningSeconds}s).");

            while (remaining > 0f && !(p1.HasSubmittedThisTurn && p2.HasSubmittedThisTurn))
            {
                DrainMessages(p1, turnNumber);
                DrainMessages(p2, turnNumber);
                if (p1.IsDisconnected && p2.IsDisconnected) yield break;

                int secondsLeft = Mathf.CeilToInt(remaining);
                if (secondsLeft != lastTick)
                {
                    lastTick = secondsLeft;
                    var tick = new NetMessage { type = "turn_timer", seconds_remaining = secondsLeft };
                    if (!p1.IsDisconnected) p1.Send(tick);
                    if (!p2.IsDisconnected) p2.Send(tick);
                }

                remaining -= Time.deltaTime;
                yield return null;
            }

            double planningElapsedSec = (DateTime.UtcNow - planningStartUtc).TotalSeconds;
            Debug.Log($"[Timing] [{ms.MatchId}] Tour {turnNumber} — RunPlanningPhasePure terminé après {planningElapsedSec:F1}s réelles (attendu max {PlanningSeconds}s).");

            ApplyForPlayerPure(ms, p1, p2);
            ApplyForPlayerPure(ms, p2, p1);
        }

        /// <summary>Équivalent pur de ApplyForPlayer. Simplification ASSUMÉE pour le repli IA d'un
        /// joueur absent/en retard (voir la discussion d'architecture du 2026-08-30) : plutôt que
        /// TacticalAIPlanner (qui dépend de vraies UnitAI/de la liste globale UnitAI.AllLivingUnits,
        /// incompatible avec des parties concurrentes), ses unités "tiennent la position" ce tour-ci
        /// (aucun ordre) — TacticalResolver.Resolve() évalue quand même les tirs pour TOUTE unité
        /// vivante, pas seulement celles avec un ordre actif (voir TacticalResolver.cs, énumération de
        /// "shooters" sur state.units en entier) : ces unités se défendent donc normalement, juste
        /// sans avancer/flanquer activement comme le ferait la vraie IA en Solo/Conquête.</summary>
        private void ApplyForPlayerPure(MatchState ms, PlayerConnection conn, PlayerConnection opponent)
        {
            bool shouldGhost = conn.IsDisconnected || !conn.HasSubmittedThisTurn;

            if (shouldGhost)
            {
                // Journalisé explicitement (2026-09-08) : jusqu'ici totalement silencieux, donc
                // impossible à distinguer après coup d'un vrai AFK/déconnexion — un joueur ayant bien
                // soumis ses ordres mais ghosté quand même par un bug (ex. désynchronisation du
                // numéro de tour, voir DrainMessages) ne laissait AUCUNE trace. Ce log est la seule
                // preuve qu'aura une session future pour distinguer les deux sans deviner à l'aveugle
                // (voir le rapport joueur "il y a toujours de l'IA" du 2026-09-08, qui a fini par
                // trouver sa cause réelle ailleurs — voir OnOpponentGhosted côté client — mais SANS
                // ce genre de preuve, faute d'accès à un vrai log de session en cours).
                Debug.Log($"[Ghost] [{ms.MatchId}] Équipe {conn.TeamId} ({conn.UserId}) ghostée ce tour — IsDisconnected={conn.IsDisconnected}, HasSubmittedThisTurn={conn.HasSubmittedThisTurn}.");

                foreach (var u in ms.World.units.Where(u => u.team == conn.TeamId)) ms.PendingOrderNodes.Remove(u.id);

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
                var myUnits = ms.World.units.Where(u => u.team == conn.TeamId && !u.isDead).ToList();
                ApplyOrdersToUnitsPure(ms, myUnits, conn.PendingOrders);
            }
        }

        /// <summary>Équivalent pur de ApplyOrdersToUnits — écrit dans ms.PendingOrderNodes au lieu
        /// d'appeler unit.AddTacticalNode sur une UnitAI réelle. Mêmes validations exactement (chemin
        /// tronqué à MaxOrderPathNodes, NaN/Infinity/action hors enum rejetés silencieusement).</summary>
        private void ApplyOrdersToUnitsPure(MatchState ms, List<TacticalUnit> myUnits, UnitOrder[] orders)
        {
            if (orders == null) return;
            var myIds = new HashSet<string>(myUnits.Select(u => u.id));

            foreach (var order in orders)
            {
                if (!myIds.Contains(order.unit_id)) continue;

                ms.PendingOrderNodes.Remove(order.unit_id);
                if (order.path == null) continue;
                if (order.path.Length > MaxOrderPathNodes) continue;

                var nodes = new List<TacticalPathManager.TacticalNode>();
                foreach (var node in order.path)
                {
                    if (float.IsNaN(node.x) || float.IsNaN(node.y) || float.IsNaN(node.z)) continue;
                    if (float.IsInfinity(node.x) || float.IsInfinity(node.y) || float.IsInfinity(node.z)) continue;
                    if (!Enum.IsDefined(typeof(TacticalPathManager.NodeAction), node.action)) continue;

                    nodes.Add(new TacticalPathManager.TacticalNode
                    {
                        position = new Vector3(node.x, node.y, node.z),
                        action = (TacticalPathManager.NodeAction)node.action
                    });
                }
                ms.PendingOrderNodes[order.unit_id] = nodes;
            }
        }

        /// <summary>Version en donnée pure de RunExecutionPhase — AUCUNE écriture sur un GameObject de
        /// scène (ni UnitAI, ni BuildingStructure/DestructibleEnvironment) : ms.World EST le résultat
        /// officiel, avant et après Resolve(), rien de plus à synchroniser ailleurs. C'est exactement
        /// ce qui rend plusieurs parties sûres en parallèle (voir le bug de corruption de santé de
        /// bâtiment/occupation de fenêtre qui a motivé ce chantier).</summary>
        private IEnumerator RunExecutionPhasePure(MatchState ms, int turnNumber)
        {
            PlayerConnection p1 = ms.P1, p2 = ms.P2;
            DateTime executionStartUtc = DateTime.UtcNow;

            // Rafraîchit spottingRange selon la strate ACTUELLE (peut avoir changé au tour précédent
            // via une Escalade — TacticalResolver.ApplyEndOfPathEffects met déjà zStrata à jour en
            // place, voir TacticalTypes.cs) : même barème que BuildTacticalUnit (55m toit / 25m
            // mortier / 35m sinon), recalculé chaque tour comme l'original.
            foreach (var u in ms.World.units)
            {
                if (u.isDead) continue;
                u.spottingRange = u.zStrata == ZStrata.Toit ? 55f : (u.isMortar ? 25f : 35f);
            }

            var unitsBeforeResolution = ms.World.units.Where(u => !u.isDead).ToList();

            // ÉTAT DE DÉBUT DE TOUR, CAPTURÉ PAR VALEUR (2026-09-07).
            //
            // TacticalUnit est une CLASSE (TacticalTypes.cs) et TacticalResolver.Resolve la mute EN
            // PLACE (unit.position, target.health, target.isDead). `unitsBeforeResolution` ne contient
            // donc que des RÉFÉRENCES : une fois la résolution terminée, ces objets portent l'état de
            // FIN de tour, et son nom ne veut plus rien dire. Or c'est exactement lui qui servait à
            // amorcer le rejeu dans BuildSnapshotsFromEventsPure — le tour entier était donc rejoué
            // à partir de son propre résultat, à chaque tour des deux modes PvP :
            //   - le snapshot t=0 plaçait déjà chaque unité à sa position FINALE, puis le premier
            //     événement Move la ramenait brutalement en arrière pour la faire re-marcher ;
            //   - les PV étaient amorcés à leur valeur d'APRÈS combat, puis chaque événement Shot
            //     retranchait ses dégâts une SECONDE fois (barres de vie fausses toute la partie) ;
            //   - une unité tuée ce tour-ci était marquée morte dès la première image du rejeu, avant
            //     même le tir qui la tue.
            // Une copie par valeur, prise AVANT Resolve, est la seule chose qui immunise ce rejeu
            // contre la mutation en place.
            var turnStartStates = unitsBeforeResolution.Select(u => new UnitTurnStart(u)).ToList();
            var mortarStrikes = new List<Vector2>();
            var ordersTeam1 = new List<UnitOrders>();
            var ordersTeam2 = new List<UnitOrders>();
            var pendingWindowByUnitId = new Dictionary<string, (int buildingIdx, int windowId)?>();

            foreach (var unit in unitsBeforeResolution)
            {
                ms.PendingOrderNodes.TryGetValue(unit.id, out var path);
                UnitOrders orders = BuildUnitOrdersPure(ms, unit, path, mortarStrikes, pendingWindowByUnitId);
                if (orders.checkpoints.Count > 0)
                {
                    (unit.team == 1 ? ordersTeam1 : ordersTeam2).Add(orders);
                }
            }

            Debug.Log($"[Trajectoire] [{ms.MatchId}] Tour {turnNumber} : équipe1={ordersTeam1.Count} unité(s) avec ordres, équipe2={ordersTeam2.Count} unité(s) avec ordres, {mortarStrikes.Count} frappe(s) de mortier.");

            double preResolveMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds;
            // Pool de threads (2026-08-30, consolidation à une seule instance de processus) —
            // TacticalResolver.Resolve() est une fonction pure : ms.World/ordersTeam1/2/mortarStrikes
            // sont des données PROPRES à cette partie, jamais partagées avec une autre (voir
            // MatchState) — donc sûr à exécuter sur un thread d'arrière-plan. Sans ça, avec une seule
            // instance/un seul thread principal Unity, plusieurs parties dont le minuteur expire au
            // même instant se sérialiseraient entre elles (chacune attend son tour pour son calcul,
            // même bref) — Task.Run les laisse s'exécuter en parallèle sur plusieurs cœurs CPU.
            var resolveTask = Task.Run(() => TacticalResolver.Resolve(ms.World, ordersTeam1, ordersTeam2, mortarStrikes));
            while (!resolveTask.IsCompleted) yield return null;
            if (resolveTask.Exception != null) throw resolveTask.Exception.InnerException ?? resolveTask.Exception;
            List<TacticalEvent> tacticalEvents = resolveTask.Result;
            double resolveOnlyMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds - preResolveMs;
            Debug.Log($"[Timing] [{ms.MatchId}] Tour {turnNumber} : préparation (ordres) {preResolveMs:F1}ms réelles, TacticalResolver.Resolve() SEUL {resolveOnlyMs:F1}ms réelles ({tacticalEvents.Count} événement(s) générés).");

            var snapshots = BuildSnapshotsFromEventsPure(ms, tacticalEvents, turnStartStates);

            // Occupation de fenêtre PROPRE À CETTE PARTIE (voir MatchState.OccupiedWindows) — remplace
            // BuildingWindow.OccupyWindow/VacateWindow (composant de scène PARTAGÉ). Hauteur Y
            // cosmétique (voir MatchState.CurrentYById) mise à jour de la même façon que
            // unit.transform.position dans RunExecutionPhase.
            foreach (var tu in ms.World.units)
            {
                if (tu.isDead) continue;

                if (tu.outputY.HasValue) ms.CurrentYById[tu.id] = tu.outputY.Value;

                pendingWindowByUnitId.TryGetValue(tu.id, out var newWindowKey);
                bool wantsWindow = tu.isGarrisoned && newWindowKey.HasValue;
                bool hasCurrentWindow = ms.CurrentWindowByUnitId.TryGetValue(tu.id, out var currentWindowKey);

                if (hasCurrentWindow && (!wantsWindow || !currentWindowKey.Equals(newWindowKey.GetValueOrDefault())))
                {
                    ms.OccupiedWindows.Remove(currentWindowKey);
                    ms.CurrentWindowByUnitId.Remove(tu.id);
                    hasCurrentWindow = false;
                }
                if (wantsWindow && !hasCurrentWindow)
                {
                    ms.OccupiedWindows.Add(newWindowKey.Value);
                    ms.CurrentWindowByUnitId[tu.id] = newWindowKey.Value;
                }
            }

            // Dégâts de bâtiment déjà appliqués directement dans ms.World.buildings par Resolve() lui
            // -même (donnée pure PROPRE à cette partie) — AUCUNE écriture sur un DestructibleEnvironment
            // partagé ici, contrairement à RunExecutionPhase (Conquête) : c'est précisément le bug de
            // corruption qui a motivé ce chantier (2026-08-30).

            ms.PendingOrderNodes.Clear();

            var unitMetaById = ms.World.units.ToDictionary(u => u.id);
            var teamById = ms.World.units.ToDictionary(u => u.id, u => u.team);
            Snapshot[] snapshotsForTeam1 = FilterSnapshotsForTeam(snapshots, ms.World, unitMetaById, teamById, 1);
            Snapshot[] snapshotsForTeam2 = FilterSnapshotsForTeam(snapshots, ms.World, unitMetaById, teamById, 2);

            int maxEnemiesVisibleToTeam1 = snapshotsForTeam1.Length == 0 ? 0 : snapshotsForTeam1.Max(s => s.units.Count(u => u.team_id == 2));
            int maxEnemiesVisibleToTeam2 = snapshotsForTeam2.Length == 0 ? 0 : snapshotsForTeam2.Max(s => s.units.Count(u => u.team_id == 1));
            Debug.Log($"[Vision] [{ms.MatchId}] Tour {turnNumber} : {snapshots.Count} tick(s) — équipe1 a vu au maximum {maxEnemiesVisibleToTeam1} ennemi(s), équipe2 a vu au maximum {maxEnemiesVisibleToTeam2} ennemi(s).");

            if (!p1.IsDisconnected)
            {
                p1.Send(new NetMessage { type = "turn_result", turn_number = turnNumber, snapshot_interval_ms = TickDurationMs, snapshots = snapshotsForTeam1 });
            }
            if (!p2.IsDisconnected)
            {
                p2.Send(new NetMessage { type = "turn_result", turn_number = turnNumber, snapshot_interval_ms = TickDurationMs, snapshots = snapshotsForTeam2 });
            }

            double totalPhaseElapsedMs = (DateTime.UtcNow - executionStartUtc).TotalMilliseconds;
            Debug.Log($"[Timing] [{ms.MatchId}] Tour {turnNumber} : RunExecutionPhasePure entièrement terminé en {totalPhaseElapsedMs:F1}ms réelles.");

            yield break;
        }

        /// <summary>Équivalent pur de BuildUnitOrders — même logique/mêmes NodeAction exactement (voir
        /// BuildUnitOrders pour le détail commenté de chaque cas), mais lit une TacticalUnit + une
        /// liste de TacticalNode déjà résolue (ms.PendingOrderNodes) au lieu d'une UnitAI réelle, et
        /// consulte l'occupation de fenêtre PROPRE à cette partie (ms.OccupiedWindows) au lieu du
        /// champ partagé BuildingWindow.isOccupied.</summary>
        private UnitOrders BuildUnitOrdersPure(MatchState ms, TacticalUnit unit, List<TacticalPathManager.TacticalNode> path,
            List<Vector2> mortarStrikesOut, Dictionary<string, (int buildingIdx, int windowId)?> pendingWindowByUnitId)
        {
            var orders = new UnitOrders { unitId = unit.id };
            Vector2 previousPos = unit.position;

            if (path != null)
            {
                foreach (var node in path)
                {
                    Vector2 pos2D = new Vector2(node.position.x, node.position.z);

                    if (node.action == TacticalPathManager.NodeAction.TirMortier)
                    {
                        mortarStrikesOut.Add(pos2D);
                        continue;
                    }

                    var checkpoint = new PathCheckpoint { position = pos2D };

                    switch (node.action)
                    {
                        case TacticalPathManager.NodeAction.Guetter:
                        case TacticalPathManager.NodeAction.Embuscade:
                            checkpoint.setGuarding = true;
                            checkpoint.enterOverwatchAtEnd = true;
                            checkpoint.overwatchToSet = BuildOverwatchTriggerPure(ms.World, pos2D, previousPos, node.action);
                            break;

                        case TacticalPathManager.NodeAction.GuetterPorte:
                            checkpoint.setGarrisonDoor = true;
                            checkpoint.enterOverwatchAtEnd = true;
                            checkpoint.overwatchToSet = BuildOverwatchTriggerPure(ms.World, pos2D, previousPos, node.action);
                            // Même rattachement que GarnisonFenetre, et pour la même raison : sans lui
                            // la garnison de porte était elle aussi impossible à toucher.
                            // Tolérant à la position de porte : un point de porte est TOUJOURS hors de
                            // l'empreinte (voir MatchGeometry.DoorAttachToleranceMeters) — avec un
                            // PointInPolygon strict ce rattachement échouait à 100%.
                            // attachBuildingId, PAS enterBuildingId : garder la porte ne doit pas faire
                            // franchir le seuil (voir PathCheckpoint.attachBuildingId).
                            checkpoint.attachBuildingId = MatchGeometry.FindBuildingAtOrNear(ms.World, pos2D, MatchGeometry.DoorAttachToleranceMeters);
                            break;

                        case TacticalPathManager.NodeAction.SeCacher:
                            checkpoint.setCamouflaged = true;
                            break;

                        // Correctif 2026-09-05 (voir PathCheckpoint.waitSeconds) : cette action ne
                        // produisait auparavant AUCUN drapeau, tombant dans le cas par défaut du
                        // switch — un simple déplacement, sans la pause promise par le menu.
                        case TacticalPathManager.NodeAction.Attendre30s:
                            checkpoint.waitSeconds = 30f;
                            break;

                        case TacticalPathManager.NodeAction.GarnisonFenetre:
                            {
                                int bIdx = MatchGeometry.FindBuildingAt(ms.World, pos2D);
                                TacticalBuilding building = bIdx >= 0 ? ms.World.GetBuilding(bIdx) : null;
                                if (building != null)
                                {
                                    TacticalWindow window = MatchGeometry.GetClosestWindow(building, pos2D,
                                        winId => !ms.OccupiedWindows.Contains((bIdx, winId)));
                                    if (window != null)
                                    {
                                        checkpoint.setGarrisonWindow = true;
                                        checkpoint.windowNormalToSet = window.outwardNormal;
                                        pendingWindowByUnitId[unit.id] = (bIdx, window.id);
                                        // Rattachement au bâtiment occupé. Sans lui, currentBuildingId
                                        // restait à -1 et l'unité en garnison devenait INVULNÉRABLE :
                                        // elle tirait par son embrasure (exemption "à moins de 2.5m du
                                        // tireur") mais aucune riposte ne pouvait l'atteindre, car
                                        // l'exemption symétrique côté cible compare justement le mur au
                                        // currentBuildingId de la cible. Le -75% annoncé n'était jamais
                                        // atteint puisque zéro tir ne portait.
                                        checkpoint.enterBuildingId = bIdx;
                                    }
                                }
                                break;
                            }

                        case TacticalPathManager.NodeAction.EntrerBatiment:
                            {
                                // Tolérant à la position de porte, sinon l'entrée ne se produisait
                                // JAMAIS : voir MatchGeometry.FindBuildingAtOrNear.
                                int bIdx = MatchGeometry.FindBuildingAtOrNear(ms.World, pos2D, MatchGeometry.DoorAttachToleranceMeters);
                                if (bIdx >= 0) checkpoint.enterBuildingId = bIdx;
                                break;
                            }

                        case TacticalPathManager.NodeAction.SortirBatiment:
                            checkpoint.exitBuilding = true;
                            break;

                        case TacticalPathManager.NodeAction.Escalade:
                            {
                                // Jamais de repli sur une hauteur arbitraire : sans bâtiment sous le
                                // point visé il n'y a rien à escalader, donc l'ordre vaut "reste au
                                // sol". L'ancien repli à 3f laissait l'unité suspendue en l'air
                                // au-dessus de la chaussée, en strate Toit (portée +20m, exemption de
                                // ligne de vue) pour tout le reste de la partie.
                                int bIdx = MatchGeometry.FindBuildingAt(ms.World, pos2D);
                                TacticalBuilding building = bIdx >= 0 ? ms.World.GetBuilding(bIdx) : null;
                                checkpoint.setPositionY = building != null ? building.height : 0f;
                                // Rattacher l'unité au bâtiment qu'elle vient de gravir. Sans ça, son
                                // currentBuildingId restait à -1 et LineOfSight ne pouvait pas savoir
                                // quel parapet était "le sien" : postée au centre du toit, l'unité ne
                                // tirait sur rien et n'était vue de personne pour tout le reste de la
                                // partie (voir LineOfSight.WallBelongsToOwnBuilding).
                                checkpoint.enterBuildingId = bIdx;
                                break;
                            }

                        case TacticalPathManager.NodeAction.Descendre:
                            checkpoint.setPositionY = 0f; // retour au sol explicite (voir NodeAction.Descendre)
                            checkpoint.exitBuilding = true; // quitte le toit : plus de rattachement ni de posture
                            break;
                    }

                    orders.checkpoints.Add(checkpoint);
                    previousPos = pos2D;
                }
            }

            // Plus de "descente implicite" posée ici (voir TacticalResolver.ExpandOrder) : la strate
            // est maintenant déduite de la géométrie pas à pas. L'ancien drapeau faisait redescendre
            // TOUTE unité perchée dont l'ordre ne contenait pas d'Escalade — donc aussi celle qui se
            // déplaçait simplement SUR son toit, que le client laissait pourtant en haut.
            return orders;
        }

        /// <summary>Équivalent pur de BuildOverwatchTrigger — résout la porte la plus proche depuis
        /// ms.World (voir MatchGeometry), jamais BuildingStructure.FindBuildingAt/GetClosestDoor
        /// (scène vivante). 2026-08-30, "des milliers de cartes".</summary>
        private static OverwatchTrigger BuildOverwatchTriggerPure(TacticalWorldState world, Vector2 finalPos, Vector2 previousPos, TacticalPathManager.NodeAction action)
        {
            if (action == TacticalPathManager.NodeAction.GuetterPorte)
            {
                // Tolérant, pour la même raison que le rattachement ci-dessus : finalPos EST un point
                // de porte, donc toujours hors de l'empreinte. Avec le test strict, la porte n'était
                // jamais trouvée et le déclencheur de guet en LIGNE DE PORTE (linePointA/linePointB,
                // rapport §2.9) se rabattait systématiquement sur le cône générique — du code mort.
                int bIdx = MatchGeometry.FindBuildingAtOrNear(world, finalPos, MatchGeometry.DoorAttachToleranceMeters);
                TacticalBuilding building = bIdx >= 0 ? world.GetBuilding(bIdx) : null;
                TacticalDoor door = building != null ? MatchGeometry.GetClosestDoor(building, finalPos) : null;
                if (door != null)
                {
                    Vector2 tangent = new Vector2(-door.entryDirection.y, door.entryDirection.x);
                    float halfWidth = Mathf.Max(1f, door.width);
                    return new OverwatchTrigger { linePointA = door.position - tangent * halfWidth, linePointB = door.position + tangent * halfWidth };
                }
            }

            Vector2 facing = finalPos - previousPos;
            facing = facing.sqrMagnitude < 0.0001f ? Vector2.up : facing.normalized;
            return new OverwatchTrigger { origin = finalPos, facing = facing, cosHalfAngle = OverwatchConeCosHalfAngle, range = OverwatchRange };
        }

        /// <summary>Équivalent pur de BuildSnapshotsFromEvents — même reconstruction du journal
        /// d'événements en Snapshots tick par tick, mais sans jamais écrire sur une UnitAI réelle
        /// (SetNetworkHealth/ApplyNetworkDeath/transform.position) : les dictionnaires locaux
        /// pos/health/dead/shooting SONT la seule source de vérité pour construire chaque Snapshot
        /// (voir CaptureTacticalSnapshotPure).</summary>
        private List<Snapshot> BuildSnapshotsFromEventsPure(MatchState ms, List<TacticalEvent> events, List<UnitTurnStart> turnStartStates)
        {
            var pos = new Dictionary<string, Vector2>();
            var rotation = new Dictionary<string, float>();
            var health = new Dictionary<string, int>();
            var dead = new Dictionary<string, bool>();
            var shooting = new Dictionary<string, bool>();
            var teamOf = new Dictionary<string, int>();

            // Hauteur suivie PAS À PAS pendant le rejeu. Elle était lue dans ms.CurrentYById, qui
            // n'est écrit qu'APRÈS la construction des snapshots : tout le tour était donc rejoué à la
            // hauteur du tour PRÉCÉDENT (une unité descendue d'un toit de 6m traversait la rue en
            // l'air pendant tout le rejeu, puis retombait d'un coup au tour suivant). On part de la
            // hauteur de début de tour et on la met à jour sur les événements Elevation, datés du pas
            // exact du franchissement.
            var yByUnit = new Dictionary<string, float>();

            // Amorçage depuis la COPIE PAR VALEUR de début de tour, jamais depuis les TacticalUnit
            // eux-mêmes : Resolve les a mutés en place entre-temps (voir UnitTurnStart).
            foreach (var u in turnStartStates)
            {
                pos[u.Id] = u.Position;
                rotation[u.Id] = 0f;
                health[u.Id] = u.Health;
                dead[u.Id] = u.IsDead;
                shooting[u.Id] = false;
                teamOf[u.Id] = u.Team;
                yByUnit[u.Id] = ms.CurrentYById.TryGetValue(u.Id, out float y0) ? y0 : 0f;
            }

            var snapshots = new List<Snapshot> { CaptureTacticalSnapshotPure(ms, 0, pos, rotation, health, dead, shooting, teamOf, yByUnit) };

            var ticks = events.Select(e => e.tick).Distinct().OrderBy(t => t).ToList();
            foreach (int tick in ticks)
            {
                foreach (var e in events.Where(e => e.tick == tick))
                {
                    switch (e.kind)
                    {
                        case TacticalEvent.Kind.Move:
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
                            if (shooting.ContainsKey(e.unitId)) shooting[e.unitId] = true;
                            break;
                        case TacticalEvent.Kind.Death:
                            if (dead.ContainsKey(e.unitId)) dead[e.unitId] = true;
                            break;
                        case TacticalEvent.Kind.Elevation:
                            if (yByUnit.ContainsKey(e.unitId)) yByUnit[e.unitId] = e.y;
                            break;
                    }
                }

                snapshots.Add(CaptureTacticalSnapshotPure(ms, tick * TickDurationMs, pos, rotation, health, dead, shooting, teamOf, yByUnit));
                foreach (var id in shooting.Keys.ToList()) shooting[id] = false;
            }

            // CAPTURE DE ZONE : UNE SEULE FOIS PAR TOUR (correctif 2026-09-03).
            //
            // Zone.Tick() était appelée dans CaptureTacticalSnapshotPure, donc une fois par PAS de
            // résolution. Un pas valant 1m de marche et ProgressPerTick valant 5%, 20 pas suffisaient
            // à atteindre 100% : la première unité entrée dans le cercle emportait le match dans le
            // tour même, la barre passant de 0 à 100 pendant le rejeu. Pire, le nombre de pas est
            // fixé par le chemin le plus long du tour : un adversaire qui marchait 50m pour venir
            // contester offrait lui-même 50 pas (250%) de progression au défenseur. Le plafond
            // ZoneControlTurnCap de 20 tours et toute la mécanique de contestation/décroissance
            // étaient du code mort.
            //
            // La progression dépend maintenant des positions de FIN de tour uniquement : 5% par tour,
            // soit exactement 20 tours pour une capture complète — la valeur de ZoneControlTurnCap.
            if (ms.Zone != null)
            {
                float progressBefore1 = ms.Zone.ProgressTeam1;
                float progressBefore2 = ms.Zone.ProgressTeam2;

                var endOfTurnUnits = pos.Keys.Select(id => (id, teamOf[id], pos[id], dead[id])).ToList();
                ms.Zone.Tick(endOfTurnUnits);

                // La barre s'animera tout de même pendant le rejeu : on répartit l'évolution du tour
                // sur les snapshots au lieu de la faire sauter d'un bloc au dernier.
                for (int s = 0; s < snapshots.Count; s++)
                {
                    float ratio = (snapshots.Count > 1) ? (float)s / (snapshots.Count - 1) : 1f;
                    snapshots[s].zone_progress_team1 = Mathf.Lerp(progressBefore1, ms.Zone.ProgressTeam1, ratio);
                    snapshots[s].zone_progress_team2 = Mathf.Lerp(progressBefore2, ms.Zone.ProgressTeam2, ratio);
                }
            }

            return snapshots;
        }

        /// <summary>Équivalent pur de CaptureTacticalSnapshot — lit unit_type/Y cosmétique depuis
        /// MatchState (voir UnitTypeById/CurrentYById) au lieu d'une UnitAI réelle. Ne fait PLUS
        /// avancer ms.Zone : la capture est comptée une seule fois par tour sur les positions de fin
        /// de tour (voir BuildSnapshotsFromEventsPure), sinon 20m de marche de n'importe qui valaient
        /// une capture complète.</summary>
        private Snapshot CaptureTacticalSnapshotPure(MatchState ms, int t, Dictionary<string, Vector2> pos, Dictionary<string, float> rotation,
            Dictionary<string, int> health, Dictionary<string, bool> dead, Dictionary<string, bool> shooting, Dictionary<string, int> teamOf,
            Dictionary<string, float> yByUnit)
        {
            var states = new UnitState[pos.Count];
            int i = 0;
            foreach (var id in pos.Keys)
            {
                states[i++] = new UnitState
                {
                    unit_id = id,
                    x = pos[id].x,
                    y = yByUnit.TryGetValue(id, out float yVal) ? yVal : 0f, // hauteur DE CE PAS, jamais celle du tour précédent
                    z = pos[id].y,
                    ry = rotation[id],
                    health = health[id],
                    dead = dead[id],
                    shooting = shooting[id],
                    unit_type = ms.UnitTypeById.TryGetValue(id, out int ut) ? ut : (int)UnitSpawnerUI.UnitType.Fantassin,
                    team_id = teamOf[id]
                };
            }

            // La progression de zone n'est PLUS calculée ici : elle l'est une seule fois par tour,
            // dans BuildSnapshotsFromEventsPure, puis répartie sur les snapshots (voir là-bas).
            return new Snapshot { t = t, units = states, zone_progress_team1 = ms.Zone != null ? ms.Zone.ProgressTeam1 : 0f, zone_progress_team2 = ms.Zone != null ? ms.Zone.ProgressTeam2 : 0f };
        }
    }
}
