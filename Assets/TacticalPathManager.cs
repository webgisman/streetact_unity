using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem; // Importation du nouveau système

public class TacticalPathManager : MonoBehaviour
{
    public enum GamePhase { Planification, CreationPath, Execution }
    public enum NodeAction { Continuer, Attendre5Min, Guetter, Embuscade, Escalade, GarnisonFenetre }

    [Header("Système")]
    public GamePhase phaseActuelle = GamePhase.Planification;

    [System.Serializable]
    public struct TacticalNode
    {
        public Vector3 position;
        public NodeAction action;
    }

    [Header("UI Références")]
    public GameObject menuPanel;

    public GameObject uniteSelectionnee;
    private Vector3 positionClicTemporaire;

    // Feedback visuel et tracé de ligne
    private Color couleurOriginale;
    // Pour stocker TOUTES les parties du soldat (corps, arme, etc.)
    private Renderer[] renderersSelectionnes;
    private LineRenderer lineRenderer;

    void Start()
    {
        // Initialisation automatique du LineRenderer
        lineRenderer = GetComponent<LineRenderer>();
        if (lineRenderer == null)
        {
            lineRenderer = gameObject.AddComponent<LineRenderer>();
            lineRenderer.startWidth = 0.3f;
            lineRenderer.endWidth = 0.3f;
            lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            lineRenderer.startColor = Color.cyan;
            lineRenderer.endColor = Color.blue;
        }
        lineRenderer.positionCount = 0;

        // Initialisation automatique du gestionnaire de déploiement d'unités
        if (GetComponent<UnitSpawnerUI>() == null)
        {
            gameObject.AddComponent<UnitSpawnerUI>();
        }
    }

