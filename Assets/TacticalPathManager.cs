using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem; // Importation du nouveau système

public class TacticalPathManager : MonoBehaviour
{
    public enum GamePhase { Planification, CreationPath, Execution }
    public enum NodeAction { Continuer = 0, Attendre5Min = 1, Guetter = 2, Embuscade = 3, Escalade = 4, GarnisonFenetre = 5, EntrerBatiment = 6, SortirBatiment = 7, GuetterPorte = 8, Attendre30s = 9, SeCacher = 10, TirMortier = 11 }

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
    private StreetAct.Interaction.DoorInteraction selectedDoor = null;
    private StreetAct.Interaction.WindowInteraction selectedWindow = null;
    private BuildingStructure selectedBuilding = null;
    private bool isBuildingSelected = false;
    private bool isDoorSelected = false;
    private bool isWindowSelected = false;
    private bool isExitDoorAction = false;
    private bool isGroundCheckpointSelected = false;
    private bool isNearBuildingWall = false;
    private float contextMenuFade = 0f; // Anim. légère (pop + fondu) des menus contextuels
    private Rect activeMenuRect = Rect.zero;

    private Color couleurOriginale;
    // Pour stocker TOUTES les parties du soldat (corps, arme, etc.)
    private Renderer[] renderersSelectionnes;
    private LineRenderer lineRenderer;
    private readonly List<Vector3> cachedLinePoints = new List<Vector3>();
    private UnityEngine.AI.NavMeshPath cachedNavPath;
    private Gradient cachedPathGradient;
    private static Gradient selectedGradient;
    private static Gradient normalGradient;
    private static bool isPathsDirty = true;

    public static void SetPathsDirty() { isPathsDirty = true; }

    // Tracking for Tap vs Drag
    private Vector2 pointerDownPos;
    private bool isPointerDown = false;

    public static TacticalPathManager Instance { get; private set; }

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        Instance = this;
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

