using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Cerveau Tactique Avancé pour les unités IA ennemies.
/// Gère l'utilisation stratégique des toits (snipers), des bâtiments (garnisons de fenêtres),
/// des barricades routières (couverture) et des frappes d'artillerie lourde (mortiers).
/// </summary>
public static class TacticalAIPlanner
{
    public static void PlanTurnForUnit(UnitAI unit)
    {
        if (unit == null || unit.isDead) return;
        // Cette IA ne planifie jamais pour une unité du joueur : une unité sans trajectoire définie
        // par le joueur reste simplement immobile ce tour-ci (pas de substitut IA).
        if (unit.isPlayerControlled) return;

        unit.tacticalPath.Clear();
        unit.currentNodeIndex = 0;

        // 1. Trouver une cible joueur CONFIRMÉE REPÉRÉE par ligne de vue (Brouillard de guerre équitable)
        UnitAI closestTarget = null;
        float closestDist = float.MaxValue;

        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI target = UnitAI.AllLivingUnits[i];
            if (target != null && !target.isDead && target.isPlayerControlled && !target.isCamouflaged && target.teamID != unit.teamID)
            {
                // Vérifier si le joueur est repéré par un éclaireur ou l'unité elle-même
                if (UnitAI.IsUnitSpottedByTeam(target, unit.teamID))
                {
                    float d = Vector3.Distance(unit.transform.position, target.transform.position);
                    if (d < closestDist)
                    {
                        closestDist = d;
                        closestTarget = target;
                    }
                }
            }
        }

        // =========================================================================
        // SI AUCUN JOUEUR N'EST REPÉRÉ : MODE RECONNAISSANCE & PATROUILLE
        // (Le mortier IA NE TIRE PAS À L'AVEUGLE SUR LE SPAWN DU JOUEUR !)
        // =========================================================================
        if (closestTarget == null)
        {
            PlanUnspottedPatrolBehavior(unit);
            return;
        }

        // =========================================================================
        // COMPORTEMENT 1 : MORTIER D'ARTILLERIE (Tir sur cible confirmée)
        // =========================================================================
        if (unit.isMortar)
        {
            PlanMortarBehavior(unit, closestTarget, closestDist);
            return;
        }

        // =========================================================================
        // COMPORTEMENT 2 : BLINDÉS (Char Leopard 2 & Véhicule Canon)
        // =========================================================================
        if (unit.isTank)
        {
            PlanTankBehavior(unit, closestTarget, closestDist);
            return;
        }

