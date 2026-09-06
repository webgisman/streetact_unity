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
            //
            // La DESCENTE est évaluée AVANT la montée, et une montée exige désormais une vraie
            // différence de hauteur en plus de l'action : "DESCENDRE DU TOIT" émettait Escalade avec
            // une position de rue, donc l'ancien test (action == Escalade) déclenchait ExecuteClimb
            // "à l'envers" et ExecuteClimbDown n'était jamais atteint — l'unité restait perchée avec
            // son NavMeshAgent éteint pour le reste de la partie (voir NodeAction.Descendre).
            float heightDelta = targetPos.y - transform.position.y;
            bool isClimbDown = !isTank && (nodeAction == TacticalPathManager.NodeAction.Descendre || heightDelta < -1.8f);
            // GarnisonFenetre exclu explicitement (correctif 2026-09-05), symétriquement à Descendre :
            // la position d'un ordre "Garnison Fenêtre" est la hauteur RÉELLE de la fenêtre visée
            // (WindowInteraction.GetInteriorStancePosition -> windowData.position), qui vaut
            // 1.4 + étage*3.0m (CityGenerator) — au-delà de 1.8m dès le 1er étage. Sans cette
            // exclusion, un ordre de garnison à l'étage déclenchait ExecuteClimb au lieu
            // d'ExecuteEnterGarrison (branche bien plus bas, jamais atteinte à cause du `continue`
            // ci-dessous) : le soldat escaladait la façade jusqu'à la hauteur de la fenêtre, puis sa
            // phase finale de sondage vers le haut trouvait le toit et l'y installait — un ordre
            // "Guetter (Couvert -75%)" faisait donc grimper le tireur en sniper exposé sur le toit,
            // sans jamais entrer dans le bâtiment ni obtenir la couverture, sans aucun message.
            bool isClimbUp = !isTank && !isClimbDown
                             && heightDelta > 1.8f
                             && nodeAction != TacticalPathManager.NodeAction.Descendre
                             && nodeAction != TacticalPathManager.NodeAction.GarnisonFenetre;

            if (isClimbDown)
            {
                // Cible ramenée au sol : un ordre de descente désigne un point de rue, dont le Y
                // capté au clic peut être resté à la hauteur du toit selon le collider touché.
                Vector3 groundTarget = new Vector3(targetPos.x, Mathf.Min(targetPos.y, 0.05f), targetPos.z);
                yield return StartCoroutine(ExecuteClimbDown(groundTarget));
                continue;
            }
            else if (isClimbUp)
            {
                yield return StartCoroutine(ExecuteClimb(targetPos));
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
            //
            // `nodeAction != GarnisonFenetre` exclu explicitement (correctif 2026-09-05), même raison
            // que pour isClimbUp ci-dessus : la position cible d'une garnison de fenêtre à l'étage a
            // un Y au-delà du seuil de toit (hauteur RÉELLE de la fenêtre, pas une hauteur de toit),
            // ce qui faisait déclencher CETTE branche (WalkAcrossRooftop) même après avoir exclu
            // GarnisonFenetre de isClimbUp — la même unité aurait fini sur le toit par un second
            // chemin si celui-ci n'était pas fermé aussi.
            if (!isTank && nodeAction != TacticalPathManager.NodeAction.GarnisonFenetre
                && (isRooftopSniper || transform.position.y > RoofStrataThresholdY || targetPos.y > RoofStrataThresholdY))
            {
                if (agent != null && agent.enabled)
                {
                    agent.isStopped = true;
                    agent.ResetPath();
                    agent.enabled = false; // Évite que le NavMesh au sol n'aspire le soldat vers le bas
                }

                yield return StartCoroutine(WalkAcrossRooftop(targetPos));
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

                    // Seuil de patience PROPRE À CETTE UNITÉ pour les blindés (stuckThreshold est
                    // tiré au hasard entre 1.0 et 2.5s à l'initialisation, justement pour désynchroniser
                    // les face-à-face — mais il n'était jamais relu, tous les chars utilisaient 4.5f).
                    // Deux chars nez à nez déclenchaient donc leur esquive à la même frame, se
                    // transformaient en obstacle en même temps, repartaient en même temps et se
                    // rebloquaient aussitôt : ils vibraient sur place pendant tout le tour et
                    // retenaient l'escouade entière jusqu'au plafond de sécurité.
                    float patience = isTank ? (stuckThreshold + 1.5f) : 4.5f;
                    if (stuckTimer > patience)
                    {
                        if (!isTank)
                        {
                            // Réinitialiser la destination plutôt que d'annuler le mouvement
                            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                            {
                                agent.SetDestination(targetPos);
                                yield return null;
                                while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.pathPending && !isDead) yield return null;
                            }
                            stuckTimer = 0f;
                            lastPos = transform.position;
                        }
                        else
                        {
                            float yieldDuration = UnityEngine.Random.Range(1.2f, 2.8f);
                            Debug.Log($"<color=yellow>[{gameObject.name}] Face-à-face ! Je cède le passage pendant {yieldDuration:F1}s.</color>");

                            // Esquive intelligente : Le char se transforme en mur pour obliger l'autre à le contourner
                            SetObstacleMode(true);
                            yield return new WaitForSeconds(yieldDuration);

                            // Je reprends la route
                            SetObstacleMode(false);
                            yield return null;
                            if (agent != null && !agent.isOnNavMesh && NavMesh.SamplePosition(transform.position, out NavMeshHit warpHit, 5.0f, NavMesh.AllAreas))
                            {
                                agent.Warp(warpHit.position);
                            }
                            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
                            {
                                agent.SetDestination(targetPos);
                                // Attendre le calcul du chemin, comme à l'envoi initial : sans ça la
                                // condition de la boucle d'arrivée testait agent.hasPath encore à false
                                // et le nœud était abandonné en silence.
                                yield return null;
                                while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.pathPending && !isDead) yield return null;
                            }

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
    /// <summary>
    /// Marche jusqu'à <paramref name="destination"/> via le NavMesh, en ATTENDANT d'abord le calcul
    /// du chemin et avec un plafond de durée.
    ///
    /// Les approches de porte, d'entrée, de sortie et de fenêtre partageaient deux défauts :
    ///   - elles testaient agent.hasPath dès la frame du SetDestination, où il est encore false
    ///     (pathPending) : la boucle pouvait donc se terminer immédiatement et l'unité était ensuite
    ///     repositionnée d'un bloc, sans avoir marché ;
    ///   - elles n'avaient aucun plafond : un seuil de porte inatteignable (bloqué par des décombres,
    ///     une barricade, un autre corps) gelait la phase d'exécution entière jusqu'au filet de
    ///     sécurité du tour, en immobilisant aussi toutes les autres unités.
    /// </summary>
    private IEnumerator WalkAgentTo(Vector3 destination, float arriveDistance)
    {
        if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh) yield break;

        agent.isStopped = false;
        if (NavMesh.SamplePosition(destination, out NavMeshHit navHit, 3.0f, NavMesh.AllAreas))
        {
            agent.SetDestination(navHit.position);
        }
        else
        {
            agent.SetDestination(destination);
        }

        yield return null;
        while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.pathPending)
        {
            if (isDead) yield break;
            yield return null;
        }

        float timeout = Mathf.Clamp(Vector3.Distance(transform.position, destination) / 2.5f + 3f, 4f, 20f);
        float timer = 0f;
        while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath
               && agent.remainingDistance > arriveDistance && timer < timeout)
        {
            if (isDead) yield break;
            timer += Time.deltaTime;
            yield return null;
        }
    }

    /// <summary>
    /// Marche sur la SURFACE d'un toit en la suivant réellement, sans jamais quitter l'empreinte du
    /// bâtiment sur lequel l'unité se tient.
    ///
    /// Remplace un simple Vector3.MoveTowards en ligne droite qui cumulait quatre défauts, tous
    /// visibles en jeu :
    ///   (a) la hauteur n'était sondée QU'UNE fois, à la destination, sur 8m — si la sonde ne
    ///       touchait rien (cible hors du toit, ou immeuble plus haut que la fenêtre de sonde),
    ///       l'unité gardait son Y et s'éloignait EN L'AIR au-dessus de la rue, où elle restait ;
    ///   (b) la hauteur était interpolée linéairement du départ à l'arrivée, donc l'unité traversait
    ///       le faîtage des toits à pans (CityGenerator.CreateHipRoofMesh : coursive plate de 2.5m
    ///       puis pente jusqu'à +1m) au lieu de marcher dessus ;
    ///   (c) le bord du toit n'était jamais testé, d'où la traversée du vide d'un immeuble à l'autre ;
    ///   (d) aucun garde-fou de durée.
    /// </summary>
    private IEnumerator WalkAcrossRooftop(Vector3 requestedTarget)
    {
        BuildingStructure roofBuilding = BuildingStructure.FindBuildingAt(transform.position);

        // Destination ramenée SUR le toit : un toit n'a pas de passerelle vers le suivant. Si le
        // joueur a désigné un point hors de cette empreinte, l'unité va aussi loin qu'elle peut sur
        // son propre toit — c'est un ordre de descente ("DESCENDRE DU TOIT") qui la fait redescendre,
        // jamais un pas dans le vide.
        Vector3 destination = requestedTarget;
        if (roofBuilding != null && !roofBuilding.ContainsPoint2D(requestedTarget))
        {
            destination = ClampToRooftopEdge(roofBuilding, transform.position, requestedTarget);
        }

        Vector3 flatDelta = new Vector3(destination.x - transform.position.x, 0f, destination.z - transform.position.z);
        if (flatDelta.sqrMagnitude <= 0.04f)
        {
            SettleOnRoofSurface(roofBuilding);
            yield break;
        }

        transform.rotation = Quaternion.LookRotation(flatDelta.normalized);
        if (animator != null) animator.SetFloat("Speed", 2.5f);

        const float walkSpeed = 3.5f;
        // Garde-fou : distance à parcourir au rythme prévu, plus une marge. Sans lui, un toit dont la
        // sonde échoue partout bloquerait la phase d'exécution entière.
        float timeout = (flatDelta.magnitude / walkSpeed) + 4f;
        float elapsed = 0f;

        while (elapsed < timeout && !isDead)
        {
            elapsed += Time.deltaTime;

            Vector3 flatToTarget = new Vector3(destination.x - transform.position.x, 0f, destination.z - transform.position.z);
            if (flatToTarget.sqrMagnitude <= 0.16f) break; // 0.4m

            if (flatToTarget.sqrMagnitude > 0.01f)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(flatToTarget.normalized), 360f * Time.deltaTime);
            }

            Vector3 step = flatToTarget.normalized * Mathf.Min(walkSpeed * Time.deltaTime, flatToTarget.magnitude);
            Vector3 candidate = transform.position + step;

            // Sonde de surface à CHAQUE pas : c'est ce qui fait suivre la pente du toit, et ce qui
            // détecte le vide. La fenêtre est large (3m au-dessus, 6m en-dessous) pour absorber une
            // coursive, une pente et un léger décrochement, sans jamais atteindre la rue depuis un
            // toit — un immeuble fait au minimum ~4.5m.
            if (Physics.Raycast(candidate + Vector3.up * 3f, Vector3.down, out RaycastHit surface, 9f, WorldGeometryMask, QueryTriggerInteraction.Ignore)
                && surface.point.y > RoofStrataThresholdY)
            {
                candidate.y = surface.point.y + 0.05f;
                transform.position = candidate;
            }
            else
            {
                // Plus rien sous les pieds : on s'arrête net au dernier appui valide plutôt que de
                // continuer dans le vide.
                break;
            }

            yield return null;
        }

        if (animator != null) animator.SetFloat("Speed", 0f);
        SettleOnRoofSurface(roofBuilding);
    }

    /// <summary>Point le plus avancé VERS la cible qui reste sur l'empreinte du toit — recherche
    /// dichotomique le long du segment, donc indépendante de la forme du polygone (les empreintes
    /// OSM sont souvent concaves).</summary>
    private Vector3 ClampToRooftopEdge(BuildingStructure roofBuilding, Vector3 from, Vector3 to)
    {
        Vector3 inside = from;
        Vector3 outside = to;
        for (int i = 0; i < 12; i++) // 12 bissections : précision ~ distance/4096, largement suffisant
        {
            Vector3 mid = (inside + outside) * 0.5f;
            if (roofBuilding.ContainsPoint2D(mid)) inside = mid;
            else outside = mid;
        }

        // Reculer un peu du bord pour que le soldat ne soit pas à moitié dans le vide.
        Vector3 inward = (inside - outside);
        inward.y = 0f;
        if (inward.sqrMagnitude > 0.0001f) inside += inward.normalized * 0.6f;
        return inside;
    }

    /// <summary>Recale l'unité sur la surface réellement sous ses pieds et remet l'état "perché" en
    /// accord avec sa hauteur RÉELLE — jamais un isRooftopSniper affirmé d'office comme avant, qui
    /// laissait des unités revenues au sol avec les bonus et les contraintes d'un poste haut.</summary>
    private void SettleOnRoofSurface(BuildingStructure roofBuilding)
    {
        if (Physics.Raycast(transform.position + Vector3.up * 3f, Vector3.down, out RaycastHit surface, 9f, WorldGeometryMask, QueryTriggerInteraction.Ignore))
        {
            transform.position = new Vector3(transform.position.x, surface.point.y + 0.05f, transform.position.z);
        }

        bool actuallyOnRoof = transform.position.y > RoofStrataThresholdY;
        isRooftopSniper = actuallyOnRoof;

        if (!actuallyOnRoof && agent != null)
        {
            // Redescendue par la géométrie : rendre l'unité au NavMesh, sinon elle glisserait en ligne
            // droite à travers le décor pour le reste de la partie.
            if (NavMesh.SamplePosition(transform.position, out NavMeshHit groundHit, 5f, NavMesh.AllAreas))
            {
                agent.enabled = true;
                agent.Warp(groundHit.position);
                agent.isStopped = false;
            }
        }

        if (roofBuilding != null && actuallyOnRoof && !roofBuilding.unitsOnRoof.Contains(this))
        {
            roofBuilding.unitsOnRoof.Add(this);
        }
    }

    private IEnumerator ExecuteClimb(Vector3 destinationRoof)
    {
        isClimbing = true;
        isPerformingCheckpointAction = true;

        Vector3 startPos = transform.position;
        Vector3 horizontalDir = (destinationRoof - startPos);
        horizontalDir.y = 0;
        float horizontalDist = horizontalDir.magnitude;
        Vector3 forwardNorm = (horizontalDist > 0.01f) ? horizontalDir.normalized : transform.forward;

        // 1. Détection précise de la façade extérieure.
        //    Le bâtiment visé est celui qui contient le point demandé : c'est LUI qu'on doit gravir,
        //    pas le premier collider rencontré. L'ancien test acceptait n'importe quel impact dont le
        //    point était au-dessus de 0.5m — donc un lampadaire, un arbre, un véhicule ou un autre
        //    soldat — et l'unité escaladait alors le vide à côté du bâtiment. Le masque de décor
        //    (WorldGeometryMask) écarte en plus les unités et les marqueurs d'interface.
        BuildingStructure targetBuilding = BuildingStructure.FindBuildingAt(destinationRoof);

        Vector3 wallHitPoint = startPos;
        Vector3 wallNormal = -forwardNorm;
        bool foundWall = false;

        RaycastHit wallHit;
        if (Physics.Raycast(startPos + Vector3.up * 1.0f, forwardNorm, out wallHit, horizontalDist + 5f, WorldGeometryMask, QueryTriggerInteraction.Ignore))
        {
            BuildingStructure hitBuilding = wallHit.collider.GetComponentInParent<BuildingStructure>();
            bool isTargetFacade = (targetBuilding != null)
                ? (hitBuilding == targetBuilding)
                : (hitBuilding != null || wallHit.collider.name.Contains("Batiment") || wallHit.collider.name.Contains("Building") || wallHit.collider.name.Contains("Wall"));

            if (isTargetFacade)
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
            
            // Laisser le NavMesh calculer son chemin AVANT de surveiller l'arrivée : la boucle
            // testait agent.hasPath dès la frame du SetDestination, où il est encore false
            // (pathPending). Elle se terminait donc immédiatement et le soldat était téléporté d'un
            // bloc jusqu'au pied du mur, quelle que soit la distance.
            yield return null;
            while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.pathPending)
            {
                if (isDead) yield break;
                yield return null;
            }

            float approachTimer = 0f;
            float approachTimeout = Mathf.Clamp(Vector3.Distance(transform.position, climbBasePos) / 2.5f + 3f, 4f, 20f);
            while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath
                   && agent.remainingDistance > 0.8f && approachTimer < approachTimeout)
            {
                if (isDead) yield break;
                approachTimer += Time.deltaTime;
                yield return null;
            }
        }

        if (agent != null) agent.enabled = false;

        // Tourner le soldat face à la façade
        transform.rotation = Quaternion.LookRotation(-wallNormal);
        // Recaler au pied du mur SANS saut visible : si l'approche a échoué (pas de NavMesh, timeout),
        // l'unité pourrait être encore loin — on la fait alors marcher les derniers mètres au lieu de
        // la faire disparaître d'un point à l'autre.
        Vector3 toBase = climbBasePos - transform.position;
        toBase.y = 0f;
        if (toBase.magnitude > 1.0f)
        {
            float glideElapsed = 0f;
            float glideDuration = Mathf.Min(2.5f, toBase.magnitude / 4f);
            Vector3 glideStart = transform.position;
            if (animator != null) animator.SetFloat("Speed", 2.5f);
            while (glideElapsed < glideDuration && !isDead)
            {
                glideElapsed += Time.deltaTime;
                transform.position = Vector3.Lerp(glideStart, climbBasePos, Mathf.Clamp01(glideElapsed / glideDuration));
                yield return null;
            }
            if (animator != null) animator.SetFloat("Speed", 0f);
        }
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

        // Détection précise de la surface supérieure du toit pour un appui au sol parfait. Masque de
        // décor (sinon un autre soldat déjà posté sur ce toit sert de "sol") et fenêtre élargie : la
        // hauteur demandée vient de BuildingStructure.height, qui n'est pas exactement la surface
        // rendue (CityGenerator ajoute une coursive plate puis une pente jusqu'à +1m).
        if (Physics.Raycast(new Vector3(stepTarget.x, roofHeight + 4.0f, stepTarget.z), Vector3.down, out RaycastHit roofSurfaceHit, 10.0f, WorldGeometryMask, QueryTriggerInteraction.Ignore))
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
        if (agent != null) agent.enabled = false; // Reste en mode toiture directe sans interférence NavMesh sol

        Debug.Log($"<color=green><b>[{gameObject.name}] 🎖️ Parapet franchi (y={stepTarget.y:F2}m) — rejoint maintenant le poste demandé.</b></color>");

        // Rejoindre RÉELLEMENT le point désigné par le joueur. L'escalade s'arrêtait jusqu'ici à
        // 1.5m derrière le parapet, quel que soit l'endroit du toit effectivement tapé : le
        // marqueur de waypoint et la ligne d'aperçu montraient donc une position que l'unité
        // n'atteignait jamais, et un poste choisi pour son angle de vue ne servait à rien.
        yield return StartCoroutine(WalkAcrossRooftop(destinationRoof));

        isPerformingCheckpointAction = false;

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

        if (agent != null) agent.enabled = false;

        // Rejoindre d'abord le VRAI bord du toit du côté de la cible, puis se laisser descendre juste
        // au-delà. L'ancien code descendait à 1.2m devant lui sans se demander où était le bord : sur
        // un immeuble un peu large, ce point est encore au-dessus du toit, et le soldat descendait
        // donc À TRAVERS le bâtiment jusqu'à la rue.
        BuildingStructure roofBuilding = BuildingStructure.FindBuildingAt(startRoofPos);
        if (roofBuilding != null)
        {
            Vector3 farOutside = startRoofPos + forwardNorm * 200f;
            Vector3 edgeOnRoof = ClampToRooftopEdge(roofBuilding, startRoofPos, farOutside);
            if ((new Vector2(edgeOnRoof.x - startRoofPos.x, edgeOnRoof.z - startRoofPos.z)).sqrMagnitude > 0.25f)
            {
                yield return StartCoroutine(WalkAcrossRooftop(edgeOnRoof));
                startRoofPos = transform.position;
                isRooftopSniper = false; // WalkAcrossRooftop le rétablit d'après la hauteur : on redescend juste après
            }
        }

        // Point d'atterrissage : au-delà du bord, hors de l'empreinte, sur le sol réel.
        Vector3 landingXZ = startRoofPos + forwardNorm * 1.8f;
        if (roofBuilding != null)
        {
            int guard = 0;
            while (roofBuilding.ContainsPoint2D(landingXZ) && guard++ < 20)
            {
                landingXZ += forwardNorm * 0.8f;
            }
        }

        float landingY = 0.05f;
        if (Physics.Raycast(new Vector3(landingXZ.x, startRoofPos.y + 1f, landingXZ.z), Vector3.down, out RaycastHit groundProbe, startRoofPos.y + 12f, WorldGeometryMask, QueryTriggerInteraction.Ignore))
        {
            landingY = groundProbe.point.y + 0.05f;
        }
        else if (NavMesh.SamplePosition(new Vector3(landingXZ.x, 0.05f, landingXZ.z), out NavMeshHit landNav, 6f, NavMesh.AllAreas))
        {
            landingY = landNav.position.y;
        }

        Vector3 groundLandPos = new Vector3(landingXZ.x, landingY, landingXZ.z);

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
        isRooftopSniper = false;

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

        // Puis rejoindre RÉELLEMENT le point ordonné : la descente laissait l'unité au pied de la
        // façade, alors que le joueur avait désigné un point de rue souvent bien plus loin — l'ordre
        // paraissait donc à moitié exécuté, sans aucun message.
        Vector3 remaining = destinationGround - transform.position;
        remaining.y = 0f;
        if (remaining.magnitude > 1.0f && agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && !isDead)
        {
            agent.stoppingDistance = 0.5f;
            agent.SetDestination(destinationGround);
            yield return null;
            while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.pathPending && !isDead) yield return null;

            float walkTimer = 0f;
            float walkTimeout = Mathf.Clamp(remaining.magnitude / 2.5f + 3f, 4f, 25f);
            if (animator != null) animator.SetFloat("Speed", 2.5f);
            while (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath
                   && agent.remainingDistance > agent.stoppingDistance && walkTimer < walkTimeout && !isDead)
            {
                walkTimer += Time.deltaTime;
                yield return null;
            }
            if (animator != null) animator.SetFloat("Speed", 0f);
        }

        isPerformingCheckpointAction = false;
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
            yield return StartCoroutine(WalkAgentTo(doorPos, 0.8f));

            // 3. Entrée dans le bâtiment
            if (agent != null) agent.enabled = false;
            if (animator != null) animator.SetFloat("Speed", 0f);

            Vector3 enterStart = transform.position;
            Vector3 windowStancePos = targetWindow.position - targetWindow.outwardNormal * 0.35f; // Positionné juste derrière la fenêtre à l'intérieur
            // Durée PROPORTIONNELLE à la distance réelle à parcourir (correctif 2026-09-05), jamais
            // un temps fixe : WalkAgentTo (ci-dessus) a un plafond borné mais aucun retour
            // succès/échec — si l'approche de la porte a expiré loin de sa cible (gravats, embouteillage
            // d'unités au même seuil), enterStart pouvait être bien plus loin de windowStancePos que
            // les ~3m du franchissement normal, et un temps fixe de 1.0s produisait alors un
            // "glissement téléportation" visible sur toute cette distance. Même idiome que le glide de
            // ExecuteClimb (voir plus haut) : borné des deux côtés pour rester une marche crédible.
            float transitTime = Mathf.Clamp(Vector3.Distance(enterStart, windowStancePos) / 3.0f, 0.5f, 4.0f);
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
            yield return StartCoroutine(WalkAgentTo(outsidePos, 0.8f));

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
            yield return StartCoroutine(WalkAgentTo(insidePos, 0.8f));

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

            yield return StartCoroutine(WalkAgentTo(stancePos, 0.4f));
            if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh) agent.isStopped = true;

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