        // Initialisation automatique du Radar Tactique
        if (TacticalRadarUI.Instance != null)
        {
            Debug.Log("[TacticalPathManager] 🛰️ Radar Tactique prêt et connecté.");
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

        // Tracé fluide et ultra-léger (recalculé uniquement si dirty ou en phase dynamique)
        if (isPathsDirty || phaseActuelle == GamePhase.CreationPath)
        {
            DessinerTousLesChemins();
        }

        // 1. Si le panneau d'action (porte/fenêtre/bâtiment) est ouvert, on attend l'interaction du joueur
        if (menuPanel != null && menuPanel.activeSelf) return;

        // Lecture universelle du pointeur (Tactile Mobile & Souris PC)
        Vector2 pointerPosition = Vector2.zero;
        bool isPointerActive = false;
        bool wasPressed = false;
        bool wasReleased = false;

        if (Touchscreen.current != null && Touchscreen.current.touches.Count > 0)
        {
            var touch = Touchscreen.current.touches[0];
            pointerPosition = touch.position.ReadValue();
            isPointerActive = true;
            wasPressed = touch.press.wasPressedThisFrame;
            wasReleased = touch.press.wasReleasedThisFrame;
        }
        else if (Input.touchCount > 0)
        {
            Touch t = Input.GetTouch(0);
            pointerPosition = t.position;
            isPointerActive = true;
            wasPressed = (t.phase == UnityEngine.TouchPhase.Began);
            wasReleased = (t.phase == UnityEngine.TouchPhase.Ended);
        }
        else if (Pointer.current != null)
        {
            pointerPosition = Pointer.current.position.ReadValue();
            isPointerActive = true;
            wasPressed = Pointer.current.press.wasPressedThisFrame;
            wasReleased = Pointer.current.press.wasReleasedThisFrame;
        }
        else if (Input.mousePresent)
        {
            pointerPosition = Input.mousePosition;
            isPointerActive = true;
            wasPressed = Input.GetMouseButtonDown(0);
            wasReleased = Input.GetMouseButtonUp(0);
        }

        Vector2 guiMousePos = new Vector2(pointerPosition.x, Screen.height - pointerPosition.y);
        Rect endTurnRect = new Rect(Screen.width - 220, Screen.height - 80, 200, 60);

        // Clic Droit : Annulation rapide du menu ou du dernier checkpoint
        if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
        {
            if (isDoorSelected || isWindowSelected || isBuildingSelected || isGroundCheckpointSelected)
            {
                isDoorSelected = false;
                isWindowSelected = false;
                isBuildingSelected = false;
                isGroundCheckpointSelected = false;
                activeMenuRect = Rect.zero;
                return;
            }
            else if (uniteSelectionnee != null)
            {
                UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();
                if (unitAI != null && unitAI.tacticalPath.Count > 0)
                {
                    unitAI.RemoveLastTacticalNode();
                    DessinerTousLesChemins();
                    Debug.Log($"[{unitAI.gameObject.name}] ↩️ Dernier checkpoint supprimé.");
                }
                return;
            }
        }

        bool isActionClick = false;

        // --- Clic Gauche ou Touch : Sélection ou Action (TAP ou CLIC DIRECT) ---
        if (isPointerActive)
        {
            if (wasPressed)
            {
                isPointerDown = true;
                pointerDownPos = pointerPosition;
                // Sur PC (souris), le clic est immédiat à l'appui
                if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                {
                    isActionClick = true;
                }
                else if (Input.GetMouseButtonDown(0))
                {
                    isActionClick = true;
                }
            }
            else if (wasReleased && isPointerDown)
            {
                isPointerDown = false;
                // Sur Mobile (Touch), déclenchement au relâchement si peu de déplacement
                if (Vector2.Distance(pointerDownPos, pointerPosition) < 45f)
                {
                    isActionClick = true;
                }
            }
        }

        if (isActionClick)
        {
            // Bloquer si le joueur est en train de déployer une nouvelle unité depuis le QG
            if (UnitSpawnerUI.IsPlacingUnit) return;

            // Vérifier si le clic est sur un bouton spécifique de l'interface
            if (activeMenuRect != Rect.zero && activeMenuRect.Contains(guiMousePos)) return;
            if (endTurnRect.Contains(guiMousePos)) return;
            if (UnitSpawnerUI.Instance != null && UnitSpawnerUI.Instance.IsPointerOverOnGUI(pointerPosition)) return;

            // 1. Détection ultra-tolérante des unités en espace écran (Mobile Forgiving Touch)
            UnitAI closestUnit = null;
            float maxTouchRadiusPx = 80f * (Screen.dpi > 0 ? Screen.dpi / 160f : 1.5f);
            float closestScreenDist = maxTouchRadiusPx;

            for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
            {
                UnitAI unit = UnitAI.AllLivingUnits[i];
                if (unit != null && unit.isPlayerControlled && !unit.isDead)
                {
                    Vector3 screenPoint = Camera.main.WorldToScreenPoint(unit.transform.position + Vector3.up * 1.0f);
                    if (screenPoint.z > 0) // Devant la caméra
                    {
                        float dist = Vector2.Distance(pointerPosition, new Vector2(screenPoint.x, screenPoint.y));
                        if (dist < closestScreenDist)
                        {
                            closestScreenDist = dist;
                            closestUnit = unit;
                        }
                    }
                }
            }

            if (closestUnit != null)
            {
                if (menuPanel != null) menuPanel.SetActive(false);
                isDoorSelected = false;
                isWindowSelected = false;
                isBuildingSelected = false;
                isGroundCheckpointSelected = false;
                activeMenuRect = Rect.zero;
                phaseActuelle = GamePhase.Planification;
                SelectionnerUnite(closestUnit.gameObject);
                return;
            }

            Ray ray = Camera.main.ScreenPointToRay(pointerPosition);
            RaycastHit hit;
            Vector3 hitPoint = Vector3.zero;
            bool hasHit = false;

            if (Physics.Raycast(ray, out hit))
            {
                hitPoint = hit.point;
                hasHit = true;
            }
            else
            {
                Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
                if (groundPlane.Raycast(ray, out float enter))
                {
                    hitPoint = ray.GetPoint(enter);
                    hasHit = true;
                }
            }

            if (hasHit && phaseActuelle == GamePhase.Planification && uniteSelectionnee != null)
            {
                UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();

                // 0. Mortier / Artillerie : tout clic est une cible de tir direct
                if (unitAI != null && unitAI.isMortar)
                {
                    positionClicTemporaire = hitPoint;
                    isBuildingSelected = false;
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = true;

                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    if (menuPanel != null) menuPanel.SetActive(false);
                    return;
                }

                // 1. Détection clic sur porte
                StreetAct.Interaction.DoorInteraction clickedDoor = null;
                if (hit.collider != null)
                {
                    clickedDoor = hit.collider.GetComponent<StreetAct.Interaction.DoorInteraction>() 
                               ?? hit.collider.GetComponentInParent<StreetAct.Interaction.DoorInteraction>();
                }

                if (clickedDoor != null)
                {
                    if (unitAI != null && unitAI.isTank)
                    {
                        Debug.LogWarning("[TacticalPathManager] Les blindés ne peuvent pas entrer dans les bâtiments !");
                        AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                        if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
                        return;
                    }

                    selectedDoor = clickedDoor;
                    selectedWindow = null;
                    selectedBuilding = null;
                    isBuildingSelected = false;
                    isDoorSelected = true;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = false;
                    clickedDoor.SetHighlight(true);

                    if (clickedDoor.building != null && clickedDoor.building.tacticalVisibility != null)
                    {
                        clickedDoor.building.tacticalVisibility.SetPlanificationPreview(true);
                    }

                    if (unitAI != null && unitAI.currentBuilding == clickedDoor.building)
                    {
                        isExitDoorAction = true;
                        positionClicTemporaire = clickedDoor.GetOutsidePosition();
                    }
                    else
                    {
                        isExitDoorAction = false;
                        positionClicTemporaire = clickedDoor.doorData.position;
                    }

                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    if (menuPanel != null) menuPanel.SetActive(false);
                    return;
                }

                // 2. Détection clic sur fenêtre
                StreetAct.Interaction.WindowInteraction clickedWindow = null;
                if (hit.collider != null)
                {
                    clickedWindow = hit.collider.GetComponent<StreetAct.Interaction.WindowInteraction>() 
                                 ?? hit.collider.GetComponentInParent<StreetAct.Interaction.WindowInteraction>();
                }

                if (clickedWindow != null)
                {
                    if (unitAI != null && unitAI.isTank)
                    {
                        Debug.LogWarning("[TacticalPathManager] Les blindés ne peuvent pas utiliser les fenêtres !");
                        AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                        if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
                        return;
                    }

                    selectedWindow = clickedWindow;
                    selectedDoor = null;
                    selectedBuilding = null;
                    isBuildingSelected = false;
                    isWindowSelected = true;
                    isDoorSelected = false;
                    isGroundCheckpointSelected = false;
                    positionClicTemporaire = clickedWindow.GetInteriorStancePosition();

                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    if (menuPanel != null) menuPanel.SetActive(false);
                    return;
                }

                // 3. Détection Polygone de Bâtiment 2D/3D (Intact vs Ruines)
                BuildingStructure structure = BuildingStructure.FindBuildingAt(hitPoint);
                if (structure == null && hit.collider != null)
                {
                    structure = hit.collider.GetComponentInParent<BuildingStructure>();
                }

                bool isRubblePoint = DestructibleEnvironment.IsPositionInRubble(hitPoint) ||
                                     (structure != null && structure.GetComponent<DestructibleEnvironment>() != null && structure.GetComponent<DestructibleEnvironment>().isDestroyed);

                // CAS BÂTIMENT INTACT : L'infanterie tape dans le polygone
                if (structure != null && !isRubblePoint)
                {
                    if (unitAI != null && unitAI.isTank)
                    {
                        Debug.LogWarning("[TacticalPathManager] Les chars ne peuvent pas entrer dans les bâtiments intacts !");
                        AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                        if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
                        return;
                    }

                    selectedBuilding = structure;
                    selectedDoor = null;
                    selectedWindow = null;
                    isBuildingSelected = true;
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = false;
                    positionClicTemporaire = hitPoint;

                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    if (menuPanel != null) menuPanel.SetActive(false);
                    return;
                }

                // CAS SOL NORMAL / RUINES / TOIT EXISTANT
                bool isUnitPlanningInside = (unitAI != null && (unitAI.currentBuilding != null || (unitAI.tacticalPath.Count > 0 && unitAI.tacticalPath[unitAI.tacticalPath.Count - 1].action == NodeAction.EntrerBatiment)));
                bool isSelectedOnRoof = (unitAI != null && (unitAI.isRooftopSniper || unitAI.transform.position.y > 2.2f));

                UnityEngine.AI.NavMeshHit navHit;
                bool hasNavMesh = UnityEngine.AI.NavMesh.SamplePosition(hitPoint, out navHit, 4.5f, UnityEngine.AI.NavMesh.AllAreas);

                if (hasNavMesh || isUnitPlanningInside || isRubblePoint || isSelectedOnRoof)
                {
                    if (isRubblePoint || isUnitPlanningInside) positionClicTemporaire = new Vector3(hitPoint.x, 0.05f, hitPoint.z);
                    else if (isSelectedOnRoof) positionClicTemporaire = hitPoint;
                    else positionClicTemporaire = navHit.position;

                    isBuildingSelected = false;
                    selectedBuilding = null;
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = true;

                    isNearBuildingWall = false;
                    if (!isSelectedOnRoof && !isUnitPlanningInside)
                    {
                        Collider[] nearby = Physics.OverlapSphere(positionClicTemporaire, 2.2f);
                        foreach (var c in nearby)
                        {
                            if (c.GetComponentInParent<BuildingStructure>() != null || c.gameObject.name.Contains("Building") || c.gameObject.name.Contains("Mur") || c.gameObject.name.Contains("Wall") || c.gameObject.name.Contains("Polygone"))
                            {
                                isNearBuildingWall = true;
                                break;
                            }
                        }
                    }

                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    if (menuPanel != null) menuPanel.SetActive(false);
                }
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

        isBuildingSelected = false;
        selectedBuilding = null;
        isDoorSelected = false;
        isWindowSelected = false;
        isGroundCheckpointSelected = false;
        activeMenuRect = Rect.zero;

        uniteSelectionnee = unite;

        if (uniteSelectionnee != null)
        {
            UnitAI unitAI = unite.GetComponent<UnitAI>();
            
            // On ne peut sélectionner que les unités du joueur
            if (unitAI != null && unitAI.isPlayerControlled)
            {
                unitAI.SetSelected(true); // Activer le cercle visuel
                
                // Si l'unité est à l'intérieur d'un bâtiment, ouvrir le toit en preview immédiate !
                if (unitAI.currentBuilding != null && !unitAI.isRooftopSniper)
                {
                    if (unitAI.currentBuilding.tacticalVisibility != null)
                    {
                        unitAI.currentBuilding.tacticalVisibility.SetPlanificationPreview(true);
                    }
                }

                // Son de sélection
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateHoverSound(), Camera.main.transform.position);
                Debug.Log("Unité sélectionnée : " + unite.name);
                isPathsDirty = true;
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
            isPathsDirty = true;

            // Réinitialiser les previews de bâtiments non occupés
            foreach (var b in BuildingStructure.AllBuildings)
            {
                if (b != null && b.tacticalVisibility != null && !b.IsAnyUnitInside())
                {
                    b.tacticalVisibility.SetPlanificationPreview(false);
                }
            }
            Debug.Log("Toutes les unités sont désélectionnées.");
        }
    }

    private void DessinerTousLesChemins()
    {
        if (phaseActuelle != GamePhase.Planification && phaseActuelle != GamePhase.CreationPath && phaseActuelle != GamePhase.Execution)
        {
            for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
            {
                UnitAI u = UnitAI.AllLivingUnits[i];
                if (u != null && u.tacticalLineRenderer != null) u.tacticalLineRenderer.positionCount = 0;
            }
            if (lineRenderer != null) lineRenderer.positionCount = 0;
            return;
        }

        if (selectedGradient == null)
        {
            selectedGradient = new Gradient();
            selectedGradient.SetKeys(
                new GradientColorKey[] { new GradientColorKey(new Color(0f, 0.95f, 1f), 0.0f), new GradientColorKey(new Color(0f, 0.45f, 1f), 1.0f) },
                new GradientAlphaKey[] { new GradientAlphaKey(1.0f, 0.0f), new GradientAlphaKey(0.85f, 1.0f) }
            );

            normalGradient = new Gradient();
            normalGradient.SetKeys(
                new GradientColorKey[] { new GradientColorKey(new Color(0.2f, 0.7f, 1f), 0.0f), new GradientColorKey(new Color(0.1f, 0.35f, 0.8f), 1.0f) },
                new GradientAlphaKey[] { new GradientAlphaKey(0.65f, 0.0f), new GradientAlphaKey(0.40f, 1.0f) }
            );
        }

        for (int uIdx = 0; uIdx < UnitAI.AllLivingUnits.Count; uIdx++)
        {
            UnitAI unitAI = UnitAI.AllLivingUnits[uIdx];
            if (unitAI == null || unitAI.isDead || !unitAI.isPlayerControlled)
            {
                if (unitAI != null && unitAI.tacticalLineRenderer != null) unitAI.tacticalLineRenderer.positionCount = 0;
                continue;
            }

            bool isSelected = (uniteSelectionnee == unitAI.gameObject);
            bool hasPath = (unitAI.tacticalPath.Count > 0);

            if (!hasPath && !isSelected)
            {
                if (unitAI.tacticalLineRenderer != null) unitAI.tacticalLineRenderer.positionCount = 0;
                continue;
            }

            if (unitAI.tacticalLineRenderer == null)
            {
                GameObject lineGo = new GameObject("TacticalLine_" + unitAI.name);
                lineGo.transform.SetParent(unitAI.transform, false);
                lineGo.layer = 0; // Layer par défaut (toujours visible pour Camera 2D et 3D)
                unitAI.tacticalLineRenderer = lineGo.AddComponent<LineRenderer>();
                
                Material lineMat = SafeMaterialFactory.CreateUnlit(Color.white);
                lineMat.enableInstancing = true;
                unitAI.tacticalLineRenderer.sharedMaterial = lineMat;
            }

            LineRenderer lr = unitAI.tacticalLineRenderer;
            lr.startWidth = isSelected ? 0.22f : 0.12f;
            lr.endWidth = isSelected ? 0.10f : 0.05f;
            lr.colorGradient = isSelected ? selectedGradient : normalGradient;

            // Recalculer le chemin uniquement s'il est marqué 'dirty'
            if (unitAI.isPathDirty || isPathsDirty || unitAI.cachedDrawPoints.Count == 0 || (isSelected && positionClicTemporaire != Vector3.zero))
            {
                unitAI.cachedDrawPoints.Clear();
                Vector3 positionCourante = unitAI.transform.position;
                unitAI.cachedDrawPoints.Add(positionCourante + Vector3.up * 0.2f);

                if (cachedNavPath == null) cachedNavPath = new UnityEngine.AI.NavMeshPath();
                UnityEngine.AI.NavMeshAgent agent = unitAI.GetComponent<UnityEngine.AI.NavMeshAgent>();
                int areaMask = (agent != null) ? agent.areaMask : UnityEngine.AI.NavMesh.AllAreas;

                for (int i = unitAI.GetCurrentNodeIndex(); i < unitAI.tacticalPath.Count; i++)
                {
                    Vector3 targetPos = unitAI.tacticalPath[i].position;
                    bool isNodeOnRoof = targetPos.y > 1.8f;

                    if (!isNodeOnRoof && UnityEngine.AI.NavMesh.SamplePosition(targetPos, out UnityEngine.AI.NavMeshHit hitTarget, 10f, areaMask))
                    {
                        targetPos = hitTarget.position;
                    }

                    if (isNodeOnRoof || positionCourante.y > 1.8f || DestructibleEnvironment.IsPositionInRubble(targetPos))
                    {
                        unitAI.cachedDrawPoints.Add(targetPos + Vector3.up * 0.2f);
                        positionCourante = targetPos;
                    }
                    else if (UnityEngine.AI.NavMesh.CalculatePath(positionCourante, targetPos, areaMask, cachedNavPath) && cachedNavPath.corners.Length > 1)
                    {
                        for (int j = 1; j < cachedNavPath.corners.Length; j++)
                        {
                            unitAI.cachedDrawPoints.Add(cachedNavPath.corners[j] + Vector3.up * 0.2f);
                        }
                        positionCourante = cachedNavPath.corners[cachedNavPath.corners.Length - 1];
                    }
                    else
                    {
                        unitAI.cachedDrawPoints.Add(targetPos + Vector3.up * 0.2f);
                        positionCourante = targetPos;
                    }
                }

                // Prévisualisation pour l'unité sélectionnée vers la position du clic temporaire
                if (isSelected && (phaseActuelle == GamePhase.Planification || phaseActuelle == GamePhase.CreationPath) && positionClicTemporaire != Vector3.zero)
                {
                    if (UnityEngine.AI.NavMesh.CalculatePath(positionCourante, positionClicTemporaire, areaMask, cachedNavPath) && cachedNavPath.corners.Length > 1)
                    {
                        for (int j = 1; j < cachedNavPath.corners.Length; j++)
                        {
                            unitAI.cachedDrawPoints.Add(cachedNavPath.corners[j] + Vector3.up * 0.2f);
                        }
                    }
                    else
                    {
                        unitAI.cachedDrawPoints.Add(positionClicTemporaire + Vector3.up * 0.2f);
                    }
                }

                unitAI.isPathDirty = false;
            }

            lr.positionCount = unitAI.cachedDrawPoints.Count;
            for (int p = 0; p < unitAI.cachedDrawPoints.Count; p++)
            {
                lr.SetPosition(p, unitAI.cachedDrawPoints[p]);
            }
        }
        isPathsDirty = false;
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
        // Son de clic UI et Vibration
        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
#if UNITY_ANDROID || UNITY_IOS
        Handheld.Vibrate();
#endif

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
                // Si l'action est d'ENTRER dans le bâtiment, ouvrir immédiatement le toit en mode Planification !
                if ((NodeAction)actionIndex == NodeAction.EntrerBatiment && selectedDoor != null && selectedDoor.building != null)
                {
                    if (selectedDoor.building.tacticalVisibility != null)
                    {
                        selectedDoor.building.tacticalVisibility.SetPlanificationPreview(true);
                    }
                    unitAI.currentBuilding = selectedDoor.building;
                    Debug.Log($"<color=green>[TacticalPathManager] 🚪 Entrée confirmée : Toit de {selectedDoor.building.gameObject.name} ouvert en direct pour continuer le tracé intérieur !</color>");
                }
                else if ((NodeAction)actionIndex == NodeAction.SortirBatiment && selectedDoor != null && selectedDoor.building != null)
                {
                    if (selectedDoor.building.tacticalVisibility != null && !selectedDoor.building.IsAnyUnitInside())
                    {
                        selectedDoor.building.tacticalVisibility.SetPlanificationPreview(false);
                    }
                    unitAI.currentBuilding = null;
                }

                unitAI.AddTacticalNode(nouveauNoeud);
                
                // --- APPARITION DU MARQUEUR HOLOGRAPHIQUE ---
                GameObject marker = new GameObject("WaypointMarker");
                marker.transform.position = positionClicTemporaire;
                WaypointMarker wm = marker.AddComponent<WaypointMarker>();
                if ((NodeAction)actionIndex == NodeAction.TirMortier)
                {
                    wm.isArtilleryTarget = true;
                }
            }
        }

        isDoorSelected = false;
        isWindowSelected = false;
        isBuildingSelected = false;
        selectedBuilding = null;
        isGroundCheckpointSelected = false;
        activeMenuRect = Rect.zero;

        if (menuPanel != null) menuPanel.SetActive(false);
    }

