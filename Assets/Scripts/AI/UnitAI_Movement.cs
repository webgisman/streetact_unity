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
        if (agent == null) agent = GetComponent<NavMeshAgent>();
        if (agent == null) return;

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
        isPathDirty = true;
        TacticalPathManager.SetPathsDirty();
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

        // Déléguer au planificateur tactique IA avancé (gestion des toits, mortiers, barricades, etc.)
        TacticalAIPlanner.PlanTurnForUnit(this);
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
        // 0. S'assurer que l'Agent est actif et positionné sur le NavMesh
        if (agent != null && !agent.isOnNavMesh && !isRooftopSniper && currentBuilding == null)
        {
            if (NavMesh.SamplePosition(transform.position, out NavMeshHit initHit, 6.0f, NavMesh.AllAreas))
            {
                agent.enabled = true;
                agent.Warp(initHit.position);
            }
            yield return null;
        }

        for (currentNodeIndex = 0; currentNodeIndex < tacticalPath.Count; currentNodeIndex++)
        {
            Vector3 targetPos = tacticalPath[currentNodeIndex].position;
            TacticalPathManager.NodeAction nodeAction = tacticalPath[currentNodeIndex].action;
            
            if (agent == null) break;

            // 1. ESCALADE OU DESCENTE DU TOIT (Priorité absolue avant tout calcul de chemin classique)
            float heightDelta = targetPos.y - transform.position.y;
            bool isClimbUp = !isTank && (nodeAction == TacticalPathManager.NodeAction.Escalade || heightDelta > 1.8f);
            bool isClimbDown = !isTank && (heightDelta < -1.8f);

            if (isClimbUp)
            {
                yield return StartCoroutine(ExecuteClimb(targetPos));
                continue;
            }
            else if (isClimbDown)
            {
                yield return StartCoroutine(ExecuteClimbDown(targetPos));
                continue;
            }

            // 2. ENTRÉE DIRECTE DANS LE BÂTIMENT PAR LA PORTE
            if (!isTank && nodeAction == TacticalPathManager.NodeAction.EntrerBatiment)
            {
                yield return StartCoroutine(ExecuteEnterBuilding(targetPos));
                continue;
            }

            // 3. SORTIE DIRECTE DU BÂTIMENT VERS LA RUE
            if (!isTank && nodeAction == TacticalPathManager.NodeAction.SortirBatiment)
            {
                yield return StartCoroutine(ExecuteExitBuilding(targetPos));
                continue;
            }

            // 4. TIR DE MORTIER (Bombardement AoE)
            if (nodeAction == TacticalPathManager.NodeAction.TirMortier)
            {
                yield return StartCoroutine(ExecuteMortarStrike(targetPos));
                continue;
            }

            // 5. DÉPLACEMENT SUR LA SURFACE DU TOIT (Infanterie en poste haut)
            if (!isTank && (isRooftopSniper || transform.position.y > 1.8f || targetPos.y > 1.8f))
            {
                if (agent != null && agent.enabled)
                {
                    agent.isStopped = true;
                    agent.ResetPath();
                    agent.enabled = false; // Évite que le NavMesh au sol n'aspire le soldat vers le bas
                }

                float roofY = transform.position.y;
                if (Physics.Raycast(new Vector3(targetPos.x, roofY + 3.0f, targetPos.z), Vector3.down, out RaycastHit rHit, 8.0f))
                {
                    roofY = rHit.point.y;
                }

                Vector3 roofMoveTarget = new Vector3(targetPos.x, roofY, targetPos.z);
                Vector3 roofDir = (roofMoveTarget - transform.position);
                roofDir.y = 0;
                if (roofDir.sqrMagnitude > 0.04f)
                {
                    transform.rotation = Quaternion.LookRotation(roofDir.normalized);
                    if (animator != null) animator.SetFloat("Speed", 2.5f);
                    while (Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z), new Vector3(roofMoveTarget.x, 0, roofMoveTarget.z)) > 0.4f && !isDead)
                    {
                        Vector3 curDir = (roofMoveTarget - transform.position);
                        curDir.y = 0;
                        if (curDir.sqrMagnitude > 0.01f)
                        {
                            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(curDir.normalized), 360f * Time.deltaTime);
                        }
                        transform.position = Vector3.MoveTowards(transform.position, roofMoveTarget, 3.5f * Time.deltaTime);
                        yield return null;
                    }
                    if (animator != null) animator.SetFloat("Speed", 0f);
                }
                isRooftopSniper = true;
                continue;
            }

            // 6. DÉPLACEMENT À L'INTÉRIEUR DU BÂTIMENT
            if (currentBuilding != null && !isRooftopSniper && (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh))
            {
                Vector3 interiorMoveTarget = new Vector3(targetPos.x, 0.05f, targetPos.z);
                Vector3 interiorDir = (interiorMoveTarget - transform.position);
                interiorDir.y = 0;
                if (interiorDir.sqrMagnitude > 0.04f)
                {
                    transform.rotation = Quaternion.LookRotation(interiorDir.normalized);
                    if (animator != null) animator.SetFloat("Speed", 2.5f);
                    while (Vector3.Distance(transform.position, interiorMoveTarget) > 0.4f && !isDead)
                    {
                        transform.position = Vector3.MoveTowards(transform.position, interiorMoveTarget, 3.5f * Time.deltaTime);
                        yield return null;
                    }
                    if (animator != null) animator.SetFloat("Speed", 0f);
                }
                continue;
            }

            // 7. DÉPLACEMENT SUR LES RUINES D'UN BÂTIMENT DÉTRUIT (Franchissement direct sans obstacle pour Engins et Troupes)
            if (DestructibleEnvironment.IsPositionInRubble(targetPos) || DestructibleEnvironment.IsPositionInRubble(transform.position))
            {
                Vector3 rubbleTarget = new Vector3(targetPos.x, 0.05f, targetPos.z);
                Vector3 rubbleDir = (rubbleTarget - transform.position);
                rubbleDir.y = 0;
                if (rubbleDir.sqrMagnitude > 0.04f)
                {
                    bool wasAgentEnabled = (agent != null && agent.enabled);
                    if (agent != null && agent.enabled)
                    {
                        agent.isStopped = true;
                        agent.ResetPath();
                        agent.enabled = false; // CRITIQUE : Empêche le NavMesh d'éjecter le char hors des ruines
                    }

                    if (animator != null && !isTank) animator.SetFloat("Speed", 2.5f);

                    float moveSpeed = isTank ? 3.8f : 4.2f;
                    float rotSpeed = isTank ? 180f : 360f;

                    while (Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z), new Vector3(rubbleTarget.x, 0, rubbleTarget.z)) > 0.5f && !isDead)
                    {
                        Vector3 currentDir = (rubbleTarget - transform.position);
                        currentDir.y = 0;
                        if (currentDir.sqrMagnitude > 0.01f)
                        {
                            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(currentDir.normalized), rotSpeed * Time.deltaTime);
                        }
                        transform.position = Vector3.MoveTowards(transform.position, rubbleTarget, moveSpeed * Time.deltaTime);
                        yield return null;
                    }

                    if (animator != null && !isTank) animator.SetFloat("Speed", 0f);

                    if (wasAgentEnabled && agent != null)
                    {
                        if (NavMesh.SamplePosition(transform.position, out NavMeshHit nh, 5.0f, NavMesh.AllAreas))
                        {
                            agent.enabled = true;
                            agent.Warp(nh.position);
                        }
                    }
                }
                continue;
            }

            if (!agent.isActiveAndEnabled || !agent.isOnNavMesh)
            {
                // Avancement direct vers la cible si hors-NavMesh ou sur un décor aplati
                Vector3 directTarget = new Vector3(targetPos.x, transform.position.y, targetPos.z);
                Vector3 directDir = (directTarget - transform.position);
                directDir.y = 0;
                if (directDir.sqrMagnitude > 0.04f)
                {
                    if (animator != null && !isTank) animator.SetFloat("Speed", 2.5f);
                    float moveSpeed = isTank ? 3.8f : 4.0f;
                    while (Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z), new Vector3(directTarget.x, 0, directTarget.z)) > 0.4f && !isDead)
                    {
                        Vector3 cDir = (directTarget - transform.position);
                        cDir.y = 0;
                        if (cDir.sqrMagnitude > 0.01f)
                        {
                            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(cDir.normalized), 360f * Time.deltaTime);
                        }
                        transform.position = Vector3.MoveTowards(transform.position, directTarget, moveSpeed * Time.deltaTime);
                        yield return null;
                    }
                    if (animator != null && !isTank) animator.SetFloat("Speed", 0f);
                }
                continue;
            }

            agent.isStopped = false;
            agent.stoppingDistance = 0.5f;
            agent.autoBraking = true;
            agent.SetDestination(targetPos);
            
            // Attendre que le NavMesh démarre
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

                    // Si l'engin est bloqué sur le bord d'une ruine détruite, forcer le passage direct
                    if (stuckTimer > 1.2f && (DestructibleEnvironment.IsPositionInRubble(targetPos) || DestructibleEnvironment.IsPositionInRubble(transform.position)))
                    {
                        if (agent != null && agent.enabled)
                        {
                            agent.isStopped = true;
                            agent.ResetPath();
                            agent.enabled = false;
                        }
                        float moveSpeed = isTank ? 3.8f : 4.2f;
                        while (Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z), new Vector3(targetPos.x, 0, targetPos.z)) > 0.5f && !isDead)
                        {
                            Vector3 curDir = (targetPos - transform.position);
                            curDir.y = 0;
                            if (curDir.sqrMagnitude > 0.01f)
                            {
                                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(curDir.normalized), 180f * Time.deltaTime);
                            }
                            transform.position = Vector3.MoveTowards(transform.position, new Vector3(targetPos.x, 0.05f, targetPos.z), moveSpeed * Time.deltaTime);
                            yield return null;
                        }
                        if (NavMesh.SamplePosition(transform.position, out NavMeshHit warpHit, 5.0f, NavMesh.AllAreas))
                        {
                            agent.enabled = true;
                            agent.Warp(warpHit.position);
                        }
                        break;
                    }

                    if (stuckTimer > 4.5f)
                    {
                        if (!isTank)
                        {
                            // Réinitialiser la destination plutôt que d'annuler le mouvement
                            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                            {
                                agent.SetDestination(targetPos);
                            }
                            stuckTimer = 0f;
                        }
                        else
                        {
                            Debug.Log($"<color=yellow>[{gameObject.name}] Face-à-face ! Je cède le passage pendant 2s.</color>");
                            
                            // Esquive intelligente : Le char se transforme en mur pour obliger l'autre à le contourner
                            SetObstacleMode(true);
                            yield return new WaitForSeconds(2.0f);
                            
                            // Je reprends la route
                            SetObstacleMode(false);
                            yield return null;
                            if (agent != null && !agent.isOnNavMesh && NavMesh.SamplePosition(transform.position, out NavMeshHit warpHit, 5.0f, NavMesh.AllAreas))
                            {
                                agent.Warp(warpHit.position);
                            }
                            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.SetDestination(targetPos);
                            
                            stuckTimer = 0f;
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

            // Exécution réelle de l'action tactique après arrivée au checkpoint
            if (!isTank && nodeAction == TacticalPathManager.NodeAction.GuetterPorte)
            {
                yield return StartCoroutine(ExecuteOverwatchDoor(targetPos));
                continue;
            }

            if (!isTank && nodeAction == TacticalPathManager.NodeAction.GarnisonFenetre)
            {
                yield return StartCoroutine(ExecuteEnterGarrison(targetPos));
                continue;
            }

            if (nodeAction == TacticalPathManager.NodeAction.Attendre30s)
            {
                isPerformingCheckpointAction = true;
                const float waitDuration = 30.0f;
                Debug.Log($"<color=cyan>[{gameObject.name}] ⏳ Halte tactique au checkpoint : pause de {waitDuration}s.</color>");
                if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.isStopped = true;
                if (animator != null && !isTank) animator.SetFloat("Speed", 0f);

                float waitTimer = 0f;
                while (waitTimer < waitDuration && !isDead)
                {
                    waitTimer += Time.deltaTime;
                    yield return null;
                }
                isPerformingCheckpointAction = false;
            }
            else if (nodeAction == TacticalPathManager.NodeAction.Guetter)
            {
                isPerformingCheckpointAction = true;
                isGuarding = true;
                Debug.Log($"<color=cyan>[{gameObject.name}] 🛡️ Posture de Guet / Overwatch active (+50% défense).</color>");
                if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.isStopped = true;
                if (animator != null && !isTank) animator.SetFloat("Speed", 0f);

                float lookTimer = 0f;
                while (lookTimer < 3.0f && !isDead)
                {
                    lookTimer += Time.deltaTime;
                    yield return null;
                }
                isPerformingCheckpointAction = false;
            }
            else if (nodeAction == TacticalPathManager.NodeAction.SeCacher)
            {
                isPerformingCheckpointAction = true;
                isCamouflaged = true;
                Debug.Log($"<color=green><b>[{gameObject.name}] 🥷 Furtivité activée : Plaquage contre le mur (Invisible pour l'ennemi) !</b></color>");
                if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.isStopped = true;
                if (animator != null && !isTank) animator.SetFloat("Speed", 0f);

                // S'orienter face à la rue le long du mur
                RaycastHit wallHit;
                if (Physics.Raycast(transform.position + Vector3.up * 1f, transform.forward, out wallHit, 2.5f))
                {
                    transform.rotation = Quaternion.LookRotation(-wallHit.normal);
                }

                float hideTimer = 0f;
                while (hideTimer < 2.0f && !isDead)
                {
                    hideTimer += Time.deltaTime;
                    yield return null;
                }
                isPerformingCheckpointAction = false;
            }
            else if (nodeAction == TacticalPathManager.NodeAction.Embuscade)
            {
                isPerformingCheckpointAction = true;
                isGuarding = true;
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
            else if (nodeAction == TacticalPathManager.NodeAction.TirMortier)
            {
                yield return StartCoroutine(ExecuteMortarStrike(targetPos));
                continue;
            }
        }

        TerminerOrdres();
    }

    /// <summary>
    /// <summary>
    /// Coroutine gérant la séquence d'artillerie lourde (visée, rotation et tirs en cloche multiples de 3 obus).
    /// </summary>
    private IEnumerator ExecuteMortarStrike(Vector3 targetPos)
    {
        isPerformingCheckpointAction = true;
        isMortarFiringMode = true;
        mortarTargetLock = targetPos;
        mortarSalvoTimer = 0f;

        if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.isStopped = true;

        // Rotation fluide vers la cible de bombardement
        Vector3 dir = (targetPos - transform.position);
        dir.y = 0;
        if (dir != Vector3.zero)
        {
            Quaternion targetRot = Quaternion.LookRotation(dir);
            float rotElapsed = 0f;
            while (rotElapsed < 0.6f)
            {
                rotElapsed += Time.deltaTime;
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotElapsed / 0.6f);
                yield return null;
            }
        }

        Debug.Log($"<color=orange><b>[{gameObject.name}] 🎯 BOMBARDEMENT D'ARTILLERIE STRICT : Salve de 3 obus sur ({targetPos.x:F1}, {targetPos.z:F1}) !</b></color>");

        // Salve de 3 obus frappant STRICTEMENT la cible désignée par le joueur
        for (int i = 0; i < 3; i++)
        {
            FireMortarShell(targetPos);
            if (i < 2) yield return new WaitForSeconds(0.9f);
        }

        yield return new WaitForSeconds(1.0f);
        isPerformingCheckpointAction = false;
        isMortarFiringMode = false;
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

        // 1. Détection précise de la façade extérieure
        Vector3 wallHitPoint = startPos;
        Vector3 wallNormal = -forwardNorm;
        bool foundWall = false;

        RaycastHit wallHit;
        if (Physics.Raycast(startPos + Vector3.up * 1.0f, forwardNorm, out wallHit, horizontalDist + 5f))
        {
            if (wallHit.collider.name.Contains("Batiment") || wallHit.collider.name.Contains("Building") || wallHit.collider.name.Contains("Wall") || wallHit.point.y > 0.5f)
            {
                wallHitPoint = wallHit.point;
                wallNormal = wallHit.normal;
                foundWall = true;
            }
        }

        if (!foundWall)
        {
            wallHitPoint = startPos + forwardNorm * Mathf.Max(0.5f, horizontalDist - 1.5f);
            wallNormal = -forwardNorm;
        }

        float roofHeight = destinationRoof.y;
        Vector3 climbBasePos = new Vector3(wallHitPoint.x, startPos.y, wallHitPoint.z) + wallNormal * 0.35f;
        Vector3 climbTopPos = new Vector3(climbBasePos.x, roofHeight, climbBasePos.z);

        // --- PHASE 1 : MARCHE AU SOL JUSQU'AU PIED DU MUR ---
        if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
        {
            agent.isStopped = false;
            if (NavMesh.SamplePosition(climbBasePos, out NavMeshHit baseNavHit, 3.0f, NavMesh.AllAreas))
            {
                agent.SetDestination(baseNavHit.position);
            }
            else
            {
                agent.SetDestination(climbBasePos);
            }
            
            float approachTimer = 0f;
            while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath && agent.remainingDistance > 0.8f && approachTimer < 8.0f)
            {
                if (isDead) yield break;
                approachTimer += Time.deltaTime;
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
            animator.transform.localPosition = Vector3.zero;
        }

        // --- PHASE 2 : ASCENSION STRICTEMENT VERTICALE SUR LA FAÇADE ---
        float heightDiff = Mathf.Abs(climbTopPos.y - climbBasePos.y);
        float climbDuration = Mathf.Max(1.6f, heightDiff * 0.28f);
        float elapsed = 0f;

        Debug.Log($"<color=green>[{gameObject.name}] 🧗 Escalade sur la façade (Hauteur: {heightDiff:F1}m)...</color>");

        while (elapsed < climbDuration && !isDead)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / climbDuration);
            transform.position = Vector3.Lerp(climbBasePos, climbTopPos, t);
            if (animator != null) animator.transform.localPosition = Vector3.zero;
            yield return null;
        }

        // --- PHASE 3 : ENJAMBEMENT ET ÉTABLISSEMENT NET SUR LE TOIT ---
        float stepElapsed = 0f;
        Vector3 stepStart = climbTopPos;
        Vector3 stepTarget = climbTopPos - wallNormal * 1.5f;

        // Détection précise de la surface supérieure du toit pour un appui au sol parfait
        if (Physics.Raycast(new Vector3(stepTarget.x, roofHeight + 3.0f, stepTarget.z), Vector3.down, out RaycastHit roofSurfaceHit, 8.0f))
        {
            stepTarget.y = roofSurfaceHit.point.y + 0.05f;
        }
        else
        {
            stepTarget.y = roofHeight + 0.05f;
        }

        while (stepElapsed < 0.5f && !isDead)
        {
            stepElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(stepElapsed / 0.5f);
            transform.position = Vector3.Lerp(stepStart, stepTarget, t);
            if (animator != null) animator.transform.localPosition = Vector3.zero;
            yield return null;
        }

        if (animator != null)
        {
            animator.SetBool("IsClimbing", false);
            animator.SetFloat("Speed", 0f);
            animator.transform.localPosition = Vector3.zero;
        }

        transform.position = stepTarget;
        isClimbing = false;
        isRooftopSniper = true;
        isPerformingCheckpointAction = false;
        if (agent != null) agent.enabled = false; // Reste en mode toiture directe sans interférence NavMesh sol

        Debug.Log($"<color=green><b>[{gameObject.name}] 🎖️ Établi fermement au-dessus du toit (y={stepTarget.y:F2}m) ! Prêt pour la suite du trajet.</b></color>");

        Debug.Log($"<color=green><b>[{gameObject.name}] 🎖️ Position sur le toit sécurisée (Poste Haut - AK-47) !</b></color>");
    }

    /// <summary>
    /// Coroutine gérant la descente en rappel / escalade inverse depuis le toit vers la rue.
    /// </summary>
    private IEnumerator ExecuteClimbDown(Vector3 destinationGround)
    {
        isClimbing = true;
        isPerformingCheckpointAction = true;
        isRooftopSniper = false;

        Vector3 startRoofPos = transform.position;
        Vector3 horizontalDir = (destinationGround - startRoofPos);
        horizontalDir.y = 0;
        Vector3 forwardNorm = (horizontalDir.sqrMagnitude > 0.01f) ? horizontalDir.normalized : transform.forward;

        Vector3 edgePos = startRoofPos + forwardNorm * 1.2f;
        Vector3 groundLandPos = new Vector3(edgePos.x, destinationGround.y, edgePos.z);

        if (agent != null) agent.enabled = false;

        transform.rotation = Quaternion.LookRotation(forwardNorm);

        if (animator != null)
        {
            animator.SetBool("IsClimbing", true);
            animator.SetFloat("Speed", 0f);
            animator.transform.localPosition = Vector3.zero;
        }

        float heightDiff = Mathf.Abs(startRoofPos.y - groundLandPos.y);
        float descendDuration = Mathf.Max(1.4f, heightDiff * 0.25f);
        float elapsed = 0f;

        Debug.Log($"<color=cyan>[{gameObject.name}] 🧗 Descente en rappel du toit vers la rue ({heightDiff:F1}m)...</color>");

        while (elapsed < descendDuration && !isDead)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / descendDuration);
            transform.position = Vector3.Lerp(startRoofPos, groundLandPos, t);
            if (animator != null) animator.transform.localPosition = Vector3.zero;
            yield return null;
        }

        if (animator != null)
        {
            animator.SetBool("IsClimbing", false);
            animator.transform.localPosition = Vector3.zero;
        }

        transform.position = groundLandPos;
        isClimbing = false;
        isPerformingCheckpointAction = false;

        if (agent != null)
        {
            agent.enabled = true;
            if (NavMesh.SamplePosition(groundLandPos, out NavMeshHit gHit, 5.0f, NavMesh.AllAreas))
            {
                agent.Warp(gHit.position);
            }
            else
            {
                agent.Warp(groundLandPos);
            }
            agent.isStopped = false;
        }

        Debug.Log($"<color=cyan>[{gameObject.name}] 🎖️ Au sol dans la rue !</color>");
    }

    /// <summary>
    /// Coroutine gérant le déplacement vers la porte du bâtiment et la prise de poste à la fenêtre (Garnison & Couverture).
    /// </summary>
    private IEnumerator ExecuteEnterGarrison(Vector3 windowTargetPos)
    {
        isPerformingCheckpointAction = true;

        BuildingStructure structure = null;
        BuildingStructure.BuildingWindow targetWindow = null;

        foreach (var b in BuildingStructure.AllBuildings)
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
    /// Coroutine gérant le déplacement vers la porte, l'infiltration à l'intérieur du polygone
    /// et l'enregistrement de l'unité auprès de TacticalVisibility pour masquer/rendre transparent le toit.
    /// </summary>
    private IEnumerator ExecuteEnterBuilding(Vector3 doorTargetPos)
    {
        isPerformingCheckpointAction = true;

        BuildingStructure structure = null;
        BuildingStructure.BuildingDoor targetDoor = null;

        foreach (var b in BuildingStructure.AllBuildings)
        {
            BuildingStructure.BuildingDoor d = b.GetClosestDoor(doorTargetPos);
            if (d != null && Vector3.Distance(d.position, doorTargetPos) < 4.0f)
            {
                structure = b;
                targetDoor = d;
                break;
            }
        }

        if (structure != null && targetDoor != null)
        {
            Vector3 outsidePos = targetDoor.position + targetDoor.entryDirection * 1.2f;
            Vector3 insidePos = targetDoor.position - targetDoor.entryDirection * 1.8f;
            insidePos.y = 0.05f; // Sol intérieur

            // 1. Déplacement sur le NavMesh jusqu'au pas de la porte
            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
            {
                agent.isStopped = false;
                agent.SetDestination(outsidePos);
                while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath && agent.remainingDistance > 0.8f)
                {
                    if (isDead) yield break;
                    yield return null;
                }
            }

            // 2. Franchissement de la porte
            if (agent != null) agent.enabled = false;
            if (animator != null && !isTank) animator.SetFloat("Speed", 2f);

            Vector3 enterStart = transform.position;
            float transitTime = 0.9f;
            float elapsed = 0f;
            while (elapsed < transitTime && !isDead)
            {
                elapsed += Time.deltaTime;
                transform.position = Vector3.Lerp(enterStart, insidePos, elapsed / transitTime);
                if (targetDoor.entryDirection != Vector3.zero)
                {
                    transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(-targetDoor.entryDirection), elapsed / transitTime);
                }
                yield return null;
            }

            if (animator != null && !isTank) animator.SetFloat("Speed", 0f);

            // 3. Quitter l'ancien bâtiment si nécessaire et s'enregistrer dans le nouveau
            if (currentBuilding != null && currentBuilding != structure)
            {
                currentBuilding.UnregisterUnitInside(this);
            }
            currentBuilding = structure;
            structure.RegisterUnitInside(this);

            // 4. Reconnexion propre au NavMesh intérieur
            if (agent != null)
            {
                agent.enabled = true;
                if (NavMesh.SamplePosition(insidePos, out NavMeshHit insideHit, 3.0f, NavMesh.AllAreas))
                {
                    agent.Warp(insideHit.position);
                }
                else
                {
                    agent.Warp(insidePos);
                }
            }

            // Son de clic / confirmation
            AudioClip clickClip = ProceduralAudioBuilder.CreateClickSound();
            if (clickClip != null) AudioSource.PlayClipAtPoint(clickClip, transform.position);

            Debug.Log($"<color=green>[{gameObject.name}] 🚪 Infiltration réussie à l'intérieur de {structure.gameObject.name} ! Toit masqué pour la vue tactique.</color>");
        }

        isPerformingCheckpointAction = false;
    }

    /// <summary>
    /// Coroutine gérant le déplacement de l'intérieur du polygone vers la porte, le franchissement vers la rue
    /// et le désenregistrement auprès de BuildingStructure / TacticalVisibility.
    /// </summary>
    private IEnumerator ExecuteExitBuilding(Vector3 outsideTargetPos)
    {
        isPerformingCheckpointAction = true;

        BuildingStructure structure = currentBuilding;
        BuildingStructure.BuildingDoor exitDoor = null;

        if (structure != null)
        {
            exitDoor = structure.GetClosestDoor(outsideTargetPos);
        }
        else
        {
            foreach (var b in BuildingStructure.AllBuildings)
            {
                BuildingStructure.BuildingDoor d = b.GetClosestDoor(outsideTargetPos);
                if (d != null && Vector3.Distance(d.position, outsideTargetPos) < 4.0f)
                {
                    structure = b;
                    exitDoor = d;
                    break;
                }
            }
        }

        if (structure != null && exitDoor != null)
        {
            Vector3 insidePos = exitDoor.position - exitDoor.entryDirection * 1.5f;
            Vector3 outsidePos = exitDoor.position + exitDoor.entryDirection * 1.5f;

            // 1. Déplacement sur le NavMesh intérieur jusqu'au seuil intérieur de la porte
            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
            {
                agent.isStopped = false;
                agent.SetDestination(insidePos);
                while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath && agent.remainingDistance > 0.8f)
                {
                    if (isDead) yield break;
                    yield return null;
                }
            }

            // 2. Franchissement de la porte vers la rue
            if (agent != null) agent.enabled = false;
            if (animator != null && !isTank) animator.SetFloat("Speed", 2f);

            Vector3 exitStart = transform.position;
            float transitTime = 0.9f;
            float elapsed = 0f;
            while (elapsed < transitTime && !isDead)
            {
                elapsed += Time.deltaTime;
                transform.position = Vector3.Lerp(exitStart, outsidePos, elapsed / transitTime);
                if (exitDoor.entryDirection != Vector3.zero)
                {
                    transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(exitDoor.entryDirection), elapsed / transitTime);
                }
                yield return null;
            }

            if (animator != null && !isTank) animator.SetFloat("Speed", 0f);

            // 3. Quitter officiellement le bâtiment
            structure.UnregisterUnitInside(this);
            currentBuilding = null;
            LeaveGarrison();

            // 4. Reconnexion propre au NavMesh de la rue
            if (agent != null)
            {
                agent.enabled = true;
                if (NavMesh.SamplePosition(outsidePos, out NavMeshHit streetHit, 3.0f, NavMesh.AllAreas))
                {
                    agent.Warp(streetHit.position);
                }
                else
                {
                    agent.Warp(outsidePos);
                }
            }

            AudioClip clickClip = ProceduralAudioBuilder.CreateClickSound();
            if (clickClip != null) AudioSource.PlayClipAtPoint(clickClip, transform.position);

            Debug.Log($"<color=green>[{gameObject.name}] 🚪 Sortie réussie de {structure.gameObject.name} vers la rue !</color>");
        }

        isPerformingCheckpointAction = false;
    }

    /// <summary>
    /// Coroutine gérant la prise de poste à l'encadrement intérieur d'une porte pour surveiller et tirer dans la rue.
    /// </summary>
    private IEnumerator ExecuteOverwatchDoor(Vector3 doorTargetPos)
    {
        isPerformingCheckpointAction = true;

        BuildingStructure structure = currentBuilding;
        BuildingStructure.BuildingDoor door = null;

        if (structure != null)
        {
            door = structure.GetClosestDoor(doorTargetPos);
        }
        else
        {
            foreach (var b in BuildingStructure.AllBuildings)
            {
                var d = b.GetClosestDoor(doorTargetPos);
                if (d != null && Vector3.Distance(d.position, doorTargetPos) < 4.0f)
                {
                    structure = b;
                    door = d;
                    break;
                }
            }
        }

        if (door != null)
        {
            Vector3 stancePos = door.position - door.entryDirection * 0.8f;
            stancePos.y = 0.05f;

            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
            {
                agent.isStopped = false;
                agent.SetDestination(stancePos);
                while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath && agent.remainingDistance > 0.4f)
                {
                    if (isDead) yield break;
                    yield return null;
                }
                agent.isStopped = true;
            }

            // Orientation face à la rue
            if (door.entryDirection != Vector3.zero)
            {
                transform.rotation = Quaternion.LookRotation(door.entryDirection);
            }

            isGarrisoned = true;
            Debug.Log($"<color=cyan>[{gameObject.name}] 👁️ En joue à l'encadrement de porte ! Surveillance active de la rue avec couverture lourde.</color>");
        }

        isPerformingCheckpointAction = false;
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