        // =========================================================================
        // COMPORTEMENT 3 : FANTASSINS (Infanterie - Toits, Bâtiments, Barricades)
        // =========================================================================
        PlanInfantryBehavior(unit, closestTarget, closestDist);
    }

    /// <summary>
    /// Comportement en phase de recherche / brouillard de guerre lorsqu'aucun soldat joueur n'a été repéré.
    /// </summary>
    private static void PlanUnspottedPatrolBehavior(UnitAI unit)
    {
        // 1. Les mortiers restent en attente / réserve et ne tirent JAMAIS sans coordonnées
        if (unit.isMortar)
        {
            unit.AddTacticalNode(new TacticalPathManager.TacticalNode
            {
                position = unit.transform.position,
                action = TacticalPathManager.NodeAction.Guetter
            });
            return;
        }

        // 2. L'infanterie cherche un toit proche pour prendre de la hauteur et repérer le joueur
        if (!unit.isTank && !unit.isRooftopSniper)
        {
            BuildingStructure bestScoutBuilding = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < BuildingStructure.AllBuildings.Count; i++)
            {
                BuildingStructure b = BuildingStructure.AllBuildings[i];
                if (b == null) continue;
                float d = Vector3.Distance(unit.transform.position, b.transform.position);
                if (d < 30f && d < bestDist)
                {
                    bestDist = d;
                    bestScoutBuilding = b;
                }
            }

            if (bestScoutBuilding != null)
            {
                Collider bCol = bestScoutBuilding.GetComponent<Collider>();
                if (bCol != null && bCol.bounds.size.y > 3.5f)
                {
                    Vector3 roofPos = new Vector3(bCol.bounds.center.x, bCol.bounds.max.y, bCol.bounds.center.z);
                    unit.AddTacticalNode(new TacticalPathManager.TacticalNode
                    {
                        position = roofPos,
                        action = TacticalPathManager.NodeAction.Escalade
                    });
                    Debug.Log($"<color=cyan>[TacticalAI] 🔭 Fantassin {unit.gameObject.name} escalade pour repérer les positions ennemies.</color>");
                    return;
                }
            }
        }

        // 3. Blindés et patrouilles : Progression tactique vers le centre de la ville
        Vector3 patrolPoint = Vector3.zero; // Centre de la carte
        MoveTowardsTarget(unit, patrolPoint, 15f);
    }

    private static void PlanMortarBehavior(UnitAI unit, UnitAI target, float dist)
    {
        // Si la cible est à portée de tir d'artillerie (15m à 120m)
        if (dist <= 120f)
        {
            // Planifie directement un tir de mortier parabolique avec salve AoE
            unit.AddTacticalNode(new TacticalPathManager.TacticalNode
            {
                position = target.transform.position,
                action = TacticalPathManager.NodeAction.TirMortier
            });
            Debug.Log($"<color=red>[TacticalAI] 🎯 Mortier Ennemi {unit.gameObject.name} verrouille la position joueur à {dist:F0}m pour un bombardement !</color>");
        }
        else
        {
            // Se rapproche pour se mettre à portée d'artillerie
            MoveTowardsTarget(unit, target.transform.position, 80f);
        }
    }

    private static void PlanTankBehavior(UnitAI unit, UnitAI target, float dist)
    {
        // Si le char est déjà à bonne distance d'engagement (18m à 35m)
        if (dist >= 18f && dist <= 35f)
        {
            // Posture de guet tourelle 360° pour faire feu
            unit.AddTacticalNode(new TacticalPathManager.TacticalNode
            {
                position = unit.transform.position,
                action = TacticalPathManager.NodeAction.Guetter
            });
            return;
        }

        // Sinon avance le long de la rue en s'arrêtant à 20m du joueur
        MoveTowardsTarget(unit, target.transform.position, 20f);
    }

    private static void PlanInfantryBehavior(UnitAI unit, UnitAI target, float dist)
    {
        Vector3 unitPos = unit.transform.position;

        // A. Si le soldat est DÉJÀ sur un toit (Sniper Haut)
        if (unit.isRooftopSniper || unitPos.y > 2.2f)
        {
            // Reste sur le toit en posture de guet pour tirer avec portée accrue
            unit.AddTacticalNode(new TacticalPathManager.TacticalNode
            {
                position = unitPos,
                action = TacticalPathManager.NodeAction.Guetter
            });
            return;
        }

        // B. STRATÉGIE TOIT : Chercher un bâtiment proche avec toit praticable pour sniper
        if (dist > 15f && dist < 70f)
        {
            BuildingStructure bestRoofBuilding = null;
            float bestRoofDist = float.MaxValue;

            for (int i = 0; i < BuildingStructure.AllBuildings.Count; i++)
            {
                BuildingStructure b = BuildingStructure.AllBuildings[i];
                if (b == null) continue;
                float d = Vector3.Distance(unitPos, b.transform.position);
                if (d < 35f && d < bestRoofDist)
                {
                    bestRoofDist = d;
                    bestRoofBuilding = b;
                }
            }

            if (bestRoofBuilding != null)
            {
                Collider bCol = bestRoofBuilding.GetComponent<Collider>();
                float bHeight = (bCol != null) ? bCol.bounds.size.y : 10f;
                if (bHeight > 3.5f)
                {
                    Vector3 roofTarget = (bCol != null) ? new Vector3(bCol.bounds.center.x, bCol.bounds.max.y, bCol.bounds.center.z) : (bestRoofBuilding.transform.position + Vector3.up * 10f);
                    unit.AddTacticalNode(new TacticalPathManager.TacticalNode
                    {
                        position = roofTarget,
                        action = TacticalPathManager.NodeAction.Escalade
                    });
                    Debug.Log($"<color=green>[TacticalAI] 🧗 Fantassin Ennemi {unit.gameObject.name} choisit d'escalader {bestRoofBuilding.gameObject.name} pour se poster sur le Toit (AK-47) !</color>");
                    return;
                }
            }
        }

        // C. STRATÉGIE BARRICADE : Chercher une barricade routière proche pour couverture lourde
        if (RoadBarrier.AllBarriers.Count > 0)
        {
            RoadBarrier bestBarrier = null;
            float bestBarrierDist = float.MaxValue;

            for (int i = 0; i < RoadBarrier.AllBarriers.Count; i++)
            {
                RoadBarrier b = RoadBarrier.AllBarriers[i];
                if (b != null)
                {
                    float d = Vector3.Distance(unitPos, b.transform.position);
                    if (d < 20f && d < bestBarrierDist)
                    {
                        bestBarrierDist = d;
                        bestBarrier = b;
                    }
                }
            }

            if (bestBarrier != null && bestBarrierDist < 18f)
            {
                Vector3 coverPos = bestBarrier.transform.position + (bestBarrier.transform.position - target.transform.position).normalized * 1.2f;
                unit.AddTacticalNode(new TacticalPathManager.TacticalNode
                {
                    position = coverPos,
                    action = TacticalPathManager.NodeAction.Guetter
                });
                Debug.Log($"<color=cyan>[TacticalAI] 🚧 Fantassin Ennemi {unit.gameObject.name} se retranche derrière la Barricade Routière !</color>");
                return;
            }
        }

        // D. STRATÉGIE ASSAUT / RAPPROCHEMENT STANDARD
        MoveTowardsTarget(unit, target.transform.position, 6.0f);
    }

    private static void MoveTowardsTarget(UnitAI unit, Vector3 targetWorldPos, float stopDistance)
    {
        NavMeshAgent agent = unit.GetComponent<NavMeshAgent>();
        if (agent == null) return;

        Vector3 startPos = unit.transform.position;
        if (unit.isTank && unit.obstacle != null && unit.obstacle.enabled)
        {
            if (NavMesh.SamplePosition(startPos, out NavMeshHit startHit, 5.0f, agent.areaMask))
            {
                startPos = startHit.position;
            }
        }

        NavMeshPath path = new NavMeshPath();
        if (NavMesh.CalculatePath(startPos, targetWorldPos, agent.areaMask, path) && path.corners.Length > 1)
        {
            float totalPathLength = 0f;
            for (int i = 0; i < path.corners.Length - 1; i++)
            {
                totalPathLength += Vector3.Distance(path.corners[i], path.corners[i + 1]);
            }

            float targetDistAlongPath = Mathf.Min(unit.maxMovementPerTurn, totalPathLength - stopDistance);

            if (targetDistAlongPath <= 0.5f)
            {
                unit.AddTacticalNode(new TacticalPathManager.TacticalNode
                {
                    position = unit.transform.position,
                    action = TacticalPathManager.NodeAction.Guetter
                });
                return;
            }

            float accumulatedDist = 0f;
            Vector3 finalNodePos = path.corners[path.corners.Length - 1];

            for (int i = 0; i < path.corners.Length - 1; i++)
            {
                float segmentLen = Vector3.Distance(path.corners[i], path.corners[i + 1]);
                if (accumulatedDist + segmentLen >= targetDistAlongPath)
                {
                    float remaining = targetDistAlongPath - accumulatedDist;
                    Vector3 dir = (path.corners[i + 1] - path.corners[i]).normalized;
                    finalNodePos = path.corners[i] + dir * remaining;
                    break;
                }
                accumulatedDist += segmentLen;
            }

            unit.AddTacticalNode(new TacticalPathManager.TacticalNode
            {
                position = finalNodePos,
                action = TacticalPathManager.NodeAction.Continuer
            });
        }
    }
}