    public void ConfirmerBuildingAction(int choice)
    {
        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
#if UNITY_ANDROID || UNITY_IOS
        Handheld.Vibrate();
#endif
        if (selectedBuilding == null || uniteSelectionnee == null) return;
        UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();
        if (unitAI == null) return;

        if (choice == 1) // Infiltration / Intérieur
        {
            if (selectedBuilding.tacticalVisibility != null)
            {
                selectedBuilding.tacticalVisibility.SetPlanificationPreview(true);
            }

            var door = selectedBuilding.GetClosestDoor(unitAI.transform.position);
            if (door != null && unitAI.currentBuilding != selectedBuilding)
            {
                unitAI.AddTacticalNode(new TacticalNode { position = door.position, action = NodeAction.EntrerBatiment });
            }
            unitAI.currentBuilding = selectedBuilding;

            Vector3 insidePos = new Vector3(positionClicTemporaire.x, 0.05f, positionClicTemporaire.z);
            unitAI.AddTacticalNode(new TacticalNode { position = insidePos, action = NodeAction.Continuer });

            GameObject marker = new GameObject("WaypointMarker");
            marker.transform.position = insidePos;
            marker.AddComponent<WaypointMarker>();
        }
        else if (choice == 2) // Escalade / Toit
        {
            float roofHeight = (selectedBuilding.height > 0) ? selectedBuilding.height : 6.0f;
            Vector3 roofPos = new Vector3(positionClicTemporaire.x, roofHeight, positionClicTemporaire.z);
            unitAI.AddTacticalNode(new TacticalNode { position = roofPos, action = NodeAction.Escalade });

            GameObject marker = new GameObject("WaypointMarker");
            marker.transform.position = roofPos;
            marker.AddComponent<WaypointMarker>();
        }
        else if (choice == 3) // Porte la plus proche
        {
            var door = selectedBuilding.GetClosestDoor(unitAI.transform.position);
            Vector3 doorPos = (door != null) ? door.position : positionClicTemporaire;
            unitAI.AddTacticalNode(new TacticalNode { position = doorPos, action = NodeAction.Continuer });

            GameObject marker = new GameObject("WaypointMarker");
            marker.transform.position = doorPos;
            marker.AddComponent<WaypointMarker>();
        }

        isBuildingSelected = false;
        selectedBuilding = null;
        isDoorSelected = false;
        isWindowSelected = false;
        isGroundCheckpointSelected = false;
        activeMenuRect = Rect.zero;

        if (menuPanel != null) menuPanel.SetActive(false);
        DessinerTousLesChemins();
    }

