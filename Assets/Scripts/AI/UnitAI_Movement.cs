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

        // En ligne, le serveur seul fixe la position des unités (le client écrit transform.position à chaque
        // snapshot) : ne jamais réactiver l'agent ni recaler l'unité sur le NavMesh, sinon elle « glisse »
        // ou se téléporte loin de sa position réelle. Test sur une partie EN COURS (IsActive ou déploiement),
        // jamais sur IsFlowActive, vrai dans tous les menus en ligne.
        if (Novgov.Network.MultiplayerMatchController.IsActive
            || Novgov.Network.MultiplayerMatchController.IsDeploymentPhaseActive)
        {
            agent.enabled = false;
            return;
        }

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

            // 1. INFANTERIE EN HAUTEUR (toit, ou fenêtre d'étage) : rester sur les toits, passer sur
            // un toit voisin, ou redescendre dans la rue — priorité absolue avant tout chemin au sol.
            //
            // 2026-10-03, retour joueur : « sur le toit, je lui demande d'aller plus loin dans la rue,
            // il marche un peu sur le toit et s'arrête — il ne sait pas descendre et passer les
            // embûches ». Une cible hors du toit était jusqu'ici soit ramenée au bord du toit (et
            // l'unité s'y arrêtait), soit atteinte par une descente « droit devant » qui pouvait
            // atterrir sur le toit mitoyen. Désormais : même toit ou toits mitoyens -> marche sur
            // les toits ; autre immeuble séparé par une rue -> descente, traversée, escalade ; cible
            // au sol -> descente par le bord qui donne sur la rue la mieux placée (voir
            // ExecuteClimbDown), puis l'action du nœud (entrer, guetter, attendre...) au sol.
            bool isHigh = !isTank && (isRooftopSniper || transform.position.y > RoofStrataThresholdY);
            if (isHigh && nodeAction != TacticalPathManager.NodeAction.TirMortier)
            {
                // GarnisonFenetre : sa position est la hauteur RÉELLE de la fenêtre visée (1.4 +
                // étage*3m), pas un toit — on y va en redescendant puis en entrant par la porte.
                bool targetOnRoof = targetPos.y > RoofStrataThresholdY
                                    && nodeAction != TacticalPathManager.NodeAction.Descendre
                                    && nodeAction != TacticalPathManager.NodeAction.GarnisonFenetre;
                if (targetOnRoof)
                {
                    if (agent != null && agent.enabled)
                    {
                        agent.isStopped = true;
                        agent.ResetPath();
                        agent.enabled = false; // Évite que le NavMesh au sol n'aspire le soldat vers le bas
                    }

                    BuildingStructure standingRoof = BuildingStructure.FindBuildingAt(transform.position);
                    BuildingStructure targetRoof = BuildingStructure.FindBuildingAt(targetPos);
                    bool sameRoof = targetRoof == null || standingRoof == null || targetRoof == standingRoof;
                    if (sameRoof || IsRoofWalkContinuous(transform.position, targetPos))
                    {
                        yield return StartCoroutine(WalkAcrossRooftop(targetPos, stayOnStandingRoof: sameRoof));
                    }
                    else
                    {
                        Vector3 foot = FindClimbFoot(targetRoof, transform.position, targetPos)
                                       ?? new Vector3(targetPos.x, 0.05f, targetPos.z);
                        yield return StartCoroutine(ExecuteClimbDown(foot));
                        if (isDead) yield break;
                        yield return StartCoroutine(ExecuteClimb(targetPos));
                    }
                    yield return StartCoroutine(ExecuteCheckpointAction(nodeAction));
                    continue;
                }

                Vector3 groundTarget = new Vector3(targetPos.x, Mathf.Min(targetPos.y, 0.05f), targetPos.z);
                yield return StartCoroutine(ExecuteClimbDown(groundTarget));
                if (isDead) yield break;
                if (nodeAction == TacticalPathManager.NodeAction.Descendre) continue;
                if (nodeAction != TacticalPathManager.NodeAction.GarnisonFenetre) targetPos = groundTarget;
                // Pas de `continue` : la suite de la boucle exécute l'action du nœud depuis le sol.
            }
            // GarnisonFenetre exclu explicitement (correctif 2026-09-05) : la position d'un ordre
            // "Garnison Fenêtre" est la hauteur RÉELLE de la fenêtre visée, au-delà de 1.8m dès le 1er
            // étage — sans cette exclusion, le soldat escaladait la façade au lieu d'entrer dans le
            // bâtiment (ExecuteEnterGarrison, plus bas).
            else if (!isTank && targetPos.y - transform.position.y > 1.8f
                     && nodeAction != TacticalPathManager.NodeAction.Descendre
                     && nodeAction != TacticalPathManager.NodeAction.GarnisonFenetre)
            {
                yield return StartCoroutine(ExecuteClimb(targetPos));
                yield return StartCoroutine(ExecuteCheckpointAction(nodeAction));
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

            // (Déplacements sur les toits : voir la branche 1 en tête de boucle.)

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

            // Repartir vers le point suivant rompt la posture de guet/embuscade (sinon son bonus défensif
            // -50 % restait acquis pour toute la partie).
            isGuarding = false;

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

            yield return StartCoroutine(ExecuteCheckpointAction(nodeAction));
        }

        TerminerOrdres();
    }

    /// <summary>Action posturale exécutée une fois le point atteint (au sol comme sur un toit) :
    /// attendre, guetter, se cacher contre un mur, embuscade. Sans effet pour les autres actions
    /// (déplacement simple, ou action déjà accomplie par sa propre coroutine).
    /// Extrait le 2026-10-03 : la marche sur les toits sautait cette étape, donc « GUETTER SUR LE
    /// TOIT » et « ATTENDRE » n'avaient jamais d'effet en hauteur.</summary>
    private IEnumerator ExecuteCheckpointAction(TacticalPathManager.NodeAction nodeAction)
    {
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
    }

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

    /// <summary>Marche jusqu'à <paramref name="destination"/> via le NavMesh : attend le calcul du chemin
    /// (pathPending) avant de surveiller l'arrivée, avec une durée maximale pour qu'une destination
    /// inatteignable ne bloque jamais le tour.</summary>
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

    /// <summary>Marche sur la SURFACE réelle d'un toit (hauteur sondée à chaque pas, arrêt au bord, durée
    /// maximale), sans quitter l'empreinte du bâtiment.
    /// <paramref name="stayOnStandingRoof"/> = false : l'appelant a vérifié que les toits sont
    /// mitoyens jusqu'à la cible (IsRoofWalkContinuous) — l'unité passe alors d'un toit à l'autre.</summary>
    private IEnumerator WalkAcrossRooftop(Vector3 requestedTarget, bool stayOnStandingRoof = true)
    {
        BuildingStructure roofBuilding = BuildingStructure.FindBuildingAt(transform.position);

        // Destination ramenée SUR le toit quand il n'y a pas de toit mitoyen jusqu'à la cible : jamais
        // un pas dans le vide (la descente est le travail d'ExecuteClimbDown).
        Vector3 destination = requestedTarget;
        if (stayOnStandingRoof && roofBuilding != null && !roofBuilding.ContainsPoint2D(requestedTarget))
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
        SettleOnRoofSurface(BuildingStructure.FindBuildingAt(transform.position) ?? roofBuilding);
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

        LeaveRoofRegistry();
        if (roofBuilding != null && actuallyOnRoof)
        {
            roofBuilding.unitsOnRoof.Add(this);
        }
    }

    /// <summary>Retire l'unité des occupants de toit de tout bâtiment. Sans ça, un soldat redescendu
    /// (ou passé sur un toit voisin) restait inscrit sur son ancien toit et mourait avec lui si ce
    /// bâtiment était détruit plus tard (DestructibleEnvironment).</summary>
    private void LeaveRoofRegistry()
    {
        foreach (BuildingStructure b in BuildingStructure.AllBuildings)
        {
            if (b != null) b.unitsOnRoof.Remove(this);
        }
    }

    private static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

    /// <summary>Peut-on aller de <paramref name="from"/> à <paramref name="to"/> en restant sur les
    /// toits ? Vrai si une surface de toit existe sous chaque pas (tous les 0,5 m) et qu'aucune
    /// marche ne dépasse 1,2 m — cas des immeubles mitoyens d'un même îlot.</summary>
    private static bool IsRoofWalkContinuous(Vector3 from, Vector3 to)
    {
        Vector3 flat = new Vector3(to.x - from.x, 0f, to.z - from.z);
        int steps = Mathf.Max(1, Mathf.CeilToInt(flat.magnitude / 0.5f));
        float probeTop = Mathf.Max(from.y, to.y) + 3f;
        float previousY = from.y;
        for (int i = 1; i <= steps; i++)
        {
            Vector3 p = from + flat * (i / (float)steps);
            if (!Physics.Raycast(new Vector3(p.x, probeTop, p.z), Vector3.down, out RaycastHit surface, probeTop + 1f, WorldGeometryMask, QueryTriggerInteraction.Ignore))
                return false;
            if (surface.point.y <= RoofStrataThresholdY || Mathf.Abs(surface.point.y - previousY) > 1.2f)
                return false;
            previousY = surface.point.y;
        }
        return true;
    }

    /// <summary>Un endroit d'où descendre d'un toit : <c>edge</c> sur le toit, 0,6 m en retrait du
    /// bord ; <c>street</c> le point de rue juste en dessous, sur le NavMesh.</summary>
    private struct RoofStreetExit { public Vector3 edge; public Vector3 street; }

    /// <summary>Points du périmètre de <paramref name="building"/> (un tous les ~1,5 m) qui donnent
    /// réellement sur la rue. Un bord mitoyen (immeubles collés d'un îlot, cas courant en ville) n'en
    /// fait pas partie : derrière lui il y a le bâtiment voisin, pas le sol — c'est là que l'ancienne
    /// descente « droit devant » atterrissait sur le toit d'à côté.</summary>
    private static List<RoofStreetExit> FindStreetExits(BuildingStructure building)
    {
        var exits = new List<RoofStreetExit>();
        List<Vector2> poly = building != null ? building.polygonFootprint : null;
        if (poly == null || poly.Count < 3) return exits;

        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 a = poly[i];
            Vector2 b = poly[(i + 1) % poly.Count];
            float length = (b - a).magnitude;
            if (length < 0.5f) continue;
            Vector2 along = (b - a) / length;
            Vector2 normal = new Vector2(along.y, -along.x);
            int samples = Mathf.Max(1, Mathf.FloorToInt(length / 1.5f));
            for (int s = 0; s < samples; s++)
            {
                Vector2 p = a + along * ((s + 0.5f) * length / samples);
                // Sens de parcours du polygone OSM inconnu : l'extérieur est le côté hors empreinte.
                Vector2 outward = building.ContainsPoint2D(p + normal * 0.3f) ? -normal : normal;
                Vector2 onRoof = p - outward * 0.6f;
                Vector2 outside = p + outward * 1.2f;
                if (!building.ContainsPoint2D(onRoof)) continue;
                if (BuildingStructure.FindBuildingAt(new Vector3(outside.x, 0f, outside.y)) != null) continue;
                if (!NavMesh.SamplePosition(new Vector3(outside.x, 0.05f, outside.y), out NavMeshHit street, 1.5f, NavMesh.AllAreas)) continue;
                if (street.position.y > RoofStrataThresholdY) continue;
                exits.Add(new RoofStreetExit { edge = new Vector3(onRoof.x, 0f, onRoof.y), street = street.position });
            }
        }
        return exits;
    }

    /// <summary>Longueur du chemin à pied de <paramref name="from"/> à <paramref name="to"/>, ou -1 si
    /// la cible n'est pas atteignable (une cour intérieure fermée, par exemple).</summary>
    private static float WalkingDistance(Vector3 from, Vector3 to, NavMeshPath path)
    {
        if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) return -1f;
        float total = 0f;
        for (int i = 1; i < path.corners.Length; i++) total += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        return total;
    }

    /// <summary>Choisit par où descendre du toit <paramref name="roof"/> pour rejoindre
    /// <paramref name="groundTarget"/> : le plus court trajet « sur le toit + à pied dans la rue »,
    /// en n'acceptant qu'un point de rue d'où la cible est réellement atteignable à pied.</summary>
    private static bool TryPickRoofExit(BuildingStructure roof, Vector3 from, Vector3 groundTarget, out RoofStreetExit best)
    {
        best = default;
        List<RoofStreetExit> exits = FindStreetExits(roof);
        if (exits.Count == 0) return false;

        exits.Sort((p, q) => (FlatDistance(p.edge, from) + FlatDistance(groundTarget, p.street))
                   .CompareTo(FlatDistance(q.edge, from) + FlatDistance(groundTarget, q.street)));
        best = exits[0];

        if (!NavMesh.SamplePosition(new Vector3(groundTarget.x, 0.05f, groundTarget.z), out NavMeshHit targetNav, 4f, NavMesh.AllAreas))
            return true; // cible hors NavMesh : on garde le meilleur à vol d'oiseau

        // Les meilleurs candidats à vol d'oiseau sont départagés par le VRAI trajet à pied.
        var path = new NavMeshPath();
        float bestCost = float.MaxValue;
        bool foundReachable = false;
        for (int i = 0; i < exits.Count && i < 12; i++)
        {
            float walk = WalkingDistance(exits[i].street, targetNav.position, path);
            if (walk < 0f) continue;
            float cost = FlatDistance(exits[i].edge, from) + walk;
            if (cost < bestCost) { bestCost = cost; best = exits[i]; foundReachable = true; }
        }
        if (!foundReachable) best = exits[0];
        return true;
    }

    /// <summary>Point de rue au pied de <paramref name="building"/>, du côté le plus direct entre
    /// <paramref name="from"/> et <paramref name="roofTarget"/> — d'où escalader sa façade.</summary>
    private static Vector3? FindClimbFoot(BuildingStructure building, Vector3 from, Vector3 roofTarget)
    {
        List<RoofStreetExit> exits = FindStreetExits(building);
        if (exits.Count == 0) return null;
        RoofStreetExit best = exits[0];
        float bestCost = float.MaxValue;
        foreach (RoofStreetExit e in exits)
        {
            float cost = FlatDistance(e.street, from) + FlatDistance(roofTarget, e.edge);
            if (cost < bestCost) { bestCost = cost; best = e; }
        }
        return best.street;
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
        LeaveRoofRegistry();

        if (agent != null) agent.enabled = false;

        Vector3 startRoofPos = transform.position;
        BuildingStructure roofBuilding = BuildingStructure.FindBuildingAt(startRoofPos);
        Vector3 groundLandPos;

        // 1. Par où descendre : le bord qui donne sur la rue la mieux placée pour la suite du trajet
        // (TryPickRoofExit). Jusqu'au 2026-10-03, l'unité descendait « droit devant » vers la cible :
        // dans un îlot d'immeubles mitoyens, ce bord donnait sur le toit voisin, la sonde de sol
        // trouvait ce toit (descente de 0,6 m relevée sur le serveur) et l'unité était ensuite
        // téléportée dans la rue par le NavMesh.
        if (roofBuilding != null && TryPickRoofExit(roofBuilding, startRoofPos, destinationGround, out RoofStreetExit exit))
        {
            Vector3 edgeOnRoof = new Vector3(exit.edge.x, startRoofPos.y, exit.edge.z);
            if (FlatDistance(edgeOnRoof, startRoofPos) > 0.5f)
            {
                yield return StartCoroutine(WalkAcrossRooftop(edgeOnRoof));
                isRooftopSniper = false; // WalkAcrossRooftop le rétablit d'après la hauteur : on redescend juste après
                LeaveRoofRegistry();
            }
            groundLandPos = exit.street;
        }
        else
        {
            // Repli (empreinte inconnue, aucun bord sur rue) : droit vers la cible, juste au-delà de
            // l'empreinte, sur le sol du NavMesh — jamais une sonde qui accroche un toit.
            Vector3 towardTarget = destinationGround - startRoofPos;
            towardTarget.y = 0f;
            Vector3 dir = towardTarget.sqrMagnitude > 0.01f ? towardTarget.normalized : transform.forward;
            Vector3 landingXZ = startRoofPos + dir * 1.8f;
            int guard = 0;
            while (roofBuilding != null && roofBuilding.ContainsPoint2D(landingXZ) && guard++ < 40) landingXZ += dir * 0.8f;
            groundLandPos = new Vector3(landingXZ.x, 0.05f, landingXZ.z);
            if (NavMesh.SamplePosition(groundLandPos, out NavMeshHit landNav, 6f, NavMesh.AllAreas) && landNav.position.y <= RoofStrataThresholdY)
                groundLandPos = landNav.position;
        }

        startRoofPos = transform.position;
        Vector3 forwardNorm = new Vector3(groundLandPos.x - startRoofPos.x, 0f, groundLandPos.z - startRoofPos.z);
        forwardNorm = forwardNorm.sqrMagnitude > 0.0001f ? forwardNorm.normalized : transform.forward;
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
