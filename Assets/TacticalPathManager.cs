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
    private StreetAct.Interaction.DoorInteraction lastHoveredDoor = null;
    private StreetAct.Interaction.WindowInteraction selectedWindow = null;
    private bool isDoorSelected = false;
    private bool isWindowSelected = false;
    private bool isExitDoorAction = false;
    private bool isGroundCheckpointSelected = false;
    private bool isNearBuildingWall = false;
    private Rect activeMenuRect = Rect.zero;

    private Color couleurOriginale;
    // Pour stocker TOUTES les parties du soldat (corps, arme, etc.)
    private Renderer[] renderersSelectionnes;
    private LineRenderer lineRenderer;
    private readonly List<Vector3> cachedLinePoints = new List<Vector3>();
    private UnityEngine.AI.NavMeshPath cachedNavPath;
    private Gradient cachedPathGradient;

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

        // Gestion de l'affichage simultané des trajectoires de toutes les unités
        DessinerTousLesChemins();

        // 1. Si le menu est ouvert ou qu'on interagit avec l'UI, on bloque le reste
        if (menuPanel != null && menuPanel.activeSelf || EventSystem.current.IsPointerOverGameObject()) return;

        Vector2 pointerPosition = (Pointer.current != null) ? Pointer.current.position.ReadValue() : Vector2.zero;
        Vector2 guiMousePos = new Vector2(pointerPosition.x, Screen.height - pointerPosition.y);
        Rect endTurnRect = new Rect(Screen.width - 220, Screen.height - 80, 200, 60);

        // Clic Droit : Annulation rapide du menu ou du dernier checkpoint
        if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
        {
            if (isDoorSelected || isWindowSelected || isGroundCheckpointSelected)
            {
                isDoorSelected = false;
                isWindowSelected = false;
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

        // --- SURVOL TACTIQUE DES PORTES ET FENÊTRES (HOVER HIGHLIGHT) ---
        if (phaseActuelle == GamePhase.Planification && uniteSelectionnee != null && !EventSystem.current.IsPointerOverGameObject())
        {
            Ray hoverRay = Camera.main.ScreenPointToRay(pointerPosition);
            if (Physics.Raycast(hoverRay, out RaycastHit hoverHit, 500f))
            {
                StreetAct.Interaction.DoorInteraction hoveredDoor = hoverHit.collider.GetComponent<StreetAct.Interaction.DoorInteraction>()
                                                                  ?? hoverHit.collider.GetComponentInParent<StreetAct.Interaction.DoorInteraction>();
                
                if (hoveredDoor == null)
                {
                    BuildingStructure bStruct = hoverHit.collider.GetComponentInParent<BuildingStructure>();
                    if (bStruct != null && hoverHit.point.y < 3.2f)
                    {
                        hoveredDoor = bStruct.GetClosestDoorInteraction(hoverHit.point, 3.5f);
                    }
                }

                if (lastHoveredDoor != hoveredDoor)
                {
                    if (lastHoveredDoor != null) lastHoveredDoor.SetHighlight(false);
                    if (hoveredDoor != null) hoveredDoor.SetHighlight(true);
                    lastHoveredDoor = hoveredDoor;
                }
            }
            else if (lastHoveredDoor != null)
            {
                lastHoveredDoor.SetHighlight(false);
                lastHoveredDoor = null;
            }
        }

        // --- Clic Gauche ou Touch : Sélection ou Action ---
        if (Pointer.current != null && Pointer.current.press.wasPressedThisFrame)
        {
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) isActionClick = true;
            else if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame) isActionClick = true;
            else if (Mouse.current == null && Touchscreen.current == null) isActionClick = true;
        }

        if (isActionClick)
        {
            // Vérifier si la souris est sur l'UI (Menu actif ou bouton Fin de Tour) -> bloquer le raycast !
            if (EventSystem.current.IsPointerOverGameObject()) return;
            if (activeMenuRect != Rect.zero && activeMenuRect.Contains(guiMousePos)) return;
            if (endTurnRect.Contains(guiMousePos)) return;

            Ray ray = Camera.main.ScreenPointToRay(pointerPosition);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit))
            {
                // 1. Détection 3D précise des unités (résolution de conflit Toit vs Intérieur)
                UnitAI closestUnit = null;
                float closestProj = float.MaxValue;

                // Test direct sur le Collider de l'unité
                UnitAI hitUnit = hit.collider.GetComponentInParent<UnitAI>();
                if (hitUnit != null && hitUnit.isPlayerControlled && !hitUnit.isDead)
                {
                    closestUnit = hitUnit;
                }
                else
                {
                    for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
                    {
                        UnitAI unit = UnitAI.AllLivingUnits[i];
                        if (unit != null && unit.isPlayerControlled && !unit.isDead)
                        {
                            Vector3 unitCenter = unit.transform.position + Vector3.up * 1.0f;
                            Vector3 toUnit = unitCenter - ray.origin;
                            float proj = Vector3.Dot(toUnit, ray.direction);
                            
                            if (proj > 0)
                            {
                                Vector3 pointOnRay = ray.origin + ray.direction * proj;
                                float distToRay = Vector3.Distance(pointOnRay, unitCenter);
                                if (distToRay < 1.8f)
                                {
                                    // Donne la priorité absolue à l'unité la plus proche de la caméra (au premier plan / sur le toit)
                                    if (proj < closestProj)
                                    {
                                        closestProj = proj;
                                        closestUnit = unit;
                                    }
                                }
                            }
                        }
                    }
                }

                if (closestUnit != null)
                {
                    if (menuPanel != null) menuPanel.SetActive(false);
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = false;
                    activeMenuRect = Rect.zero;
                    phaseActuelle = GamePhase.Planification;
                    SelectionnerUnite(closestUnit.gameObject);
                }
                else if (phaseActuelle == GamePhase.Planification && uniteSelectionnee != null)
                {
                    UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();

                    // 0. Si l'unité est une unité de Mortier / Artillerie, tout clic (bâtiment, toit, sol) est une coordonnée de bombardement ciblable
                    if (unitAI != null && unitAI.isMortar)
                    {
                        positionClicTemporaire = hit.point;
                        isDoorSelected = false;
                        isWindowSelected = false;
                        isGroundCheckpointSelected = true;

                        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                        if (menuPanel != null) menuPanel.SetActive(false);
                        return;
                    }
                    
                    // 2. Détection du clic sur une porte d'entrée / sortie
                    StreetAct.Interaction.DoorInteraction clickedDoor = hit.collider.GetComponent<StreetAct.Interaction.DoorInteraction>() 
                                                                      ?? hit.collider.GetComponentInParent<StreetAct.Interaction.DoorInteraction>();

                    if (clickedDoor == null)
                    {
                        BuildingStructure bStruct = hit.collider.GetComponentInParent<BuildingStructure>();
                        if (bStruct != null && hit.point.y < 3.2f)
                        {
                            clickedDoor = bStruct.GetClosestDoorInteraction(hit.point, 3.5f);
                        }
                    }

                    if (clickedDoor != null)
                    {
                        if (unitAI != null && unitAI.isTank)
                        {
                            Debug.LogWarning("[TacticalPathManager] Les véhicules blindés ne peuvent pas entrer dans les bâtiments !");
                            AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                            if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
                            return;
                        }

                        selectedDoor = clickedDoor;
                        selectedWindow = null;
                        isDoorSelected = true;
                        isWindowSelected = false;
                        clickedDoor.SetHighlight(true);

                        // Ouvrir immédiatement le toit pour visualiser l'intérieur
                        if (clickedDoor.building != null && clickedDoor.building.tacticalVisibility != null)
                        {
                            clickedDoor.building.tacticalVisibility.SetPlanificationPreview(true);
                        }

                        // Si le soldat est déjà à l'intérieur, options Sortir / Guetter / Checkpoint
                        if (unitAI != null && unitAI.currentBuilding == clickedDoor.building)
                        {
                            isExitDoorAction = true;
                            positionClicTemporaire = clickedDoor.GetOutsidePosition();
                            Debug.Log($"<color=cyan>[TacticalPathManager] Porte ciblée depuis l'intérieur : {clickedDoor.building.gameObject.name} !</color>");
                        }
                        else
                        {
                            isExitDoorAction = false;
                            positionClicTemporaire = clickedDoor.doorData.position;
                            Debug.Log($"<color=cyan>[TacticalPathManager] Porte ciblée depuis la rue : {clickedDoor.building.gameObject.name} !</color>");
                        }

                        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);

                        if (menuPanel != null) menuPanel.SetActive(false);
                        return;
                    }

                    // 3. Détection du clic sur une fenêtre (depuis l'intérieur ou l'extérieur)
                    StreetAct.Interaction.WindowInteraction clickedWindow = hit.collider.GetComponent<StreetAct.Interaction.WindowInteraction>() 
                                                                          ?? hit.collider.GetComponentInParent<StreetAct.Interaction.WindowInteraction>();

                    if (clickedWindow != null)
                    {
                        if (unitAI != null && unitAI.isTank)
                        {
                            Debug.LogWarning("[TacticalPathManager] Les chars et véhicules ne peuvent pas utiliser les fenêtres !");
                            AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                            if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
                            return;
                        }

                        selectedWindow = clickedWindow;
                        selectedDoor = null;
                        isWindowSelected = true;
                        isDoorSelected = false;
                        positionClicTemporaire = clickedWindow.GetInteriorStancePosition();

                        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                        Debug.Log($"<color=orange>[TacticalPathManager] Fenêtre ciblée : Prêt pour le tir et couverture dans {clickedWindow.building.gameObject.name} !</color>");

                        if (menuPanel != null) menuPanel.SetActive(false);
                        return;
                    }

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
                                isDoorSelected = false;
                                isWindowSelected = false;
                                if (menuPanel != null)
                                {
                                    StopAllCoroutines();
                                    StartCoroutine(AnimateMenuBounce());
                                }
                                return;
                            }
                        }
                    }

                    bool isUnitPlanningInside = (unitAI != null && (unitAI.currentBuilding != null || (unitAI.tacticalPath.Count > 0 && unitAI.tacticalPath[unitAI.tacticalPath.Count - 1].action == NodeAction.EntrerBatiment)));
                    bool isSelectedOnRoof = (unitAI != null && (unitAI.isRooftopSniper || unitAI.transform.position.y > 2.2f));

                    bool isClickOnRoof = hit.point.y > 1.8f;
                    if (isUnitPlanningInside && !isSelectedOnRoof)
                    {
                        isClickOnRoof = false; // Clic au sol à l'intérieur du bâtiment
                    }

                    // Si le soldat est déjà sur le toit et clique sur un bâtiment dont le toit est transparent
                    if (isSelectedOnRoof && !isClickOnRoof)
                    {
                        BuildingStructure hitBldg = hit.collider.GetComponentInParent<BuildingStructure>();
                        if (hitBldg != null)
                        {
                            Collider bCol = hitBldg.GetComponent<Collider>();
                            if (bCol != null && bCol.bounds.size.y > 3.5f)
                            {
                                isClickOnRoof = true;
                                hit.point = new Vector3(hit.point.x, bCol.bounds.max.y, hit.point.z);
                            }
                        }
                    }

                    bool isRubblePoint = DestructibleEnvironment.IsPositionInRubble(hit.point);
                    if (isRubblePoint) isClickOnRoof = false; // Les ruines sont au ras du sol

                    // Restriction : Les véhicules blindés ne peuvent pas monter sur les toits intacts
                    if (isClickOnRoof && !isRubblePoint && unitAI != null && unitAI.isTank)
                    {
                        Debug.LogWarning("[TacticalPathManager] Les chars et véhicules ne peuvent pas monter sur les toits !");
                        AudioClip errClip = ProceduralAudioBuilder.CreateErrorSound();
                        if (errClip != null) AudioSource.PlayClipAtPoint(errClip, Camera.main.transform.position);
                        return;
                    }

                    // Accepter le point s'il est sur le NavMesh, sur un toit, à l'intérieur ou sur des ruines
                    UnityEngine.AI.NavMeshHit navHit;
                    bool hasNavMesh = UnityEngine.AI.NavMesh.SamplePosition(hit.point, out navHit, 4.5f, UnityEngine.AI.NavMesh.AllAreas);

                    if (hasNavMesh || isClickOnRoof || isUnitPlanningInside || isRubblePoint)
                    {
                        if (isClickOnRoof && !isRubblePoint) positionClicTemporaire = hit.point;
                        else if (isUnitPlanningInside || isRubblePoint) positionClicTemporaire = new Vector3(hit.point.x, 0.05f, hit.point.z);
                        else positionClicTemporaire = navHit.position;

                        isDoorSelected = false;
                        isWindowSelected = false;
                        isGroundCheckpointSelected = true;

                        // Vérification si le soldat est collé à un mur (< 2.2m d'un bâtiment)
                        isNearBuildingWall = false;
                        if (!isClickOnRoof && !isUnitPlanningInside)
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
        if (phaseActuelle != GamePhase.Planification && phaseActuelle != GamePhase.CreationPath)
        {
            for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
            {
                UnitAI u = UnitAI.AllLivingUnits[i];
                if (u != null && u.tacticalLineRenderer != null) u.tacticalLineRenderer.positionCount = 0;
            }
            if (lineRenderer != null) lineRenderer.positionCount = 0;
            return;
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
                unitAI.tacticalLineRenderer = lineGo.AddComponent<LineRenderer>();
                
                Shader sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
                unitAI.tacticalLineRenderer.material = new Material(sh);
                unitAI.tacticalLineRenderer.material.enableInstancing = true;
            }

            LineRenderer lr = unitAI.tacticalLineRenderer;
            lr.startWidth = isSelected ? 0.18f : 0.11f;
            lr.endWidth = isSelected ? 0.08f : 0.04f;

            Gradient grad = new Gradient();
            if (isSelected)
            {
                grad.SetKeys(
                    new GradientColorKey[] { new GradientColorKey(new Color(0f, 0.95f, 1f), 0.0f), new GradientColorKey(new Color(0f, 0.45f, 1f), 1.0f) },
                    new GradientAlphaKey[] { new GradientAlphaKey(1.0f, 0.0f), new GradientAlphaKey(0.85f, 1.0f) }
                );
            }
            else
            {
                grad.SetKeys(
                    new GradientColorKey[] { new GradientColorKey(new Color(0.2f, 0.7f, 1f), 0.0f), new GradientColorKey(new Color(0.1f, 0.35f, 0.8f), 1.0f) },
                    new GradientAlphaKey[] { new GradientAlphaKey(0.65f, 0.0f), new GradientAlphaKey(0.40f, 1.0f) }
                );
            }
            lr.colorGradient = grad;

            cachedLinePoints.Clear();
            Vector3 positionCourante = unitAI.transform.position;
            cachedLinePoints.Add(positionCourante + Vector3.up * 0.2f);

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
                    cachedLinePoints.Add(targetPos + Vector3.up * 0.2f);
                    positionCourante = targetPos;
                }
                else if (UnityEngine.AI.NavMesh.CalculatePath(positionCourante, targetPos, areaMask, cachedNavPath) && cachedNavPath.corners.Length > 1)
                {
                    for (int j = 1; j < cachedNavPath.corners.Length; j++)
                    {
                        cachedLinePoints.Add(cachedNavPath.corners[j] + Vector3.up * 0.2f);
                    }
                    positionCourante = cachedNavPath.corners[cachedNavPath.corners.Length - 1];
                }
                else
                {
                    cachedLinePoints.Add(targetPos + Vector3.up * 0.2f);
                    positionCourante = targetPos;
                }
            }

            // Prévisualisation pour l'unité sélectionnée vers la position du curseur
            if (isSelected && phaseActuelle == GamePhase.CreationPath && positionClicTemporaire != Vector3.zero)
            {
                if (UnityEngine.AI.NavMesh.CalculatePath(positionCourante, positionClicTemporaire, areaMask, cachedNavPath) && cachedNavPath.corners.Length > 1)
                {
                    for (int j = 1; j < cachedNavPath.corners.Length; j++)
                    {
                        cachedLinePoints.Add(cachedNavPath.corners[j] + Vector3.up * 0.2f);
                    }
                }
                else
                {
                    cachedLinePoints.Add(positionClicTemporaire + Vector3.up * 0.2f);
                }
            }

            lr.positionCount = cachedLinePoints.Count;
            for (int p = 0; p < cachedLinePoints.Count; p++)
            {
                lr.SetPosition(p, cachedLinePoints[p]);
            }
        }
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
        isGroundCheckpointSelected = false;
        activeMenuRect = Rect.zero;

        if (menuPanel != null) menuPanel.SetActive(false);
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
            if (GUI.Button(new Rect(virtualW - 155, virtualH - 62, 145, 52), "▶️ FIN TOUR\n(Lancer)", endTurnBtnStyle))
            {
                LancerExecutionTour();
            }

            // Boutons tactiles contextuels quand une unité est sélectionnée (Bas Gauche)
            if (uniteSelectionnee != null)
            {
                GUIStyle touchBtnStyle = new GUIStyle(GUI.skin.button);
                touchBtnStyle.fontSize = 11;
                touchBtnStyle.fontStyle = FontStyle.Bold;

                // 1. Bouton tactile pour désélectionner (Touch Mobile / Tablette)
                touchBtnStyle.normal.textColor = new Color(1f, 0.45f, 0.45f);
                if (GUI.Button(new Rect(12, virtualH - 62, 105, 52), "❌ DÉSÉLECT.\n(Retour)", touchBtnStyle))
                {
                    SelectionnerUnite(null);
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = false;
                    activeMenuRect = Rect.zero;
                }

                // 2. Bouton tactile pour annuler le dernier point
                touchBtnStyle.normal.textColor = new Color(1f, 0.85f, 0.2f);
                if (GUI.Button(new Rect(122, virtualH - 62, 105, 52), "↩️ ANNULER\n(Point)", touchBtnStyle))
                {
                    UnitAI uAI = uniteSelectionnee.GetComponent<UnitAI>();
                    if (uAI != null && uAI.tacticalPath.Count > 0)
                    {
                        uAI.RemoveLastTacticalNode();
                        DessinerTousLesChemins();
                        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    }
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = false;
                    activeMenuRect = Rect.zero;
                }

                // 3. Bouton tactile pour effacer toute la trajectoire
                touchBtnStyle.normal.textColor = new Color(1f, 0.4f, 0.3f);
                if (GUI.Button(new Rect(232, virtualH - 62, 105, 52), "🔄 EFFACER\n(Trajet)", touchBtnStyle))
                {
                    UnitAI uAI = uniteSelectionnee.GetComponent<UnitAI>();
                    if (uAI != null)
                    {
                        uAI.ClearTacticalPath();
                        DessinerTousLesChemins();
                        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateErrorSound(), Camera.main.transform.position);
                    }
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isGroundCheckpointSelected = false;
                    activeMenuRect = Rect.zero;
                }
            }

            bool isAnyMenuDrawn = false;

            // MENU CONTEXTUEL DE PORTE
            if (uniteSelectionnee != null && isDoorSelected && selectedDoor != null)
            {
                isAnyMenuDrawn = true;
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
                if (GUI.Button(new Rect(startX + 10, startY + 28, menuWidth - 20, 34), "1. 🛡️ GUETTER (Couvert -75%)", btnStyle))
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
                    if (GUI.Button(new Rect(startX + 10, startY + 28, menuWidth - 20, 32), "1. 🎯 TIR DE MORTIER (Zone AoE)", btnStyle))
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
                    if (GUI.Button(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
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
                    if (GUI.Button(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), "2. 🛡️ GUETTER (Surveillance)", btnStyle))
                    {
                        ConfirmerAction((int)NodeAction.Guetter);
                    }

                    // Option 3 : Attendre 30 secondes
                    btnStyle.normal.textColor = Color.yellow;
                    if (GUI.Button(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
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
                            if (GUI.Button(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), "2. 🛡️ GUETTER SUR LE TOIT", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Guetter);
                            }

                            btnStyle.normal.textColor = Color.yellow;
                            if (GUI.Button(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
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
                            if (GUI.Button(new Rect(startX + 10, startY + 28, menuWidth - 20, 32), "1. 🧗 ESCALADER SUR LE TOIT", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Escalade);
                            }

                            btnStyle.normal.textColor = Color.cyan;
                            if (GUI.Button(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), "2. 🛡️ GUETTER (+50% Défense)", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Guetter);
                            }

                            btnStyle.normal.textColor = Color.yellow;
                            if (GUI.Button(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
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
                            if (GUI.Button(new Rect(startX + 10, startY + 28, menuWidth - 20, 32), "1. 🧗 DESCENDRE DU TOIT", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Escalade);
                            }

                            btnStyle.normal.textColor = Color.cyan;
                            if (GUI.Button(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), "2. 🛡️ GUETTER (+50% Défense)", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Guetter);
                            }

                            btnStyle.normal.textColor = Color.yellow;
                            if (GUI.Button(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
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
                            if (GUI.Button(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), "2. 🛡️ GUETTER INTÉRIEUR", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Guetter);
                            }

                            btnStyle.normal.textColor = Color.yellow;
                            if (GUI.Button(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
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
                            if (GUI.Button(new Rect(startX + 10, startY + 64, menuWidth - 20, 32), "2. 🛡️ GUETTER (+50% Défense)", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Guetter);
                            }

                            btnStyle.normal.textColor = Color.yellow;
                            if (GUI.Button(new Rect(startX + 10, startY + 100, menuWidth - 20, 32), "3. ⏳ ATTENDRE 30 SECONDES", btnStyle))
                            {
                                ConfirmerAction((int)NodeAction.Attendre30s);
                            }

                            if (isNearBuildingWall)
                            {
                                btnStyle.normal.textColor = Color.green;
                                if (GUI.Button(new Rect(startX + 10, startY + 136, menuWidth - 20, 32), "4. 🥷 SE CACHER (Contre mur)", btnStyle))
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

            if (!isAnyMenuDrawn)
            {
                activeMenuRect = Rect.zero;
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