    [Header("Paramètres de Tour")]
    private AudioSource uiAudioSource;
    private Coroutine turnExecutionCoroutine;

    public void LancerExecutionTour()
    {
        if (phaseActuelle == GamePhase.Execution) return;

        // Son de validation et Vibration
        if (uiAudioSource == null) uiAudioSource = gameObject.AddComponent<AudioSource>();
        AudioClip startSound = ProceduralAudioBuilder.CreateClickSound();
        if (startSound != null) uiAudioSource.PlayOneShot(startSound, 0.7f);
#if UNITY_ANDROID || UNITY_IOS
        Handheld.Vibrate();
#endif

        phaseActuelle = GamePhase.Execution;
        Debug.Log("--- DÉBUT DE LA PHASE D'EXÉCUTION (Action en cours) ---");
        
        SelectionnerUnite(null); // On désélectionne tout
        if (menuPanel != null) menuPanel.SetActive(false);

        // Nettoyer les anciens marqueurs de waypoints holographiques au début de l'exécution
        WaypointMarker[] existingMarkers = FindObjectsByType<WaypointMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var wm in existingMarkers)
        {
            if (wm != null) Destroy(wm.gameObject);
        }

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
        // L'action dure tant que des unités avancent ou exécutent des pauses tactiques (supporte l'attente 30s)
        float safetyMovementTimer = 0f;
        while (UnitesEncoreEnDeplacementOuAction() && safetyMovementTimer < 45.0f)
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
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
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
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
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

        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
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
        // Mise à l'échelle automatique +50% pour écrans tactiles mobiles
        Matrix4x4 origMat = GUI.matrix;
        float uiScale = Mathf.Clamp(Screen.width / 480f, 1.35f, 2.2f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

        float virtualW = Screen.width / uiScale;
        float virtualH = Screen.height / uiScale;

        if (phaseActuelle == GamePhase.Planification)
        {
            // --- BARRE DE CONTRÔLE TACTILE MOBILE (BOTTOM DOCK) ---
            GUIStyle endTurnBtnStyle = new GUIStyle(GUI.skin.button);
            endTurnBtnStyle.fontSize = 13;
            endTurnBtnStyle.fontStyle = FontStyle.Bold;
            endTurnBtnStyle.normal.textColor = Color.white;

            // Bouton Fin de Tour (Bas Droite)
            if (ProceduralIconFactory.IconButton(new Rect(virtualW - 145, virtualH - 58, 135, 46), ProceduralIconFactory.Check(), "▶️ FIN TOUR", endTurnBtnStyle))
            {
                LancerExecutionTour();
            }

            // Barre contextuelle épurée lorsqu'une unité est sélectionnée (Bas Gauche)
            if (uniteSelectionnee != null)
            {
                GUIStyle touchBtnStyle = new GUIStyle(GUI.skin.button);
                touchBtnStyle.fontSize = 11;
                touchBtnStyle.fontStyle = FontStyle.Bold;

                // 1. Désélectionner (Croix rouge compacte)
                touchBtnStyle.normal.textColor = new Color(1f, 0.45f, 0.45f);
                if (GUI.Button(new Rect(14, virtualH - 58, 50, 46), "❌", touchBtnStyle))
                {
                    SelectionnerUnite(null);
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = false;
                    activeMenuRect = Rect.zero;
                }

                // 2. Annuler dernier point
                touchBtnStyle.normal.textColor = new Color(1f, 0.85f, 0.2f);
                if (GUI.Button(new Rect(70, virtualH - 58, 95, 46), "↩️ Annuler", touchBtnStyle))
                {
                    UnitAI uAI = uniteSelectionnee.GetComponent<UnitAI>();
                    if (uAI != null && uAI.tacticalPath.Count > 0)
                    {
                        uAI.RemoveLastTacticalNode();
                        DessinerTousLesChemins();
                        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    }
                    isGroundCheckpointSelected = false;
                    activeMenuRect = Rect.zero;
                }

                // 3. Bouton Vue Action 3D (Seulement en vue 2D)
                bool is2DMode = CameraStateManager.Instance == null || CameraStateManager.Instance.CurrentState == CameraStateManager.CameraState.Command;
                if (is2DMode)
                {
                    touchBtnStyle.normal.textColor = new Color(0.2f, 0.95f, 1f);
                    if (ProceduralIconFactory.IconButton(new Rect(172, virtualH - 58, 125, 46), ProceduralIconFactory.Cube3D(), "🔍 VUE 3D", touchBtnStyle))
                    {
                        if (CameraStateManager.Instance != null)
                        {
                            CameraStateManager.Instance.Enter3DView(uniteSelectionnee.transform);
                            isDoorSelected = false;
                            isWindowSelected = false;
                            if (menuPanel != null) menuPanel.SetActive(false);
                        }
                    }
                }
            }

            bool isAnyMenuDrawn = false;

            // MENU CONTEXTUEL DE BÂTIMENT (POLYGON 2D / 3D)
            if (uniteSelectionnee != null && isBuildingSelected && selectedBuilding != null)
            {
                isAnyMenuDrawn = true;
                contextMenuFade = UIAnimator.Advance(contextMenuFade, true, 8f);
                UIAnimator.ApplyFadeColor(contextMenuFade);
                GUIStyle titleStyle = new GUIStyle(GUI.skin.box);
                titleStyle.fontSize = 13;
                titleStyle.fontStyle = FontStyle.Bold;
                titleStyle.normal.textColor = Color.white;

                GUIStyle btnStyle = new GUIStyle(GUI.skin.button);
                btnStyle.fontSize = 12;
                btnStyle.fontStyle = FontStyle.Bold;

                float menuWidth = 310;
                float menuHeight = 185;
                float startX = (virtualW - menuWidth) * 0.5f;
                float startY = virtualH - menuHeight - 70;
                activeMenuRect = new Rect(startX, startY, menuWidth, menuHeight);

                GUI.Box(activeMenuRect, $"🏢 BÂTIMENT : {selectedBuilding.gameObject.name}", titleStyle);

                // Option 1 : Infiltration / Intérieur
                btnStyle.normal.textColor = new Color(0.3f, 1f, 0.5f);
                if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 28, menuWidth - 20, 34), ProceduralIconFactory.House(), "1. 🏢 INFILTRATION / INTÉRIEUR (RDC)", btnStyle))
                {
                    ConfirmerBuildingAction(1);
                }