    // --- ANIMATION UI (Bounce) ---
    private System.Collections.IEnumerator AnimateMenuBounce()
    {
        menuPanel.SetActive(true);
        Vector3 finalScale = Vector3.one;
        menuPanel.transform.localScale = Vector3.zero;

        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime * 4f; // Vitesse de l'animation
            // Formule mathématique d'un "Spring/Bounce" d'amortissement
            float scale = 1f - Mathf.Exp(-t * 8f) * Mathf.Cos(t * 15f);
            menuPanel.transform.localScale = finalScale * scale;
            yield return null;
        }
        menuPanel.transform.localScale = finalScale;
    }

    void Update()
    {
        // Bloquer l'assignation de nouveaux ordres pendant l'exécution ou pendant le placement d'unités
        if (phaseActuelle == GamePhase.Execution || UnitSpawnerUI.IsPlacingUnit) return;

        // Gestion de la création de la ligne (preview) vers la souris
        DessinerChemin();

        // 1. Si le menu est ouvert ou qu'on interagit avec l'UI, on bloque le reste
        if (menuPanel != null && menuPanel.activeSelf || EventSystem.current.IsPointerOverGameObject()) return;

        // Clic Droit pour Annuler la création de chemin en cours
        if (phaseActuelle == GamePhase.CreationPath && Pointer.current != null && Pointer.current.press.wasPressedThisFrame && Mouse.current.rightButton.wasPressedThisFrame)
        {
            phaseActuelle = GamePhase.Planification;
            if (menuPanel != null) menuPanel.SetActive(false);
            return;
        }

        bool isActionClick = false;

        // --- Clic Gauche ou Touch : Sélection ou Action ---
        if (Pointer.current != null && Pointer.current.press.wasPressedThisFrame)
        {
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) isActionClick = true;
            else if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame) isActionClick = true;
            else if (Mouse.current == null && Touchscreen.current == null) isActionClick = true; // Fallback Steam Link (Pointer sans souris/touch reconnu)
        }

        if (isActionClick)
        {
            // Vérifier si la souris est sur une interface UI (bouton) -> ignorer le clic
            if (EventSystem.current.IsPointerOverGameObject())
                return;

            Vector2 pointerPosition = Pointer.current.position.ReadValue();
            Ray ray = Camera.main.ScreenPointToRay(pointerPosition);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit))
            {
                // Chercher si on a cliqué PROCHE d'une unité
                UnitAI closestUnit = null;
                float closestDist = 4.0f; // Rayon de tolérance augmenté à 4 mètres
                
                UnitAI[] allUnits = FindObjectsByType<UnitAI>();
                foreach (UnitAI unit in allUnits)
                {
                    if (unit.isPlayerControlled)
                    {
                        Vector3 clickPos = hit.point;
                        clickPos.y = 0;
                        Vector3 unitPos = unit.transform.position;
                        unitPos.y = 0;

                        float dist = Vector3.Distance(clickPos, unitPos);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            closestUnit = unit;
                        }
                    }
                }

                if (closestUnit != null)
                {
                    // Si on a trouvé une unité proche, on la sélectionne
                    if (menuPanel != null) menuPanel.SetActive(false);
                    phaseActuelle = GamePhase.Planification;
                    SelectionnerUnite(closestUnit.gameObject);
                }
                else if (phaseActuelle == GamePhase.Planification && uniteSelectionnee != null)
                {
                    UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();
                    BuildingStructure structure = hit.collider.GetComponentInParent<BuildingStructure>();

                    // Si on a cliqué sur un bâtiment
                    if (structure != null)
                    {
                        if (unitAI != null && unitAI.isTank)
                        {
                            Debug.LogWarning("[TacticalPathManager] Les chars et véhicules ne peuvent pas entrer dans les bâtiments !");
                            AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                            if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
                            return;
                        }

                        // Clic sur une fenêtre ou façade (entre 0.5m et la hauteur max)
                        if (hit.point.y > 0.5f && hit.point.y < hit.collider.bounds.max.y - 0.5f)
                        {
                            BuildingStructure.BuildingWindow bestWin = structure.GetClosestWindow(hit.point);
                            if (bestWin != null)
                            {
                                positionClicTemporaire = bestWin.position;
                                if (menuPanel != null)
                                {
                                    StopAllCoroutines();
                                    StartCoroutine(AnimateMenuBounce());
                                }
                                return;
                            }
                        }
                    }

                    bool isClickOnRoof = hit.point.y > 2.0f;

                    // Restriction : Les véhicules blindés ne peuvent pas monter sur les toits
                    if (isClickOnRoof && unitAI != null && unitAI.isTank)
                    {
                        Debug.LogWarning("[TacticalPathManager] Les chars et véhicules ne peuvent pas monter sur les toits !");
                        AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                        if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
                        return;
                    }

                    // Vérifier si le point est valide sur le NavMesh (toit ou sol)
                    UnityEngine.AI.NavMeshHit navHit;
                    if (UnityEngine.AI.NavMesh.SamplePosition(hit.point, out navHit, 4.5f, UnityEngine.AI.NavMesh.AllAreas))
                    {
                        positionClicTemporaire = navHit.position;
                        if (menuPanel != null)
                        {
                            StopAllCoroutines();
                            StartCoroutine(AnimateMenuBounce());
                        }
                    }
                }
            }
            else
            {
                Debug.Log("[TacticalPathManager] Le Raycast n'a touché aucun collider (le sol manque-t-il d'un MeshCollider ?).");
            }
        }
    }

    private void SelectionnerUnite(GameObject unite)
    {
        // Désélectionner l'ancienne unité si nécessaire
        if (uniteSelectionnee != null && uniteSelectionnee != unite)
        {
            UnitAI ancienneUnitAI = uniteSelectionnee.GetComponent<UnitAI>();
            if (ancienneUnitAI != null) ancienneUnitAI.SetSelected(false);
        }

        if (unite != null)
        {
            UnitAI unitAI = unite.GetComponent<UnitAI>();
            
            // On ne peut sélectionner que les unités du joueur
            if (unitAI != null && unitAI.isPlayerControlled)
            {
                uniteSelectionnee = unite;
                unitAI.SetSelected(true); // Activer le cercle visuel
                
                // Son de sélection
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateHoverSound(), Camera.main.transform.position);
                
                Debug.Log("Unité sélectionnée : " + unite.name);
            }
        }
        else
        {
            if (uniteSelectionnee != null)
            {
                UnitAI ancienneUnitAI = uniteSelectionnee.GetComponent<UnitAI>();
                if (ancienneUnitAI != null) ancienneUnitAI.SetSelected(false);
            }
            uniteSelectionnee = null;
            Debug.Log("Toutes les unités sont désélectionnées.");
        }
    }

    private void DessinerChemin()
    {
        if (uniteSelectionnee != null && lineRenderer != null && (phaseActuelle == GamePhase.Planification || phaseActuelle == GamePhase.CreationPath))
        {
            UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();
            UnityEngine.AI.NavMeshAgent agent = uniteSelectionnee.GetComponent<UnityEngine.AI.NavMeshAgent>();

            if (unitAI != null && agent != null)
            {
                // --- POLISSAGE VISUEL DE LA LIGNE ---
                lineRenderer.startWidth = 0.15f;
                lineRenderer.endWidth = 0.05f; // La ligne s'affine vers la fin
                // Dégradé cyan tactique
                Gradient gradient = new Gradient();
                gradient.SetKeys(
                    new GradientColorKey[] { new GradientColorKey(new Color(0, 0.8f, 1f), 0.0f), new GradientColorKey(new Color(0, 0.2f, 1f), 1.0f) },
                    new GradientAlphaKey[] { new GradientAlphaKey(1.0f, 0.0f), new GradientAlphaKey(0.2f, 1.0f) }
                );
                lineRenderer.colorGradient = gradient;

                List<Vector3> pointsLigne = new List<Vector3>();
                Vector3 positionCourante = uniteSelectionnee.transform.position;
                pointsLigne.Add(positionCourante + Vector3.up * 0.2f);

                UnityEngine.AI.NavMeshPath path = new UnityEngine.AI.NavMeshPath();

                // Dessiner le chemin déjà validé
                for (int i = unitAI.GetCurrentNodeIndex(); i < unitAI.tacticalPath.Count; i++)
                {
                    Vector3 targetPos = unitAI.tacticalPath[i].position;
                    if (UnityEngine.AI.NavMesh.SamplePosition(targetPos, out UnityEngine.AI.NavMeshHit hitTarget, 10f, agent.areaMask))
                    {
                        targetPos = hitTarget.position;
                    }

                    if (UnityEngine.AI.NavMesh.CalculatePath(positionCourante, targetPos, agent.areaMask, path) && path.corners.Length > 1)
                    {
                        for (int j = 1; j < path.corners.Length; j++)
                        {
                            pointsLigne.Add(path.corners[j] + Vector3.up * 0.2f);
                        }
                        positionCourante = path.corners[path.corners.Length - 1];
                    }
                }

                // Ajouter le segment en cours de création vers la souris
                Vector3 currentCursorPos = (phaseActuelle == GamePhase.CreationPath) ? positionClicTemporaire : GetMousePositionOnNavMesh();
                if (currentCursorPos != Vector3.zero)
                {
                    if (UnityEngine.AI.NavMesh.CalculatePath(positionCourante, currentCursorPos, agent.areaMask, path) && path.corners.Length > 1)
                    {
                        for (int j = 1; j < path.corners.Length; j++)
                        {
                            pointsLigne.Add(path.corners[j] + Vector3.up * 0.2f);
                        }
                    }
                    else
                    {
                        pointsLigne.Add(currentCursorPos + Vector3.up * 0.2f);
                    }
                }

                lineRenderer.positionCount = pointsLigne.Count;
                lineRenderer.SetPositions(pointsLigne.ToArray());
                return;
            }
        }

        // Si aucune unité sélectionnée, on cache la ligne
        if (lineRenderer != null) lineRenderer.positionCount = 0;
    }

    private Vector3 GetMousePositionOnNavMesh()
    {
        if (Pointer.current == null) return Vector3.zero;

        Ray ray = Camera.main.ScreenPointToRay(Pointer.current.position.ReadValue());
        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            UnityEngine.AI.NavMeshHit navHit;
            if (UnityEngine.AI.NavMesh.SamplePosition(hit.point, out navHit, 1.0f, UnityEngine.AI.NavMesh.AllAreas))
                return navHit.position;
        }
        return Vector3.zero;
    }

    public void ConfirmerAction(int actionIndex)
    {
        // Son de clic UI
        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);

        TacticalNode nouveauNoeud = new TacticalNode
        {
            position = positionClicTemporaire,
            action = (NodeAction)actionIndex
        };

        if (uniteSelectionnee != null)
        {
            UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();
            if (unitAI != null)
            {
                unitAI.AddTacticalNode(nouveauNoeud);
                
                // --- APPARITION DU MARQUEUR HOLOGRAPHIQUE ---
                GameObject marker = new GameObject("WaypointMarker");
                marker.transform.position = positionClicTemporaire;
                marker.AddComponent<WaypointMarker>();
            }
        }

        menuPanel.SetActive(false);
    }

    [Header("Paramètres de Tour")]
    private AudioSource uiAudioSource;
    private Coroutine turnExecutionCoroutine;

    public void LancerExecutionTour()
    {
        if (phaseActuelle == GamePhase.Execution) return;

        // Son de validation
        if (uiAudioSource == null) uiAudioSource = gameObject.AddComponent<AudioSource>();
        AudioClip startSound = ProceduralAudioBuilder.CreateClickSound();
        if (startSound != null) uiAudioSource.PlayOneShot(startSound, 0.7f);

        phaseActuelle = GamePhase.Execution;
        Debug.Log("--- DÉBUT DE LA PHASE D'EXÉCUTION (Action en cours) ---");
        
        SelectionnerUnite(null); // On désélectionne tout
        if (menuPanel != null) menuPanel.SetActive(false);

        UnitAI[] allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude);
        List<UnitAI> livingUnits = new List<UnitAI>();
        foreach (var u in allUnits)
        {
            if (u != null && !u.isDead) livingUnits.Add(u);
        }

        // Lancer les ordres pour toutes les unités (IA et Joueur)
        foreach (var unit in livingUnits)
        {
            if (!unit.isPlayerControlled)
            {
                unit.PlanifierTourIA();
            }
            unit.ExecuterOrdres();
        }

        if (turnExecutionCoroutine != null) StopCoroutine(turnExecutionCoroutine);
        turnExecutionCoroutine = StartCoroutine(ExecuterTourCoroutine());
    }

    /// <summary>
    /// Coroutine d'action dynamique : le tour dure le temps que les unités parcourent leur chemin, 
    /// exécutent leurs checkpoints (Attendre, Guetter), tirent sur les ennemis croisés,
    /// puis balayent et sécurisent leur zone d'arrivée avant de redonner la main.
    /// </summary>
    private System.Collections.IEnumerator ExecuterTourCoroutine()
    {
        // Laisser le temps aux agents et coroutines de démarrer
        yield return new WaitForSeconds(0.4f);

        // 1. PHASE DE PROGRESSION & ACTIONS AUX CHECKPOINTS
        // L'action dure tant que des unités avancent ou exécutent des pauses tactiques
        float safetyMovementTimer = 0f;
        while (UnitesEncoreEnDeplacementOuAction() && safetyMovementTimer < 18.0f)
        {
            safetyMovementTimer += Time.deltaTime;
            yield return null;
        }

        // 2. PHASE DE BALAYAGE FINAL ET SÉCURISATION DU SECTEUR
        // Si les unités sont arrivées mais qu'un duel est en cours (cibles visibles en portée),
        // on laisse jusqu'à 2 secondes pour échanger les tirs finaux
        float combatResolutionTimer = 0f;
        while (UnitesEncoreEnCombat() && combatResolutionTimer < 2.0f)
        {
            combatResolutionTimer += Time.deltaTime;
            yield return null;
        }

        // Si personne ne bougeait et personne n'était en combat, laisser un bref instant de réactivité (0.8s)
        if (safetyMovementTimer == 0f && combatResolutionTimer == 0f)
        {
            yield return new WaitForSeconds(0.8f);
        }

        ForcerFinExecution();
    }

    /// <summary>
    /// Vérifie si au moins une unité vivante est en train de marcher ou d'effectuer une action de checkpoint.
    /// </summary>
    private bool UnitesEncoreEnDeplacementOuAction()
    {
        UnitAI[] allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude);
        foreach (var u in allUnits)
        {
            if (u != null && !u.isDead && u.IsMovingOrActing())
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Vérifie si au moins une unité a une cible ennemie vivante dans sa ligne de mire et à portée.
    /// </summary>
    private bool UnitesEncoreEnCombat()
    {
        UnitAI[] allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude);
        foreach (var u in allUnits)
        {
            if (u != null && !u.isDead && u.HasActiveTargetInRange())
            {
                return true;
            }
        }
        return false;
    }

    public void ForcerFinExecution()
    {
        if (turnExecutionCoroutine != null)
        {
            StopCoroutine(turnExecutionCoroutine);
            turnExecutionCoroutine = null;
        }

        phaseActuelle = GamePhase.Planification;

        UnitAI[] allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude);
        foreach (var u in allUnits)
        {
            if (u != null && !u.isDead)
            {
                u.StopAllCoroutines();
                u.ResetOrderState();
            }
        }

        // Nettoyer les marqueurs au sol
        foreach (var marker in FindObjectsByType<WaypointMarker>(FindObjectsInactive.Exclude))
        {
            Destroy(marker.gameObject);
        }

        Debug.Log("--- FIN DU TOUR. SECTEUR BALAYÉ. RETOUR À LA PLANIFICATION ---");
    }

    public void SignalerFinMouvement(UnitAI unit)
    {
        // Résolution dynamique gérée par ExecuterTourCoroutine
    }

    void OnGUI()
    {
        if (phaseActuelle == GamePhase.Planification)
        {
            // Dessine un bouton UI basique en bas à droite
            if (GUI.Button(new Rect(Screen.width - 220, Screen.height - 80, 200, 60), "FIN DE TOUR\n(Lancer l'Action)"))
            {
                LancerExecutionTour();
            }
        }
        else
        {
            // Affiche un label d'indication
            GUIStyle style = new GUIStyle(GUI.skin.box);
            style.fontSize = 18;
            style.normal.textColor = Color.yellow;
            GUI.Box(new Rect(Screen.width / 2 - 160, 20, 320, 50), "ACTION DU TOUR EN COURS...", style);

            // Bouton pour passer immédiatement si besoin
            if (GUI.Button(new Rect(Screen.width - 150, 20, 130, 40), "Passer l'Action"))
            {
                ForcerFinExecution();
            }
        }
    }
}