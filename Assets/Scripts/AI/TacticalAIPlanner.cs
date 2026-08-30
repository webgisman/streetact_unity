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
        // Cette IA ne planifie jamais pour une unité du joueur SAUF si son propriétaire vient
        // d'être marqué "ghost" ce tour-ci (absent/déconnecté/pas soumis à temps, voir
        // MatchSessionManager.ApplyForPlayer) — dans ce cas précis, elle prend temporairement le
        // relais plutôt que de laisser l'unité totalement immobile face à un adversaire humain.
        if (unit.isPlayerControlled && !unit.isGhosted) return;

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
                if (bCol != null && bCol.bounds.size.y > 3.5f && IsBuildingReachableWithinBudget(unit, bestScoutBuilding, out _))
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

    private const float MortarMinRange = 15f;
    private const float MortarMaxRange = 120f;

    private static void PlanMortarBehavior(UnitAI unit, UnitAI target, float dist)
    {
        // Trop proche : son propre souffle (MortarShell.ApplySplashDamage, rayon 6.5m) toucherait le
        // mortier lui-même. Se replier au lieu de tirer, plutôt que de se suicider à bout portant.
        if (dist < MortarMinRange)
        {
            Vector3 retreatDir = (unit.transform.position - target.transform.position).normalized;
            if (retreatDir == Vector3.zero) retreatDir = Vector3.forward;
            Vector3 retreatPoint = unit.transform.position + retreatDir * (MortarMinRange + 5f - dist);
            MoveTowardsTarget(unit, retreatPoint, 0f);
            Debug.Log($"<color=yellow>[TacticalAI] ⚠️ Mortier Ennemi {unit.gameObject.name} recule, cible trop proche ({dist:F0}m) pour tirer sans se blesser.</color>");
            return;
        }

        // Si la cible est à portée de tir d'artillerie (15m à 120m)
        if (dist <= MortarMaxRange)
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

    private const float TankMinEngageDist = 18f;
    private const float TankMaxEngageDist = 35f;

    private static void PlanTankBehavior(UnitAI unit, UnitAI target, float dist)
    {
        // Si le char est déjà à bonne distance d'engagement (18m à 35m)
        if (dist >= TankMinEngageDist && dist <= TankMaxEngageDist)
        {
            // Posture de guet tourelle 360° pour faire feu
            unit.AddTacticalNode(new TacticalPathManager.TacticalNode
            {
                position = unit.transform.position,
                action = TacticalPathManager.NodeAction.Guetter
            });
            return;
        }

        // Trop proche : "avancer en s'arrêtant à 20m" n'a aucun effet quand on est déjà plus près que
        // la distance d'arrêt (MoveTowardsTarget calcule alors une distance négative et se contente
        // d'un Guetter sur place) — reculer explicitement pour reprendre sa distance d'engagement.
        if (dist < TankMinEngageDist)
        {
            Vector3 retreatDir = (unit.transform.position - target.transform.position).normalized;
            if (retreatDir == Vector3.zero) retreatDir = Vector3.forward;
            Vector3 retreatPoint = unit.transform.position + retreatDir * (TankMaxEngageDist - dist);
            MoveTowardsTarget(unit, retreatPoint, 0f);
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
                if (bHeight > 3.5f && IsBuildingReachableWithinBudget(unit, bestRoofBuilding, out _))
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

                // La position de couverture brute peut tomber hors NavMesh (ou dans l'obstacle de la
                // barricade elle-même) — valider avant de commander une destination inatteignable qui
                // ferait échouer silencieusement le déplacement de l'unité ce tour-ci.
                NavMeshAgent coverAgent = unit.GetComponent<NavMeshAgent>();
                int coverAreaMask = coverAgent != null ? coverAgent.areaMask : NavMesh.AllAreas;
                if (NavMesh.SamplePosition(coverPos, out NavMeshHit coverHit, 3.0f, coverAreaMask))
                {
                    unit.AddTacticalNode(new TacticalPathManager.TacticalNode
                    {
                        position = coverHit.position,
                        action = TacticalPathManager.NodeAction.Guetter
                    });
                    Debug.Log($"<color=cyan>[TacticalAI] 🚧 Fantassin Ennemi {unit.gameObject.name} se retranche derrière la Barricade Routière !</color>");
                    return;
                }
            }
        }

        // D. STRATÉGIE ASSAUT / RAPPROCHEMENT STANDARD
        MoveTowardsTarget(unit, target.transform.position, 6.0f);
    }

    /// <summary>
    /// Vrai si le pied de <paramref name="building"/> est atteignable ce tour-ci dans le budget de
    /// mouvement de l'unité — contrairement à un ordre Escalade posé directement sans passer par
    /// MoveTowardsTarget, qui ignorait jusqu'ici totalement <c>unit.maxMovementPerTurn</c>.
    /// </summary>
    private static bool IsBuildingReachableWithinBudget(UnitAI unit, BuildingStructure building, out Vector3 basePosOnNavMesh)
    {
        basePosOnNavMesh = building.transform.position;
        NavMeshAgent agent = unit.GetComponent<NavMeshAgent>();
        if (agent == null) return false;

        if (!NavMesh.SamplePosition(building.transform.position, out NavMeshHit hit, 8f, agent.areaMask)) return false;
        basePosOnNavMesh = hit.position;

        NavMeshPath path = new NavMeshPath();
        if (!NavMesh.CalculatePath(unit.transform.position, basePosOnNavMesh, agent.areaMask, path) || path.corners.Length < 2) return false;

        float len = 0f;
        for (int i = 0; i < path.corners.Length - 1; i++) len += Vector3.Distance(path.corners[i], path.corners[i + 1]);
        return len <= unit.maxMovementPerTurn;
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
        else
        {
            // Cible génuinement inatteignable (ex: coupée par des décombres) — sans ce repli,
            // l'unité ne recevait aucun nœud tactique et restait totalement inactive, tour après
            // tour, sans jamais retenter une autre approche.
            unit.AddTacticalNode(new TacticalPathManager.TacticalNode
            {
                position = unit.transform.position,
                action = TacticalPathManager.NodeAction.Guetter
            });
        }
    }
}
