using System;
using System.Collections.Generic;
using System.Linq;
using Novgov.Network;
using UnityEngine;

namespace Novgov.Server
{
    /// <summary>
    /// 2026-09-13 : ce fichier ne contient plus que ce qui reste réellement utilisé après la bascule
    /// vers le vrai moteur pour tous les modes (voir MatchSessionManager_CombatRealEngine.cs) — le
    /// calcul de résolution via TacticalResolver.Resolve() (RunExecutionPhase, BuildTacticalUnit/
    /// BuildUnitOrders/BuildSnapshotsFromEvents/FilterSnapshotsForTeam/ComputeVisibleUnitIds, ainsi que
    /// RunPlanningPhase/ApplyForPlayer, plus appelés par personne) a été supprimé — voir
    /// 13-dead-code-removal-2026-09-13.md pour le détail de ce qui a été retiré et pourquoi.
    /// </summary>
    public partial class MatchSessionManager
    {
        private string currentMatchMode = "deathmatch";
        private const int TickDurationMs = 250; // durée visuelle d'un pas de résolution, pour le rythme de lecture côté client

        private void DrainMessages(PlayerConnection conn, int turnNumber)
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
                else if (msg.type == "turn_result_ack" && msg.turn_number == turnNumber)
                {
                    conn.HasAckedTurnResult = true;
                }
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

                    // DURCISSEMENT SERVEUR (2026-09-19) — miroir de TacticalPathManager_ContextMenu.
                    // TryFallbackAuSolPourBlinde côté client (voir sa doc pour la root cause complète) :
                    // le NavMesh, ici comme là-bas, marche à tort sur l'intérieur de chaque bâtiment
                    // (son sol 2D "Footprint_2D" est un vrai collider plein bake par NavMeshSurface,
                    // voir CityGenerator.cs), donc rien n'empêche STRUCTURELLEMENT un char/canon-
                    // véhicule/mortier de recevoir un nœud de chemin dont les coordonnées XZ tombent
                    // dans l'empreinte d'un bâtiment. Le client corrigé n'en soumettra plus, mais ce
                    // chemin réseau reste atteignable par un client modifié qui saute le menu — le
                    // serveur (autorité finale du mouvement réel via NavMeshAgent, voir
                    // RunExecutionPhaseRealEngine) ne doit pas non plus faire confiance au client sur
                    // ce point. Un blindé ne peut, par construction, ni entrer dans un bâtiment ni
                    // monter sur un toit (voir TacticalPathManager_TapActionRouter) — un point XZ dans
                    // UNE empreinte de bâtiment est donc TOUJOURS invalide pour lui, quelle que soit
                    // l'action demandée ou la hauteur Y (une position sur le toit partage la même
                    // empreinte XZ que l'intérieur).
                    if (unit.isTank && BuildingStructure.FindBuildingAt(new Vector3(node.x, node.y, node.z)) != null)
                    {
                        continue;
                    }

                    unit.AddTacticalNode(new TacticalPathManager.TacticalNode
                    {
                        position = new Vector3(node.x, node.y, node.z),
                        action = (TacticalPathManager.NodeAction)node.action
                    });
                }
            }
        }

        /// <summary>Construit un Snapshot réseau à partir de l'état ACTUEL fourni pour chaque unité —
        /// générique par rapport à la SOURCE de ces valeurs (voir MatchSessionManager_CombatRealEngine.
        /// CaptureRealEngineSnapshot, seul appelant restant : lit l'état RÉEL des UnitAI à un instant
        /// donné du vrai moteur, alors que cette méthode servait avant à rejouer un journal
        /// d'événements pré-calculé — la signature n'a pas eu besoin de changer).</summary>
        private Snapshot CaptureTacticalSnapshot(int t, Dictionary<string, Vector2> pos, Dictionary<string, float> rotation,
            Dictionary<string, int> health, Dictionary<string, bool> dead, Dictionary<string, bool> shooting, Dictionary<string, string> shootTarget,
            List<UnitAI> units, Dictionary<string, float> yByUnit, int[] destroyedBuildingIdsThisTick, string[] destroyedBarrierIdsThisTick)
        {
            var states = new UnitState[units.Count];
            for (int i = 0; i < units.Count; i++)
            {
                string id = units[i].gameObject.name;
                Vector2 p = pos[id];
                states[i] = new UnitState
                {
                    unit_id = id,
                    x = p.x,
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

            float zoneProgress1 = 0f, zoneProgress2 = 0f;
            if (currentMatchMode == "zone_control" && CaptureZone.Instance != null)
            {
                zoneProgress1 = CaptureZone.Instance.ProgressTeam1;
                zoneProgress2 = CaptureZone.Instance.ProgressTeam2;
            }

            return new Snapshot { 
                t = t, 
                units = states, 
                zone_progress_team1 = zoneProgress1, 
                zone_progress_team2 = zoneProgress2, 
                destroyed_building_ids = destroyedBuildingIdsThisTick,
                destroyed_barrier_ids = destroyedBarrierIdsThisTick
            };
        }
    }
}