                // Option 2 : Monter sur le toit
                btnStyle.normal.textColor = new Color(0.2f, 0.9f, 1f);
                if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 66, menuWidth - 20, 34), ProceduralIconFactory.Ladder(), "2. 🧗 MONTER SUR LE TOIT (Sniper / Guet)", btnStyle))
                {
                    ConfirmerBuildingAction(2);
                }

                // Option 3 : Porte la plus proche
                btnStyle.normal.textColor = Color.yellow;
                if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 104, menuWidth - 20, 34), ProceduralIconFactory.Door(), "3. 🚪 PORTE LA PLUS PROCHE", btnStyle))
                {
                    ConfirmerBuildingAction(3);
                }

                // Annuler
                btnStyle.normal.textColor = Color.gray;
                if (GUI.Button(new Rect(startX + 10, startY + 142, menuWidth - 20, 28), "Annuler", btnStyle))
                {
                    isBuildingSelected = false;
                    selectedBuilding = null;
                    activeMenuRect = Rect.zero;
                }
            }
            // MENU CONTEXTUEL DE PORTE
            else if (uniteSelectionnee != null && isDoorSelected && selectedDoor != null)
            {
                isAnyMenuDrawn = true;
                contextMenuFade = UIAnimator.Advance(contextMenuFade, true, 8f);
                UIAnimator.ApplyFadeColor(contextMenuFade);
                GUIStyle titleStyle = new GUIStyle(GUI.skin.box);
                titleStyle.fontSize = 13;
                titleStyle.fontStyle = FontStyle.Bold;
                titleStyle.normal.textColor = Color.white;

                GUIStyle btnStyle = new GUIStyle(GUI.skin.button);
                btnStyle.fontSize = 12;
                btnStyle.fontStyle = FontStyle.Bold;

                if (isExitDoorAction)
                {
                    float menuWidth = 280;
                    float menuHeight = 180;
                    float startX = (virtualW - menuWidth) * 0.5f;
                    float startY = virtualH - menuHeight - 70;
                    activeMenuRect = new Rect(startX, startY, menuWidth, menuHeight);

                    GUI.Box(activeMenuRect, $"🚪 PORTE : {selectedDoor.building.gameObject.name}", titleStyle);

                    btnStyle.normal.textColor = Color.green;
                    if (GUI.Button(new Rect(startX + 10, startY + 28, menuWidth - 20, 34), "1. 🚪 SORTIR DANS LA RUE", btnStyle))
                    {
                        ConfirmerAction((int)NodeAction.SortirBatiment);
                    }

                    btnStyle.normal.textColor = Color.cyan;
                    if (GUI.Button(new Rect(startX + 10, startY + 66, menuWidth - 20, 34), "2. 👁️ GUETTER PAR LA PORTE", btnStyle))
                    {
                        ConfirmerAction((int)NodeAction.GuetterPorte);
                    }

                    btnStyle.normal.textColor = Color.yellow;
                    if (GUI.Button(new Rect(startX + 10, startY + 104, menuWidth - 20, 34), "3. 📍 CHECKPOINT SIMPLE", btnStyle))
                    {
                        ConfirmerAction((int)NodeAction.Continuer);
                    }

                    btnStyle.normal.textColor = Color.gray;
                    if (GUI.Button(new Rect(startX + 10, startY + 142, menuWidth - 20, 28), "Annuler", btnStyle))
                    {
                        isDoorSelected = false;
                        activeMenuRect = Rect.zero;
                    }
                }
                else
                {
                    float menuWidth = 260;
                    float menuHeight = 105;
                    float startX = (virtualW - menuWidth) * 0.5f;
                    float startY = virtualH - menuHeight - 70;
                    activeMenuRect = new Rect(startX, startY, menuWidth, menuHeight);

                    GUI.Box(activeMenuRect, $"🚪 ENTRÉE : {selectedDoor.building.gameObject.name}", titleStyle);

                    btnStyle.normal.textColor = Color.cyan;
                    if (GUI.Button(new Rect(startX + 10, startY + 28, menuWidth - 20, 34), "🚪 ENTRER DANS LE BÂTIMENT", btnStyle))
                    {
                        ConfirmerAction((int)NodeAction.EntrerBatiment);
                    }

                    btnStyle.normal.textColor = Color.gray;
                    if (GUI.Button(new Rect(startX + 10, startY + 66, menuWidth - 20, 28), "Annuler", btnStyle))
                    {
                        isDoorSelected = false;
                        activeMenuRect = Rect.zero;
                    }
                }
            }
            // MENU CONTEXTUEL DE FENÊTRE
            else if (uniteSelectionnee != null && isWindowSelected && selectedWindow != null)
            {
                isAnyMenuDrawn = true;
                contextMenuFade = UIAnimator.Advance(contextMenuFade, true, 8f);
                UIAnimator.ApplyFadeColor(contextMenuFade);
                GUIStyle titleStyle = new GUIStyle(GUI.skin.box);
                titleStyle.fontSize = 13;
                titleStyle.fontStyle = FontStyle.Bold;
                titleStyle.normal.textColor = Color.white;

                GUIStyle btnStyle = new GUIStyle(GUI.skin.button);
                btnStyle.fontSize = 12;
                btnStyle.fontStyle = FontStyle.Bold;

                float menuWidth = 280;
                float menuHeight = 145;
                float startX = (virtualW - menuWidth) * 0.5f;
                float startY = virtualH - menuHeight - 70;
                activeMenuRect = new Rect(startX, startY, menuWidth, menuHeight);

                GUI.Box(activeMenuRect, $"🛡️ FENÊTRE : {selectedWindow.building.gameObject.name}", titleStyle);

                btnStyle.normal.textColor = new Color(1f, 0.75f, 0.1f);
                if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 28, menuWidth - 20, 34), ProceduralIconFactory.Shield(), "1. 🛡️ GUETTER (Couvert -75%)", btnStyle))
                {
                    ConfirmerAction((int)NodeAction.GarnisonFenetre);
                }

                btnStyle.normal.textColor = Color.yellow;
                if (GUI.Button(new Rect(startX + 10, startY + 66, menuWidth - 20, 34), "2. 📍 CHECKPOINT SIMPLE", btnStyle))
                {
                    ConfirmerAction((int)NodeAction.Continuer);
                }

                btnStyle.normal.textColor = Color.gray;
                if (GUI.Button(new Rect(startX + 10, startY + 104, menuWidth - 20, 28), "Annuler", btnStyle))
                {
                    isWindowSelected = false;
                    activeMenuRect = Rect.zero;
                }
            }
            // MENU TACTIQUE DU CHECKPOINT AU SOL (4 CHOIX)
            else if (uniteSelectionnee != null && isGroundCheckpointSelected)
            {
                isAnyMenuDrawn = true;
                contextMenuFade = UIAnimator.Advance(contextMenuFade, true, 8f);
                UIAnimator.ApplyFadeColor(contextMenuFade);
                UnitAI selectedUnitAI = uniteSelectionnee.GetComponent<UnitAI>();
                bool isMortarUnit = (selectedUnitAI != null && selectedUnitAI.isMortar);

                GUIStyle titleStyle = new GUIStyle(GUI.skin.box);
                titleStyle.fontSize = 13;
                titleStyle.fontStyle = FontStyle.Bold;
                titleStyle.normal.textColor = Color.white;

                GUIStyle btnStyle = new GUIStyle(GUI.skin.button);
                btnStyle.fontSize = 12;
                btnStyle.fontStyle = FontStyle.Bold;

                if (isMortarUnit)
                {
                    float menuWidth = 300;
                    float menuHeight = 175;
                    float startX = (virtualW - menuWidth) * 0.5f;
                    float startY = virtualH - menuHeight - 70;
                    activeMenuRect = new Rect(startX, startY, menuWidth, menuHeight);

                    GUI.Box(activeMenuRect, "💥 ARTILLERIE : ORDRE DE TIR", titleStyle);

                    // Option 1 : Tir de Mortier
                    btnStyle.normal.textColor = new Color(1f, 0.4f, 0.1f);
                    if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 28, menuWidth - 20, 32), ProceduralIconFactory.Mortar(), "1. 🎯 TIR DE MORTIER (Zone AoE)", btnStyle))
                    {
                        ConfirmerAction((int)NodeAction.TirMortier);
                    }

                    // Option 2 : Déplacement
                    btnStyle.normal.textColor = Color.white;
                    if (GUI.Button(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), "2. ▶️ SE DÉPLACER (Position)", btnStyle))
                    {
                        ConfirmerAction((int)NodeAction.Continuer);
                    }

                    // Option 3 : Attendre 30 secondes
                    btnStyle.normal.textColor = Color.yellow;
                    if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), ProceduralIconFactory.Clock(), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
                    {
                        ConfirmerAction((int)NodeAction.Attendre30s);
                    }

                    // Annuler
                    btnStyle.normal.textColor = Color.gray;
                    if (GUI.Button(new Rect(startX + 10, startY + 136, menuWidth - 20, 28), "Annuler", btnStyle))
                    {
                        isGroundCheckpointSelected = false;
                        activeMenuRect = Rect.zero;
                    }
                }
                else if (selectedUnitAI != null && selectedUnitAI.isTank)
                {
                    // Menu Blindé
                    float menuWidth = 290;
                    float menuHeight = 175;
                    float startX = (virtualW - menuWidth) * 0.5f;
                    float startY = virtualH - menuHeight - 70;
                    activeMenuRect = new Rect(startX, startY, menuWidth, menuHeight);

                    GUI.Box(activeMenuRect, "🛡️ BLINDÉ : ORDRE DE MANOEUVRE", titleStyle);

                    // Option 1 : Avancer
                    btnStyle.normal.textColor = Color.white;
                    if (GUI.Button(new Rect(startX + 10, startY + 28, menuWidth - 20, 32), "1. ▶️ AVANCER (Déplacement)", btnStyle))
                    {
                        ConfirmerAction((int)NodeAction.Continuer);
                    }

                    // Option 2 : Guetter Tourelle
                    btnStyle.normal.textColor = Color.cyan;
                    if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), ProceduralIconFactory.Shield(), "2. 🛡️ GUETTER (Surveillance)", btnStyle))
                    {
                        ConfirmerAction((int)NodeAction.Guetter);
                    }

                    // Option 3 : Attendre 30 secondes
                    btnStyle.normal.textColor = Color.yellow;
                    if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), ProceduralIconFactory.Clock(), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
                    {
                        ConfirmerAction((int)NodeAction.Attendre30s);
                    }

                    // Annuler
                    btnStyle.normal.textColor = Color.gray;
                    if (GUI.Button(new Rect(startX + 10, startY + 136, menuWidth - 20, 28), "Annuler", btnStyle))
                    {
                        isGroundCheckpointSelected = false;
                        activeMenuRect = Rect.zero;
                    }
                }
                else
                {
                    // Menu Fantassin
                    UnitAI uAI = uniteSelectionnee.GetComponent<UnitAI>();
                    bool isTargetOnRoof = positionClicTemporaire.y > 1.8f;
                    
                    bool unitIsAlreadyOnRoof = (uAI != null && (uAI.isRooftopSniper || uAI.transform.position.y > 2.0f));
                    if (uAI != null && uAI.tacticalPath.Count > 0)
                    {
                        var lastN = uAI.tacticalPath[uAI.tacticalPath.Count - 1];
                        if (lastN.action == NodeAction.Escalade || lastN.position.y > 2.0f)
                        {
                            unitIsAlreadyOnRoof = true;
                        }
                    }

                    bool unitIsInsideBuilding = (uAI != null && uAI.currentBuilding != null && !uAI.isRooftopSniper);
                    if (uAI != null && uAI.tacticalPath.Count > 0)
                    {
                        var lastN = uAI.tacticalPath[uAI.tacticalPath.Count - 1];
                        if (lastN.action == NodeAction.EntrerBatiment) unitIsInsideBuilding = true;
                        else if (lastN.action == NodeAction.SortirBatiment) unitIsInsideBuilding = false;
                    }

                    float menuWidth = 300;
                    float menuHeight = (isNearBuildingWall && !isTargetOnRoof && !unitIsAlreadyOnRoof && !unitIsInsideBuilding) ? 215 : 175;
                    float startX = (virtualW - menuWidth) * 0.5f;
                    float startY = virtualH - menuHeight - 70;
                    activeMenuRect = new Rect(startX, startY, menuWidth, menuHeight);

                    if (isTargetOnRoof)
                    {
                        if (unitIsAlreadyOnRoof)
                        {
                            GUI.Box(activeMenuRect, "🏃 INFANTERIE : DÉPLACEMENT TOIT", titleStyle);

                            btnStyle.normal.textColor = Color.white;
                            if (GUI.Button(new Rect(startX + 10, startY + 28, menuWidth - 20, 32), "1. ▶️ CONTINUER SUR LE TOIT", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Continuer);
                            }

                            btnStyle.normal.textColor = Color.cyan;
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), ProceduralIconFactory.Shield(), "2. 🛡️ GUETTER SUR LE TOIT", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Guetter);
                            }

                            btnStyle.normal.textColor = Color.yellow;
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), ProceduralIconFactory.Clock(), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Attendre30s);
                            }

                            btnStyle.normal.textColor = Color.gray;
                            if (GUI.Button(new Rect(startX + 10, startY + 136, menuWidth - 20, 28), "Annuler", btnStyle))
                            {
                                isGroundCheckpointSelected = false;
                                activeMenuRect = Rect.zero;
                            }
                        }
                        else
                        {
                            GUI.Box(activeMenuRect, "🧗 INFANTERIE : ESCALADE DE FAÇADE", titleStyle);

                            btnStyle.normal.textColor = new Color(0.2f, 0.9f, 0.4f);
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 28, menuWidth - 20, 32), ProceduralIconFactory.Ladder(), "1. 🧗 ESCALADER SUR LE TOIT", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Escalade);
                            }

                            btnStyle.normal.textColor = Color.cyan;
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), ProceduralIconFactory.Shield(), "2. 🛡️ GUETTER (+50% Défense)", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Guetter);
                            }

                            btnStyle.normal.textColor = Color.yellow;
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), ProceduralIconFactory.Clock(), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Attendre30s);
                            }

                            btnStyle.normal.textColor = Color.gray;
                            if (GUI.Button(new Rect(startX + 10, startY + 136, menuWidth - 20, 28), "Annuler", btnStyle))
                            {
                                isGroundCheckpointSelected = false;
                                activeMenuRect = Rect.zero;
                            }
                        }
                    }
                    else
                    {
                        if (unitIsAlreadyOnRoof)
                        {
                            GUI.Box(activeMenuRect, "🧗 INFANTERIE : DESCENTE VERS RUE", titleStyle);

                            btnStyle.normal.textColor = new Color(1f, 0.6f, 0.2f);
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 28, menuWidth - 20, 32), ProceduralIconFactory.Ladder(), "1. 🧗 DESCENDRE DU TOIT", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Escalade);
                            }

                            btnStyle.normal.textColor = Color.cyan;
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), ProceduralIconFactory.Shield(), "2. 🛡️ GUETTER (+50% Défense)", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Guetter);
                            }

                            btnStyle.normal.textColor = Color.yellow;
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), ProceduralIconFactory.Clock(), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Attendre30s);
                            }

                            btnStyle.normal.textColor = Color.gray;
                            if (GUI.Button(new Rect(startX + 10, startY + 136, menuWidth - 20, 28), "Annuler", btnStyle))
                            {
                                isGroundCheckpointSelected = false;
                                activeMenuRect = Rect.zero;
                            }
                        }
                        else if (unitIsInsideBuilding)
                        {
                            GUI.Box(activeMenuRect, "🏢 INFANTERIE : DÉPLACEMENT INTÉRIEUR", titleStyle);

                            btnStyle.normal.textColor = Color.white;
                            if (GUI.Button(new Rect(startX + 10, startY + 28, menuWidth - 20, 32), "1. ▶️ SE DÉPLACER À L'INTÉRIEUR", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Continuer);
                            }

                            btnStyle.normal.textColor = Color.cyan;
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), ProceduralIconFactory.Shield(), "2. 🛡️ GUETTER INTÉRIEUR", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Guetter);
                            }

                            btnStyle.normal.textColor = Color.yellow;
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), ProceduralIconFactory.Clock(), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Attendre30s);
                            }

                            btnStyle.normal.textColor = Color.gray;
                            if (GUI.Button(new Rect(startX + 10, startY + 136, menuWidth - 20, 28), "Annuler", btnStyle))
                            {
                                isGroundCheckpointSelected = false;
                                activeMenuRect = Rect.zero;
                            }
                        }
                        else
                        {
                            GUI.Box(activeMenuRect, "🎖️ INFANTERIE : ORDRE TACTIQUE", titleStyle);

                            btnStyle.normal.textColor = Color.white;
                            if (GUI.Button(new Rect(startX + 10, startY + 28, menuWidth - 20, 32), "1. ▶️ CONTINUER (Mouvement)", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Continuer);
                            }

                            btnStyle.normal.textColor = Color.cyan;
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), ProceduralIconFactory.Shield(), "2. 🛡️ GUETTER (+50% Défense)", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Guetter);
                            }

                            btnStyle.normal.textColor = Color.yellow;
                            if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), ProceduralIconFactory.Clock(), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Attendre30s);
                            }

                            if (isNearBuildingWall)
                            {
                                btnStyle.normal.textColor = Color.green;
                                if (ProceduralIconFactory.IconButton(new Rect(startX + 10, startY + 136, menuWidth - 20, 32), ProceduralIconFactory.Eye(), "4. 🥷 SE CACHER (Contre mur)", btnStyle))
                                {
                                    ConfirmerAction((int)NodeAction.SeCacher);
                                }
                            }

                            btnStyle.normal.textColor = Color.gray;
                            float cancelY = (isNearBuildingWall) ? startY + 172 : startY + 136;
                            if (GUI.Button(new Rect(startX + 10, cancelY, menuWidth - 20, 28), "Annuler", btnStyle))
                            {
                                isGroundCheckpointSelected = false;
                                activeMenuRect = Rect.zero;
                            }
                        }
                    }
                }
            }

            GUI.color = Color.white; // Toujours restaurer après le fondu des menus contextuels ci-dessus

            if (!isAnyMenuDrawn)
            {
                activeMenuRect = Rect.zero;
                contextMenuFade = UIAnimator.Advance(contextMenuFade, false, 8f);
            }
        }
        else
        {
            GUIStyle style = new GUIStyle(GUI.skin.box);
            style.fontSize = 14;
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = Color.yellow;
            GUI.Box(new Rect(virtualW / 2 - 140, 15, 280, 42), "ACTION EN COURS...", style);

            if (GUI.Button(new Rect(virtualW - 130, 15, 115, 42), "Passer"))
            {
                ForcerFinExecution();
            }
        }
    }
}