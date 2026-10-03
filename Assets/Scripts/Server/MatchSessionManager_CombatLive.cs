using System;
using System.Collections.Generic;
using System.Linq;
using Novgov.Network;
using UnityEngine;

namespace Novgov.Server
{
    /// <summary>
    /// Réception des messages d'un joueur pendant une bataille (ordres, déploiement, accusés de
    /// rejeu, vérification de géométrie), application des ordres aux unités, et capture d'un
    /// snapshot de simulation. La simulation elle-même : MatchSessionManager_CombatRealEngine.cs.
    /// </summary>
    public partial class MatchSessionManager
    {
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
                else if (msg.type == "city_verify")
                {
                    AnswerCityVerify(conn, msg.city_building_hash);
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

                    // Un blindé ne peut ni entrer dans un bâtiment ni monter sur un toit : tout point dont la position au
                    // sol tombe dans une empreinte est refusé pour lui (le NavMesh couvre à tort l'intérieur des
                    // bâtiments, et un client modifié pourrait envoyer un tel point).
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

            return new Snapshot { 
                t = t, 
                units = states, 
                destroyed_building_ids = destroyedBuildingIdsThisTick,
                destroyed_barrier_ids = destroyedBarrierIdsThisTick
            };
        }
    }
}
