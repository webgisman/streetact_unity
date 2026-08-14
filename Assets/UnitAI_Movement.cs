using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public partial class UnitAI
{
    // ==========================================
    // MÉCANIQUES DE DÉPLACEMENT ET NAVMESH
    // ==========================================

    /// <summary>
    /// Appelé quand le NavMesh est généré et prêt.
    /// Configure l'agent pour le déplacement.
    /// </summary>
    public void OnNavMeshReady()
    {
        navMeshReady = true;
        
        // Réactiver l'agent et le placer sur le NavMesh
        agent.enabled = true;
        
        // Configuration fluide de l'agent
        agent.speed = 4.0f;
        agent.angularSpeed = 360f; // Vitesse de rotation plus naturelle
        agent.acceleration = 8f;   // Accélération par défaut Unity pour éviter le sur-dépassement
        
        if (isTank)
        {
            agent.radius = isCanonVehicle ? 1.5f : 3.0f;
            agent.height = 3f;
        }
        else
        {
            agent.radius = 0.5f;
            agent.height = 2f;
        }
        
        agent.stoppingDistance = 0.5f;
        agent.autoBraking = true;
        
        // ACTIVER l'évitement dynamique (RVO) pour que les véhicules se contournent
        agent.obstacleAvoidanceType = UnityEngine.AI.ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        agent.avoidancePriority = isPlayerControlled ? 30 : 60;

        // Exclure "Not Walkable" (Area 1) du masque.
        int notWalkableArea = NavMesh.GetAreaFromName("Not Walkable");
        agent.areaMask = ~(1 << notWalkableArea);
        
        // Forcer le placement sur le NavMesh le plus proche
        NavMeshHit hit;
        if (NavMesh.SamplePosition(transform.position, out hit, 50f, NavMesh.AllAreas))
        {
            agent.Warp(hit.position);
            Debug.Log($"[UnitAI {gameObject.name}] Placé sur le NavMesh à {hit.position}");
        }
    }

    /// <summary>
    /// Ajoute un nœud à la trajectoire tactique de l'unité.
    /// </summary>
    public void AddTacticalNode(TacticalPathManager.TacticalNode node)
    {
        tacticalPath.Add(node);
    }

    /// <summary>
    /// Intelligence Artificielle basique pour les ennemis : trouver et foncer sur le joueur le plus proche.
    /// </summary>
    public void PlanifierTourIA()
    {
        if (isPlayerControlled) return;

        if (!navMeshReady)
        {
            UnityEngine.AI.NavMeshHit checkHit;
            if (UnityEngine.AI.NavMesh.SamplePosition(transform.position, out checkHit, 15f, UnityEngine.AI.NavMesh.AllAreas))
            {
                OnNavMeshReady();
            }
        }

        if (!navMeshReady) return;

        tacticalPath.Clear();
        currentNodeIndex = 0;

        // 1. Trouver l'unité du joueur la plus proche
        UnitAI[] allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude);
        UnitAI closestPlayer = null;
        float closestDist = float.MaxValue;

        foreach (var unit in allUnits)
        {
            if (unit != null && !unit.isDead && unit.isPlayerControlled)
            {
                float d = Vector3.Distance(transform.position, unit.transform.position);
                if (d < closestDist)
                {
                    closestDist = d;
                    closestPlayer = unit;
                }
            }
        }

        if (closestPlayer == null) return;

        // 2. Calculer le chemin vers le joueur
        Vector3 startPos = transform.position;
        // Si c'est un char à l'arrêt, son centre est creusé dans le NavMesh par l'obstacle. On trouve le point valide le plus proche.
        if (isTank && obstacle != null && obstacle.enabled)
        {
            UnityEngine.AI.NavMeshHit startHit;
            if (UnityEngine.AI.NavMesh.SamplePosition(startPos, out startHit, 5.0f, agent.areaMask))
            {
                startPos = startHit.position;
            }
        }

        UnityEngine.AI.NavMeshPath path = new UnityEngine.AI.NavMeshPath();
        if (UnityEngine.AI.NavMesh.CalculatePath(startPos, closestPlayer.transform.position, agent.areaMask, path) && path.corners.Length > 1)
        {
            float totalPathLength = 0f;
            for (int i = 0; i < path.corners.Length - 1; i++)
            {
                totalPathLength += Vector3.Distance(path.corners[i], path.corners[i + 1]);
            }

            // L'IA doit toujours s'arrêter à 3m pour ne jamais chevaucher le joueur
            float stopDistanceBeforePlayer = 3.0f;
            float targetDistanceAlongPath = Mathf.Min(maxMovementPerTurn, totalPathLength - stopDistanceBeforePlayer);

            if (targetDistanceAlongPath <= 0.5f)
            {
                AddTacticalNode(new TacticalPathManager.TacticalNode { position = transform.position, action = TacticalPathManager.NodeAction.Attendre5Min });
                return;
            }

            float accumulatedDist = 0f;
            Vector3 targetPosition = path.corners[path.corners.Length - 1];

            for (int i = 0; i < path.corners.Length - 1; i++)
            {
                float segmentLength = Vector3.Distance(path.corners[i], path.corners[i + 1]);
                if (accumulatedDist + segmentLength >= targetDistanceAlongPath)
                {
                    float remainingOnSegment = targetDistanceAlongPath - accumulatedDist;
                    Vector3 direction = (path.corners[i + 1] - path.corners[i]).normalized;
                    targetPosition = path.corners[i] + direction * remainingOnSegment;
                    break;
                }
                accumulatedDist += segmentLength;
            }

            AddTacticalNode(new TacticalPathManager.TacticalNode { position = targetPosition, action = TacticalPathManager.NodeAction.Continuer });
        }
    }

    /// <summary>
    /// Lance l'exécution des déplacements planifiés.
    /// </summary>
    public void ExecuterOrdres()
    {
        if (tacticalPath.Count > 0)
        {
            isExecuting = true;
            SetObstacleMode(false); // Le char redevient un Agent pour se déplacer
            StopAllCoroutines();
            StartCoroutine(ExecuteMovementCoroutine());
        }
        else
        {
            // Pas d'ordres de mouvement : l'unité reste en garde et fera feu sur les ennemis à portée
            isExecuting = false;
        }
    }

    public int GetCurrentNodeIndex()
    {
        return currentNodeIndex;
    }

    /// <summary>
    /// Coroutine gérant le déplacement point par point avec système d'esquive intelligente (Yielding).
    /// </summary>
    private IEnumerator ExecuteMovementCoroutine()
    {
        // 0. Attendre que l'Agent soit actif sur le NavMesh avant de donner des ordres
        if (isTank && agent != null && !agent.isOnNavMesh)
        {
            yield return new WaitUntil(() => agent.isOnNavMesh);
        }

        for (currentNodeIndex = 0; currentNodeIndex < tacticalPath.Count; currentNodeIndex++)
        {
            Vector3 targetPos = tacticalPath[currentNodeIndex].position;
            
            if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh) break;

            agent.isStopped = false;
            agent.stoppingDistance = 0.5f;
            agent.autoBraking = true;
            agent.SetDestination(targetPos);
            
            // 1. Attendre que le NavMesh démarre
            yield return null;
            while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.pathPending) yield return null;

            // 2. Variables de sécurité Anti-Blocage
            float stuckTimer = 0f;
            Vector3 lastPos = transform.position;

            // 3. Attendre l'arrivée
            while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath && agent.remainingDistance > agent.stoppingDistance)
            {
                if (isDead) yield break;

                // Vérifier si le chemin est toujours en calcul
                if (agent.pathPending) yield return null;
                
                // Si l'agent est en train de tirer, il est arrêté par Update(), on attend
                if (agent.isStopped)
                {
                    yield return null;
                    continue; // Empêche l'activation du système "Stuck" pendant qu'on vise/tire
                }

                // Si l'agent n'a presque pas bougé depuis la frame précédente
                if (Vector3.Distance(transform.position, lastPos) < 0.002f)
                {
                    stuckTimer += Time.deltaTime;
                    if (stuckTimer > stuckThreshold)
                    {
                        if (!isTank)
                        {
                            Debug.LogWarning($"[{gameObject.name}] Infanterie bloquée ! Annulation de la fin de trajectoire.");
                            break;
                        }
                        else
                        {
                            Debug.Log($"<color=yellow>[{gameObject.name}] Face-à-face ! Je cède le passage pendant 2s.</color>");
                            
                            // Esquive intelligente : Le char se transforme en mur pour obliger l'autre à le contourner
                            SetObstacleMode(true);
                            yield return new WaitForSeconds(2.0f);
                            
                            // Je reprends la route
                            SetObstacleMode(false);
                            if (agent != null && !agent.isOnNavMesh) yield return new WaitUntil(() => agent.isOnNavMesh);
                            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.SetDestination(targetPos);
                            
                            stuckTimer = 0f;
                            stuckThreshold = Random.Range(1.5f, 3.0f); // Nouveau seuil de patience
                            lastPos = transform.position;
                        }
                    }
                }
                else
                {
                    stuckTimer = 0f;
                    lastPos = transform.position;
                }

                yield return null;
            }

            // Exécution réelle de l'action tactique associée au checkpoint
            TacticalPathManager.NodeAction nodeAction = tacticalPath[currentNodeIndex].action;

            // Vérifier s'il s'agit d'une entrée en garnison / prise de fenêtre
            if (!isTank && nodeAction == TacticalPathManager.NodeAction.GarnisonFenetre)
            {
                yield return StartCoroutine(ExecuteEnterGarrison(targetPos));
                continue;
            }

            // Vérifier s'il s'agit d'une escalade ou d'un changement d'élévation (Sol <-> Toit)
            float heightDelta = targetPos.y - transform.position.y;
            bool requiresClimb = !isTank && (nodeAction == TacticalPathManager.NodeAction.Escalade || Mathf.Abs(heightDelta) > 2.0f);

            if (requiresClimb)
            {
                yield return StartCoroutine(ExecuteClimb(targetPos));
                continue;
            }

            if (nodeAction == TacticalPathManager.NodeAction.Attendre5Min)
            {
                isPerformingCheckpointAction = true;
                Debug.Log($"<color=cyan>[{gameObject.name}] Halte tactique au checkpoint : pause de 2.5s en couverture.</color>");
                if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.isStopped = true;
                if (animator != null && !isTank) animator.SetFloat("Speed", 0f);

                float waitTimer = 0f;
                while (waitTimer < 2.5f && !isDead)
                {
                    waitTimer += Time.deltaTime;
                    yield return null;
                }
                isPerformingCheckpointAction = false;
            }
            else if (nodeAction == TacticalPathManager.NodeAction.Guetter)
            {
                isPerformingCheckpointAction = true;
                Debug.Log($"<color=cyan>[{gameObject.name}] Guet / Surveillance du secteur pendant 2.0s.</color>");
                if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.isStopped = true;
                if (animator != null && !isTank) animator.SetFloat("Speed", 0f);

                float lookTimer = 0f;
                while (lookTimer < 2.0f && !isDead)
                {
                    lookTimer += Time.deltaTime;
                    yield return null;
                }
                isPerformingCheckpointAction = false;
            }
            else if (nodeAction == TacticalPathManager.NodeAction.Embuscade)
            {
                isPerformingCheckpointAction = true;
                Debug.Log($"<color=cyan>[{gameObject.name}] Posture d'embuscade et tir d'opportunité pendant 2.0s.</color>");
                if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.isStopped = true;
                if (animator != null && !isTank) animator.SetFloat("Speed", 0f);

                float ambushTimer = 0f;
                while (ambushTimer < 2.0f && !isDead)
                {
                    ambushTimer += Time.deltaTime;
                    yield return null;
                }
                isPerformingCheckpointAction = false;
            }
        }

        TerminerOrdres();
    }

    /// <summary>
    /// Coroutine gérant l'ascension verticale le long de la façade extérieure (sans traverser l'intérieur du polygone)
    /// puis le rétablissement sur le toit avec l'animation Freehang Climb.
    /// </summary>
    private IEnumerator ExecuteClimb(Vector3 destinationRoof)
    {
        isClimbing = true;
        isPerformingCheckpointAction = true;

        Vector3 startPos = transform.position;
        Vector3 horizontalDir = (destinationRoof - startPos);
        horizontalDir.y = 0;
        float horizontalDist = horizontalDir.magnitude;
        Vector3 forwardNorm = (horizontalDist > 0.01f) ? horizontalDir.normalized : transform.forward;

        // 1. CALCUL DU POINT D'IMPACT SUR LA FAÇADE EXTÉRIEURE DU BÂTIMENT
        Vector3 wallHitPoint = startPos;
        Vector3 wallNormal = -forwardNorm;
        bool foundWall = false;

        RaycastHit wallHit;
        if (Physics.Raycast(startPos + Vector3.up * 1.0f, forwardNorm, out wallHit, horizontalDist + 5f))
        {
            if (wallHit.collider.name.Contains("Batiment") || wallHit.collider.name.Contains("Building") || wallHit.point.y > 0.5f)
            {
                wallHitPoint = wallHit.point;
                wallNormal = wallHit.normal;
                foundWall = true;
            }
        }

        if (!foundWall)
        {
            wallHitPoint = startPos + forwardNorm * Mathf.Max(0.5f, horizontalDist - 2.0f);
            wallNormal = -forwardNorm;
        }

        // Le point de départ d'escalade est à 0.4m À L'EXTÉRIEUR du mur au sol
        Vector3 climbBasePos = new Vector3(wallHitPoint.x, startPos.y, wallHitPoint.z) + wallNormal * 0.4f;
        // Le point haut d'escalade est au niveau du toit, TOUJOURS à 0.4m À L'EXTÉRIEUR du mur
        Vector3 climbTopPos = new Vector3(climbBasePos.x, destinationRoof.y, climbBasePos.z);
        // Le point de réception sur le toit
        Vector3 roofLandPos = destinationRoof;

        // --- PHASE 1 : MARCHE AU SOL JUSQU'AU PIED DU MUR ---
        if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
        {
            agent.isStopped = false;
            agent.SetDestination(climbBasePos);
            while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath && agent.remainingDistance > 0.6f)
            {
                if (isDead) yield break;
                yield return null;
            }
        }

        if (agent != null) agent.enabled = false;

        // Tourner le soldat face à la façade
        transform.rotation = Quaternion.LookRotation(-wallNormal);
        transform.position = climbBasePos;

        if (animator != null)
        {
            animator.SetBool("IsClimbing", true);
            animator.SetFloat("Speed", 0f);
        }

        // --- PHASE 2 : ASCENSION STRICTEMENT VERTICALE SUR LA FAÇADE EXTÉRIEURE ---
        float heightDiff = Mathf.Abs(climbTopPos.y - climbBasePos.y);
        float climbDuration = Mathf.Max(2.0f, heightDiff * 0.35f);
        float elapsed = 0f;

        Debug.Log($"<color=green>[{gameObject.name}] 🧗 Escalade sur la façade extérieure (Hauteur: {heightDiff:F1}m)...</color>");

        while (elapsed < climbDuration && !isDead)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / climbDuration);

            // Ascension STRICTEMENT verticale (X et Z restent constants, seul Y monte le long de la façade !)
            transform.position = Vector3.Lerp(climbBasePos, climbTopPos, t);
            yield return null;
        }

        // --- PHASE 3 : ENJAMBEMENT DU REBORD SUR LE TOIT ---
        float stepElapsed = 0f;
        Vector3 stepStart = climbTopPos;
        Vector3 stepTarget = climbTopPos - wallNormal * 0.8f;
        while (stepElapsed < 0.4f && !isDead)
        {
            stepElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(stepElapsed / 0.4f);
            transform.position = Vector3.Lerp(stepStart, stepTarget, t);
            yield return null;
        }

        if (animator != null)
        {
            animator.SetBool("IsClimbing", false);
        }

        isClimbing = false;
        isPerformingCheckpointAction = false;

        // Reconnexion propre au NavMesh du toit
        if (agent != null)
        {
            agent.enabled = true;
            if (NavMesh.SamplePosition(destinationRoof, out NavMeshHit roofHit, 4.0f, NavMesh.AllAreas))
            {
                agent.Warp(roofHit.position);
            }
            else
            {
                agent.Warp(stepTarget);
            }
        }

        Debug.Log($"<color=green>[{gameObject.name}] 🎖️ Position sur le toit sécurisée !</color>");
    }

    /// <summary>
    /// Coroutine gérant le déplacement vers la porte du bâtiment et la prise de poste à la fenêtre (Garnison & Couverture).
    /// </summary>
    private IEnumerator ExecuteEnterGarrison(Vector3 windowTargetPos)
    {
        isPerformingCheckpointAction = true;

        BuildingStructure structure = null;
        BuildingStructure.BuildingWindow targetWindow = null;

        BuildingStructure[] allBuildings = FindObjectsByType<BuildingStructure>(FindObjectsInactive.Exclude);
        foreach (var b in allBuildings)
        {
            BuildingStructure.BuildingWindow w = b.GetClosestWindow(windowTargetPos, false);
            if (w != null && Vector3.Distance(w.position, windowTargetPos) < 2.5f)
            {
                structure = b;
                targetWindow = w;
                break;
            }
        }

        if (structure != null && targetWindow != null)
        {
            // 1. Trouver la porte d'entrée au sol la plus proche
            BuildingStructure.BuildingDoor door = structure.GetClosestDoor(transform.position);
            Vector3 doorPos = (door != null) ? door.position : new Vector3(targetWindow.position.x, 0.05f, targetWindow.position.z);

            // 2. Marcher jusqu'à la porte
            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
            {
                agent.isStopped = false;
                agent.SetDestination(doorPos);
                while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath && agent.remainingDistance > 0.8f)
                {
                    if (isDead) yield break;
                    yield return null;
                }
            }

            // 3. Entrée dans le bâtiment
            if (agent != null) agent.enabled = false;
            if (animator != null) animator.SetFloat("Speed", 0f);

            Vector3 enterStart = transform.position;
            Vector3 windowStancePos = targetWindow.position - targetWindow.outwardNormal * 0.35f; // Positionné juste derrière la fenêtre à l'intérieur
            float transitTime = 1.0f;
            float elapsed = 0f;
            while (elapsed < transitTime && !isDead)
            {
                elapsed += Time.deltaTime;
                transform.position = Vector3.Lerp(enterStart, windowStancePos, elapsed / transitTime);
                yield return null;
            }

            // 4. Orientation face à la rue à travers la fenêtre
            transform.rotation = Quaternion.LookRotation(targetWindow.outwardNormal);
            transform.position = windowStancePos;

            // Assigner le statut de garnison
            structure.OccupyWindow(targetWindow, this);
            currentWindow = targetWindow;
            isGarrisoned = true;
            isPerformingCheckpointAction = false;

            Debug.Log($"<color=cyan>[{gameObject.name}] 🛡️ Retranché à la fenêtre (Étage {targetWindow.floorLevel}) ! Couverture lourde active (-75% dégâts).</color>");
        }
        else
        {
            isPerformingCheckpointAction = false;
        }
    }

    /// <summary>
    /// Invoqué quand l'unité a atteint sa destination finale.
    /// </summary>
    private void TerminerOrdres()
    {
        isExecuting = false;
        isPerformingCheckpointAction = false;
        tacticalPath.Clear(); // Les ordres sont consommés
        currentNodeIndex = 0;
        
        if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
        {
            agent.isStopped = true;
            agent.ResetPath();
        }
        
        SetObstacleMode(true); // À l'arrêt, le char redevient un mur

        // Arrêter l'animation de marche
        if (animator != null && !isTank)
        {
            animator.SetFloat("Speed", 0f);
        }
        
        Debug.Log($"[UnitAI {gameObject.name}] Arrivé à destination. Balayage et surveillance du secteur.");
    }
}
