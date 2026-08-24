using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem; // Importation du nouveau système
using UnityEngine.UIElements;

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
    private Novgov.Interaction.DoorInteraction selectedDoor = null;
    private Novgov.Interaction.WindowInteraction selectedWindow = null;
    private BuildingStructure selectedBuilding = null;
    private bool isBuildingSelected = false;
    private bool isDoorSelected = false;
    private bool isWindowSelected = false;
    private bool isExitDoorAction = false;
    private bool isGroundCheckpointSelected = false;
    private bool isNearBuildingWall = false;

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
#if !UNITY_SERVER
        BindTacticalUI();
#endif
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
            lineRenderer.material = SafeMaterialFactory.CreateUnlit(Color.cyan);
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
#if UNITY_SERVER
        return; // Aucune entrée tactile/souris à traiter côté serveur headless.
#else
        RefreshTacticalUI();
#endif
        // Pulsation "battement de cœur" des lignes de trajectoire : ne touche qu'un multiplicateur
        // de largeur (aucun recalcul de chemin/positions), donc s'exécute chaque frame — y compris
        // pendant l'Exécution où le tracé restant reste affiché — sans réintroduire le coût que
        // isPathDirty a justement été ajouté pour éliminer.
        AnimateTacticalLinesHeartbeat();

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
        else if (Mouse.current != null)
        {
            pointerPosition = Mouse.current.position.ReadValue();
            isPointerActive = true;
            wasPressed = Mouse.current.leftButton.wasPressedThisFrame;
            wasReleased = Mouse.current.leftButton.wasReleasedThisFrame;
        }
        else if (Pointer.current != null)
        {
            pointerPosition = Pointer.current.position.ReadValue();
            isPointerActive = true;
            wasPressed = Pointer.current.press.wasPressedThisFrame;
            wasReleased = Pointer.current.press.wasReleasedThisFrame;
        }

        // Clic Droit : Annulation rapide du menu ou du dernier checkpoint
        if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
        {
            if (isDoorSelected || isWindowSelected || isBuildingSelected || isGroundCheckpointSelected)
            {
                isDoorSelected = false;
                isWindowSelected = false;
                isBuildingSelected = false;
                isGroundCheckpointSelected = false;
                selectedBuilding = null;
                currentMenuCancelAction?.Invoke();
                currentMenuCancelAction = null;
                UIScreenManager.Instance.SetVisible("ContextMenu", false);
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                return;
            }
            else if (uniteSelectionnee != null)
            {
                UnitAI unitAI = uniteSelectionnee.GetComponent<UnitAI>();
                if (unitAI != null && unitAI.tacticalPath.Count > 0)
                {
                    unitAI.RemoveLastTacticalNode();
                    DessinerTousLesChemins();
                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
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
                isActionClick = true;
            }
            else if (wasReleased && isPointerDown)
            {
                isPointerDown = false;
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

            // Vérifier si le clic est sur un bouton de l'interface — un seul test générique
            // (UI Toolkit picking) couvre désormais la barre du bas, les menus contextuels et
            // le dock de déploiement, plus besoin de Rect codées en dur par écran.
            if (UnitSpawnerUI.Instance != null && UnitSpawnerUI.Instance.IsPointerOverOnGUI(pointerPosition)) return;

            // 1. Détection tolérante des unités en espace écran + Raycast 3D direct
            UnitAI closestUnit = null;
            float maxTouchRadiusPx = 75f;
            float closestScreenDist = maxTouchRadiusPx;

            Ray ray = Camera.main.ScreenPointToRay(pointerPosition);
            RaycastHit hit;
            Vector3 hitPoint = Vector3.zero;
            bool hasHit = false;

            if (Physics.Raycast(ray, out hit))
            {
                hitPoint = hit.point;
                hasHit = true;
                
                UnitAI hitUnit = hit.collider.GetComponent<UnitAI>() ?? hit.collider.GetComponentInParent<UnitAI>();
                if (hitUnit != null && hitUnit.isPlayerControlled && !hitUnit.isDead)
                {
                    closestUnit = hitUnit;
                }
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

            if (closestUnit == null)
            {
                for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
                {
                    UnitAI unit = UnitAI.AllLivingUnits[i];
                    if (unit != null && unit.isPlayerControlled && !unit.isDead)
                    {
                        Vector3 screenPoint = Camera.main.WorldToScreenPoint(unit.transform.position + Vector3.up * 0.5f);
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
            }

            if (closestUnit != null)
            {
                if (menuPanel != null) menuPanel.SetActive(false);
                isDoorSelected = false;
                isWindowSelected = false;
                isBuildingSelected = false;
                isGroundCheckpointSelected = false;
                phaseActuelle = GamePhase.Planification;
                SelectionnerUnite(closestUnit.gameObject);
                return;
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
#if !UNITY_SERVER
                    ShowGroundCheckpointMenu();
#endif
                    return;
                }

                // 1. Détection clic sur porte
                Novgov.Interaction.DoorInteraction clickedDoor = null;
                if (hit.collider != null)
                {
                    clickedDoor = hit.collider.GetComponent<Novgov.Interaction.DoorInteraction>() 
                               ?? hit.collider.GetComponentInParent<Novgov.Interaction.DoorInteraction>();
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
#if !UNITY_SERVER
                    ShowDoorMenu();
#endif
                    return;
                }

                // 2. Détection clic sur fenêtre
                Novgov.Interaction.WindowInteraction clickedWindow = null;
                if (hit.collider != null)
                {
                    clickedWindow = hit.collider.GetComponent<Novgov.Interaction.WindowInteraction>() 
                                 ?? hit.collider.GetComponentInParent<Novgov.Interaction.WindowInteraction>();
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
#if !UNITY_SERVER
                    ShowWindowMenu();
#endif
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
#if !UNITY_SERVER
                    ShowBuildingMenu();
#endif
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
#if !UNITY_SERVER
                    ShowGroundCheckpointMenu();
#endif
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
                PlayUnitVoiceLine(unitAI, isSelection: true);
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

    /// <summary>Cloche de notification (haut-droit) : sélectionne la prochaine unité alliée
    /// vivante en dessous de 50% de vie, pour que le joueur puisse réagir sans devoir la
    /// repérer visuellement sur la carte. Ne fait rien si aucune unité n'est en difficulté.</summary>
    private void SelectionnerProchaineUniteBlessee()
    {
        UnitAI candidate = null;
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u == null || u.isDead || !u.isPlayerControlled) continue;
            if (u.gameObject == uniteSelectionnee) continue; // déjà sélectionnée, passer à la suivante
            float pct = u.maxHealth > 0 ? (float)u.health / u.maxHealth : 1f;
            if (pct < 0.5f) { candidate = u; break; }
        }
        if (candidate != null)
        {
            phaseActuelle = GamePhase.Planification;
            SelectionnerUnite(candidate.gameObject);
        }
    }

    /// <summary>Nombre d'unités alliées vivantes en dessous de 50% de vie — affiché en pastille
    /// sur la cloche de notification.</summary>
    private int CountWoundedPlayerUnits()
    {
        int count = 0;
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u == null || u.isDead || !u.isPlayerControlled) continue;
            float pct = u.maxHealth > 0 ? (float)u.health / u.maxHealth : 1f;
            if (pct < 0.5f) count++;
        }
        return count;
    }

    // Fréquence et amplitude du "battement de cœur" des lignes de trajectoire.
    private const float HEARTBEAT_FREQUENCY = 5.24f; // ~1,2s par battement
    private const float HEARTBEAT_AMPLITUDE = 0.35f;

    private void AnimateTacticalLinesHeartbeat()
    {
        // Pic bref suivi d'un repos (sin élevé à une puissance impaire) plutôt qu'une simple
        // respiration sinusoïdale continue : ça se lit comme un pouls, pas comme un néon qui clignote.
        float pulse = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(Time.time * HEARTBEAT_FREQUENCY)), 4f);
        float widthMultiplier = 1f + pulse * HEARTBEAT_AMPLITUDE;

        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI unitAI = UnitAI.AllLivingUnits[i];
            if (unitAI == null || unitAI.tacticalLineRenderer == null) continue;
            if (unitAI.tacticalLineRenderer.positionCount == 0) continue;

            unitAI.tacticalLineRenderer.widthMultiplier = widthMultiplier;
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
            lr.startWidth = isSelected ? 0.42f : 0.22f;
            lr.endWidth = isSelected ? 0.20f : 0.10f;
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

    /// <summary>
    /// Petites répliques radio pour donner du caractère aux ordres (voir Assets/Resources/Sounds/)
    /// — une paire sélection/accusé de réception pour l'infanterie, une autre pour le reste
    /// (blindés, véhicules canon, mortiers).
    /// </summary>
    private void PlayUnitVoiceLine(UnitAI unitAI, bool isSelection)
    {
        if (unitAI == null || Camera.main == null) return;
        bool isInfantry = !unitAI.isMortar && !unitAI.isTank;
        string clipName = isSelection
            ? (isInfantry ? "Sounds/yes_sir_rex_sneaky_laugh" : "Sounds/target_locked_radio_deep")
            : (isInfantry ? "Sounds/ok_ill_do_it_rex" : "Sounds/roger_will_do_radio");
        AudioClip clip = Resources.Load<AudioClip>(clipName);
        if (clip != null) AudioSource.PlayClipAtPoint(clip, Camera.main.transform.position);
    }

    public void ConfirmerAction(int actionIndex)
    {
        // Son de clic UI et Vibration
        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
        if (uniteSelectionnee != null) PlayUnitVoiceLine(uniteSelectionnee.GetComponent<UnitAI>(), isSelection: false);
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
        PlayUnitVoiceLine(unitAI, isSelection: false);

        if (choice == 1) // Infiltration / Intérieur
        {
            if (selectedBuilding.tacticalVisibility != null)
            {
                selectedBuilding.tacticalVisibility.SetPlanificationPreview(true);
            }

            // currentBuilding ne doit être posé QUE s'il existe réellement une porte pour y entrer
            // (GetClosestDoor renvoie null pour un bâtiment sans donnée de porte générée, cas réel
            // et atteignable) — sinon l'unité se retrouve marquée "à l'intérieur" sans jamais avoir
            // de nœud EntrerBatiment ni bougé, ce qui fausse tous ses clics suivants et contourne
            // l'enregistrement d'occupation du bâtiment (RegisterUnitInside).
            var door = selectedBuilding.GetClosestDoor(unitAI.transform.position);
            if (door != null)
            {
                if (unitAI.currentBuilding != selectedBuilding)
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
            else
            {
                Debug.LogWarning($"[TacticalPathManager] {selectedBuilding.name} n'a aucune porte détectée — infiltration impossible pour cette unité.");
            }
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
        WaypointMarker[] existingMarkers = FindObjectsByType<WaypointMarker>(FindObjectsInactive.Include);
        foreach (var wm in existingMarkers)
        {
            if (wm != null) Destroy(wm.gameObject);
        }

        // MULTIJOUEUR : le calcul du tour est entièrement délégué au serveur autoritaire (voir
        // Assets/Scripts/Network/MultiplayerMatchController.cs). On envoie nos ordres et on
        // n'exécute JAMAIS de simulation locale ni de planification IA pour l'adversaire.
        if (Novgov.Network.MultiplayerMatchController.IsActive)
        {
            Novgov.Network.MultiplayerMatchController.Instance.SubmitLocalTurn();
            return;
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

    // =====================================================================
    // UI Toolkit (barre du bas + menus contextuels) — remplace l'ancien OnGUI().
    // Le contenu d'un menu contextuel est construit une seule fois, au moment précis où le
    // joueur tape (aux points où isXSelected passe à true, voir Update() plus haut) — pas
    // chaque frame comme le faisait OnGUI, pour ne jamais reconstruire un bouton pendant qu'il
    // est en train d'être touché. RefreshTacticalUI() (appelé chaque frame) ne fait que basculer
    // de la visibilité, jamais de reconstruction, donc sans risque d'interrompre un tap en cours.
    // =====================================================================
#if !UNITY_SERVER
    private bool tacticalUiBound = false;
    private VisualElement bottomBarRoot, contextMenuRoot, planGroupEl, execGroupEl, contextualBarEl, squadBarEl, topActionsRowEl;
    private Button view3dButtonEl;
    private Label notifBellBadgeEl;
    private System.Action currentMenuCancelAction;
    private readonly List<UnitAI> squadBarRoster = new List<UnitAI>();
    private readonly List<VisualElement> squadBarPortraitEls = new List<VisualElement>();

    private void BindTacticalUI()
    {
        // Le binding entier est protégé par un try/catch, et chaque étape est vérifiée
        // individuellement (voir plus bas) : si un écran/élément UXML manque, on logue une erreur
        // claire et on s'arrête proprement plutôt que de laisser tacticalUiBound passer à true trop
        // tôt et planter en boucle chaque frame dans RefreshTacticalUI() sur des champs jamais
        // assignés (voir le garde-fou correspondant dans RefreshTacticalUI()).
        try
        {
            if (UIScreenManager.Instance == null)
            {
                Debug.LogError("[TacticalPathManager] UIScreenManager.Instance introuvable — UIBootstrap ne s'est-il pas exécuté avant cette scène ?");
                return;
            }

            // tacticalUiBound n'est mis à true qu'à la toute fin, une fois TOUT le binding réussi.
            // Avant ce correctif, il était activé trop tôt : si une seule Query<T>() ci-dessous
            // retournait null (écran introuvable), l'exception qui suivait laissait RefreshTacticalUI()
            // — qui tourne chaque frame dès que tacticalUiBound est vrai — planter en boucle sur des
            // champs jamais assignés, silencieusement sur un build sans accès à la Console.
            bottomBarRoot = UIScreenManager.Instance.GetScreen("TacticalBottomBar");
            if (bottomBarRoot == null)
            {
                Debug.LogError("[TacticalPathManager] Écran 'TacticalBottomBar' introuvable (UXML non chargé) — Fin de tour/Annuler resteront inopérants.");
                return;
            }
            planGroupEl = bottomBarRoot.Q<VisualElement>("planification-group");
            execGroupEl = bottomBarRoot.Q<VisualElement>("execution-group");
            contextualBarEl = bottomBarRoot.Q<VisualElement>("contextual-bar");
            view3dButtonEl = bottomBarRoot.Q<Button>("view3d-button");
            squadBarEl = bottomBarRoot.Q<VisualElement>("squad-bar");
            topActionsRowEl = bottomBarRoot.Q<VisualElement>("top-actions-row");

            Button endTurnBtn = bottomBarRoot.Q<Button>("end-turn-button");
            if (endTurnBtn == null) { Debug.LogError("[TacticalPathManager] Bouton 'end-turn-button' introuvable dans le UXML instancié."); return; }
            endTurnBtn.clicked += LancerExecutionTour;

            Button notifBellBtn = bottomBarRoot.Q<Button>("notif-bell-button");
            notifBellBadgeEl = bottomBarRoot.Q<Label>("notif-bell-badge");
            if (notifBellBtn == null) { Debug.LogError("[TacticalPathManager] Bouton 'notif-bell-button' introuvable dans le UXML instancié."); return; }
            notifBellBtn.clicked += SelectionnerProchaineUniteBlessee;

            Button skipBtn = bottomBarRoot.Q<Button>("skip-button");
            if (skipBtn == null) { Debug.LogError("[TacticalPathManager] Bouton 'skip-button' introuvable dans le UXML instancié."); return; }
            skipBtn.clicked += ForcerFinExecution;

            Button deselectBtn = bottomBarRoot.Q<Button>("deselect-button");
            if (deselectBtn == null) { Debug.LogError("[TacticalPathManager] Bouton 'deselect-button' introuvable dans le UXML instancié."); return; }
            deselectBtn.clicked += () =>
            {
                SelectionnerUnite(null);
                isDoorSelected = false;
                isWindowSelected = false;
                isBuildingSelected = false;
                isGroundCheckpointSelected = false;
                selectedBuilding = null;
                currentMenuCancelAction?.Invoke();
                currentMenuCancelAction = null;
                UIScreenManager.Instance.SetVisible("ContextMenu", false);
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
            };

            Button undoBtn = bottomBarRoot.Q<Button>("undo-button");
            if (undoBtn == null) { Debug.LogError("[TacticalPathManager] Bouton 'undo-button' introuvable dans le UXML instancié."); return; }
            undoBtn.clicked += () =>
            {
                UnitAI uAI = uniteSelectionnee != null ? uniteSelectionnee.GetComponent<UnitAI>() : null;
                if (uAI != null && uAI.tacticalPath.Count > 0)
                {
                    uAI.RemoveLastTacticalNode();
                    DessinerTousLesChemins();
                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                    Debug.Log($"[{uAI.gameObject.name}] ↩️ Dernier checkpoint supprimé via le bouton Annuler.");
                }
                isGroundCheckpointSelected = false;
                isBuildingSelected = false;
                isDoorSelected = false;
                isWindowSelected = false;
                selectedBuilding = null;
                currentMenuCancelAction?.Invoke();
                currentMenuCancelAction = null;
                UIScreenManager.Instance.SetVisible("ContextMenu", false);
            };

            Button moveBtn = bottomBarRoot.Q<Button>("action-move");
            if (moveBtn != null)
            {
                moveBtn.clicked += () =>
                {
                    if (uniteSelectionnee != null)
                    {
                        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                        positionClicTemporaire = uniteSelectionnee.transform.position + uniteSelectionnee.transform.forward * 2f;
                        isGroundCheckpointSelected = true;
                        isBuildingSelected = false;
                        isDoorSelected = false;
                        isWindowSelected = false;
                        ShowGroundCheckpointMenu();
                    }
                };
            }

            if (view3dButtonEl == null) { Debug.LogError("[TacticalPathManager] Élément 'view3d-button' introuvable dans le UXML instancié."); return; }
            view3dButtonEl.clicked += () =>
            {
                if (CameraStateManager.Instance != null && uniteSelectionnee != null)
                {
                    CameraStateManager.Instance.Enter3DView(uniteSelectionnee.transform);
                    isDoorSelected = false;
                    isWindowSelected = false;
                    isBuildingSelected = false;
                    isGroundCheckpointSelected = false;
                    if (menuPanel != null) menuPanel.SetActive(false);
                    UIScreenManager.Instance.SetVisible("ContextMenu", false);
                }
            };

            contextMenuRoot = UIScreenManager.Instance.GetScreen("ContextMenu");
            if (contextMenuRoot == null)
            {
                Debug.LogError("[TacticalPathManager] Écran 'ContextMenu' introuvable (UXML non chargé) — les menus d'action (porte/toit/bâtiment) resteront inopérants.");
                return;
            }
            Button cancelBtn = contextMenuRoot.Q<Button>("cancel-button");
            if (cancelBtn == null) { Debug.LogError("[TacticalPathManager] Élément 'cancel-button' introuvable dans ContextMenu."); return; }
            cancelBtn.clicked += () =>
            {
                isGroundCheckpointSelected = false;
                isBuildingSelected = false;
                isDoorSelected = false;
                isWindowSelected = false;
                selectedBuilding = null;
                currentMenuCancelAction?.Invoke();
                currentMenuCancelAction = null;
                UIScreenManager.Instance.SetVisible("ContextMenu", false);
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
            };

            UIScreenManager.Instance.SetVisible("TacticalBottomBar", true);
            tacticalUiBound = true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[TacticalPathManager] BindTacticalUI() a levé une exception : {ex.GetType().Name} — {ex.Message}");
        }
    }

    /// <summary>Bascule la visibilité (fin de tour / barre contextuelle / bandeau d'exécution /
    /// menu ouvert) chaque frame — jamais de reconstruction ici.</summary>
    private void RefreshTacticalUI()
    {
        if (!tacticalUiBound) return;

        // Garde-fou : un rechargement de scripts survenu pendant que le Play Mode tourne encore
        // réinitialise les champs statiques (dont UIScreenManager.Instance), mais ne redéclenche
        // pas UIBootstrap.Init() (BeforeSceneLoad ne se relance pas en cours de session) — cette
        // instance de TacticalPathManager, elle, survit et continuerait sinon à planter ici à
        // chaque frame. Un Stop puis Play propre recrée tout correctement.
        if (UIScreenManager.Instance == null || bottomBarRoot == null || planGroupEl == null
            || execGroupEl == null || contextualBarEl == null || view3dButtonEl == null)
        {
            tacticalUiBound = false;
            return;
        }

        // Réaffirmé chaque frame plutôt qu'une seule fois dans BindTacticalUI() : UIScreenManager
        // .Show(nom) masque TOUS les autres écrans, y compris celui-ci — GameManagerUI.Show
        // ("StartupMenu") au lancement, ou MultiplayerMatchController.Show("InMatchHud") en PvP,
        // le cachaient donc définitivement dès leur premier appel puisque rien ne le réaffichait
        // ensuite. Mêmes conditions de masquage que UnitSpawnerUI.RefreshDeploymentDockUI(), qui
        // s'en sort pour la même raison (SetVisible appelé chaque frame, pas Show).
        bool is2DMode = CameraStateManager.Instance == null || CameraStateManager.Instance.CurrentState == CameraStateManager.CameraState.Command;

        bool hideBottomBar = !is2DMode;
        if (Novgov.Network.MultiplayerMatchController.IsFlowActive) hideBottomBar = true;
        if (GameManagerUI.Instance != null && GameManagerUI.Instance.IsStartupSelectionActive) hideBottomBar = true;
        UIScreenManager.Instance.SetVisible("TacticalBottomBar", !hideBottomBar);

        bool isPlanification = phaseActuelle == GamePhase.Planification;
        planGroupEl.style.display = isPlanification ? DisplayStyle.Flex : DisplayStyle.None;
        execGroupEl.style.display = isPlanification ? DisplayStyle.None : DisplayStyle.Flex;

        bool showContext = isPlanification && uniteSelectionnee != null;
        contextualBarEl.style.display = showContext ? DisplayStyle.Flex : DisplayStyle.None;
        view3dButtonEl.style.display = (is2DMode && showContext) ? DisplayStyle.Flex : DisplayStyle.None;

        if (showContext && uniteSelectionnee != null)
        {
            UnitAI uAI = uniteSelectionnee.GetComponent<UnitAI>();
            if (uAI != null)
            {
                var nameLabel = contextualBarEl.Q<Label>("unit-name");
                if (nameLabel != null) nameLabel.text = uniteSelectionnee.name.ToUpper();
                var hpLabel = contextualBarEl.Q<Label>("unit-health");
                if (hpLabel != null) hpLabel.text = $"HP: {(int)uAI.health}/{(int)uAI.maxHealth}";
            }
        }

        bool anyContextMenu = isBuildingSelected || isDoorSelected || isWindowSelected || isGroundCheckpointSelected;
        UIScreenManager.Instance.SetVisible("ContextMenu", anyContextMenu);

        RefreshSquadBar();
        PositionTopRightCluster();

        if (notifBellBadgeEl != null)
        {
            int wounded = CountWoundedPlayerUnits();
            notifBellBadgeEl.style.display = wounded > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            notifBellBadgeEl.text = wounded > 9 ? "9+" : wounded.ToString();
        }
    }

    /// <summary>
    /// Empile dynamiquement le cluster haut-droit (Fin de tour + cloche, barre contextuelle,
    /// portraits d'escouade) sous le radar tactique (OnGUI, TacticalRadarUI), en POURCENTAGE de
    /// Screen.height plutôt qu'en pixels fixes : un décalage en dur (essayé précédemment) casse
    /// dès que la résolution/le ratio d'écran change (constaté dans la fenêtre Game de l'Éditeur,
    /// où un "top: 460px" poussait tout hors d'une vue de seulement 472px de haut). Le pourcentage
    /// est recalculé chaque frame à partir de TacticalRadarUI.BottomEdgeScreenY (0 quand le radar
    /// est masqué, ex: vue 3D Action), donc le cluster remonte automatiquement dans ce cas.
    /// </summary>
    private void PositionTopRightCluster()
    {
        // Supprimé pour laisser l'interface UI Toolkit se positionner en haut de l'écran 
        // comme demandé par l'utilisateur ("tous les boutons en haut").
    }

    /// <summary>Barre de portraits d'escouade (coin haut-droit, style Commandos: Behind Enemy
    /// Lines). Ne reconstruit les boutons que quand le roster change réellement (mort/déploiement)
    private VisualElement mortarPortraitEl;
    private Button mortarBtnEl;
    private Label mortarCountEl;
    private readonly List<UnitAI> activeMortars = new List<UnitAI>();

    private VisualElement tankPortraitEl;
    private Button tankBtnEl;
    private Label tankCountEl;
    private readonly List<UnitAI> activeTanks = new List<UnitAI>();

    private VisualElement soldierPortraitEl;
    private Button soldierBtnEl;
    private Label soldierCountEl;
    private readonly List<UnitAI> activeInfantry = new List<UnitAI>();
    private bool squadBarInitialized = false;

    private void InitSquadBar()
    {
        if (squadBarEl == null) return;
        squadBarEl.Clear();

        // 1. Mortier
        mortarPortraitEl = new VisualElement();
        mortarPortraitEl.AddToClassList("squad-portrait");
        mortarBtnEl = new Button(() => CycleSelectGroup(activeMortars));
        mortarBtnEl.AddToClassList("squad-portrait-btn");
        mortarBtnEl.style.backgroundImage = new StyleBackground(ProceduralIconFactory.Mortar());
        mortarCountEl = new Label("0");
        mortarCountEl.style.position = Position.Absolute;
        mortarCountEl.style.bottom = 2;
        mortarCountEl.style.right = 4;
        mortarCountEl.style.color = Color.white;
        mortarCountEl.style.fontSize = 12;
        mortarCountEl.style.unityFontStyleAndWeight = FontStyle.Bold;
        mortarPortraitEl.Add(mortarBtnEl);
        mortarPortraitEl.Add(mortarCountEl);
        squadBarEl.Add(mortarPortraitEl);

        // 2. Chars
        tankPortraitEl = new VisualElement();
        tankPortraitEl.AddToClassList("squad-portrait");
        tankBtnEl = new Button(() => CycleSelectGroup(activeTanks));
        tankBtnEl.AddToClassList("squad-portrait-btn");
        tankBtnEl.style.backgroundImage = new StyleBackground(ProceduralIconFactory.Tank());
        tankCountEl = new Label("0");
        tankCountEl.style.position = Position.Absolute;
        tankCountEl.style.bottom = 2;
        tankCountEl.style.right = 4;
        tankCountEl.style.color = Color.white;
        tankCountEl.style.fontSize = 12;
        tankCountEl.style.unityFontStyleAndWeight = FontStyle.Bold;
        tankPortraitEl.Add(tankBtnEl);
        tankPortraitEl.Add(tankCountEl);
        squadBarEl.Add(tankPortraitEl);

        // 3. Infanterie
        soldierPortraitEl = new VisualElement();
        soldierPortraitEl.AddToClassList("squad-portrait");
        soldierBtnEl = new Button(() => CycleSelectGroup(activeInfantry));
        soldierBtnEl.AddToClassList("squad-portrait-btn");
        soldierBtnEl.style.backgroundImage = new StyleBackground(ProceduralIconFactory.Soldier());
        soldierCountEl = new Label("0");
        soldierCountEl.style.position = Position.Absolute;
        soldierCountEl.style.bottom = 2;
        soldierCountEl.style.right = 4;
        soldierCountEl.style.color = Color.white;
        soldierCountEl.style.fontSize = 12;
        soldierCountEl.style.unityFontStyleAndWeight = FontStyle.Bold;
        soldierPortraitEl.Add(soldierBtnEl);
        soldierPortraitEl.Add(soldierCountEl);
        squadBarEl.Add(soldierPortraitEl);

        squadBarInitialized = true;
    }

    private void RefreshSquadBar()
    {
        if (squadBarEl == null) return;
        if (!squadBarInitialized) InitSquadBar();

        activeMortars.Clear();
        activeTanks.Clear();
        activeInfantry.Clear();

        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null && u.isPlayerControlled && !u.isDead)
            {
                string nameLower = u.gameObject.name.ToLowerInvariant();
                if (u.isMortar || nameLower.Contains("mortier") || nameLower.Contains("mortar") || nameLower.Contains("artillerie"))
                {
                    activeMortars.Add(u);
                }
                else if (u.isTank || u.isCanonVehicle || nameLower.Contains("leopard") || nameLower.Contains("char") || nameLower.Contains("tank") || nameLower.Contains("canon"))
                {
                    activeTanks.Add(u);
                }
                else
                {
                    activeInfantry.Add(u);
                }
            }
        }

        // Mortiers
        mortarPortraitEl.style.display = activeMortars.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        mortarCountEl.text = activeMortars.Count.ToString();
        mortarBtnEl.EnableInClassList("selected", activeMortars.Exists(u => u.gameObject == uniteSelectionnee));

        // Chars
        tankPortraitEl.style.display = activeTanks.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        tankCountEl.text = activeTanks.Count.ToString();
        tankBtnEl.EnableInClassList("selected", activeTanks.Exists(u => u.gameObject == uniteSelectionnee));

        // Infanterie
        soldierPortraitEl.style.display = activeInfantry.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        soldierCountEl.text = activeInfantry.Count.ToString();
        soldierBtnEl.EnableInClassList("selected", activeInfantry.Exists(u => u.gameObject == uniteSelectionnee));
    }

    private void CycleSelectGroup(List<UnitAI> group)
    {
        if (group.Count == 0) return;
        
        int currentIndex = -1;
        if (uniteSelectionnee != null)
        {
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i].gameObject == uniteSelectionnee) { currentIndex = i; break; }
            }
        }

        int nextIndex = (currentIndex + 1) % group.Count;
        UnitAI targetUnit = group[nextIndex];
        SelectionnerUnite(targetUnit.gameObject);

        if (TacticalCamera.Instance != null)
        {
            TacticalCamera.Instance.focusPosition = targetUnit.transform.position;
        }
    }

    private int CountPlayerUnits()
    {
        int count = 0;
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null && u.isPlayerControlled && !u.isDead) count++;
        }
        return count;
    }

    private static Texture2D SquadPortraitIcon(UnitAI unit)
    {
        if (unit.isTank) return ProceduralIconFactory.Tank();
        if (unit.isMortar) return ProceduralIconFactory.Mortar();
        string n = unit.gameObject.name.ToLowerInvariant();
        if (n.Contains("canon")) return ProceduralIconFactory.GunVehicle();
        if (n.Contains("barricade") || n.Contains("barrier")) return ProceduralIconFactory.Barrier();
        return ProceduralIconFactory.Soldier();
    }

    private void ShowContextMenu(string title, System.Action onCancel, params (string text, Color color, System.Action onClick)[] buttons)
    {
        if (!tacticalUiBound) return;
        contextMenuRoot.Q<Label>("menu-title").text = title;
        VisualElement container = contextMenuRoot.Q<VisualElement>("button-container");
        container.Clear();
        foreach (var (text, color, onClick) in buttons)
        {
            var btn = new Button(() =>
            {
                onClick?.Invoke();
                isGroundCheckpointSelected = false;
                isBuildingSelected = false;
                isDoorSelected = false;
                isWindowSelected = false;
                selectedBuilding = null;
                currentMenuCancelAction = null;
                UIScreenManager.Instance.SetVisible("ContextMenu", false);
            }) { text = text };
            btn.AddToClassList("context-button");
            btn.style.borderLeftColor = new StyleColor(color);
            container.Add(btn);
        }
        currentMenuCancelAction = onCancel;
        UIScreenManager.Instance.SetVisible("ContextMenu", true);
    }

    private void ShowBuildingMenu()
    {
        ShowContextMenu($"🏢 BÂTIMENT : {selectedBuilding.gameObject.name}",
            () => { isBuildingSelected = false; selectedBuilding = null; },
            ("1. 🏢 INFILTRATION / INTÉRIEUR (RDC)", new Color(0.3f, 1f, 0.5f), () => ConfirmerBuildingAction(1)),
            ("2. 🧗 MONTER SUR LE TOIT (Sniper / Guet)", new Color(0.2f, 0.9f, 1f), () => ConfirmerBuildingAction(2)),
            ("3. 🚪 PORTE LA PLUS PROCHE", Color.yellow, () => ConfirmerBuildingAction(3))
        );
    }

    private void ShowDoorMenu()
    {
        if (isExitDoorAction)
        {
            ShowContextMenu($"🚪 PORTE : {selectedDoor.building.gameObject.name}",
                () => { isDoorSelected = false; },
                ("1. 🚪 SORTIR DANS LA RUE", Color.green, () => ConfirmerAction((int)NodeAction.SortirBatiment)),
                ("2. 👁️ GUETTER PAR LA PORTE", Color.cyan, () => ConfirmerAction((int)NodeAction.GuetterPorte)),
                ("3. 📍 CHECKPOINT SIMPLE", Color.yellow, () => ConfirmerAction((int)NodeAction.Continuer))
            );
        }
        else
        {
            ShowContextMenu($"🚪 ENTRÉE : {selectedDoor.building.gameObject.name}",
                () => { isDoorSelected = false; },
                ("🚪 ENTRER DANS LE BÂTIMENT", Color.cyan, () => ConfirmerAction((int)NodeAction.EntrerBatiment))
            );
        }
    }

    private void ShowWindowMenu()
    {
        ShowContextMenu($"🛡️ FENÊTRE : {selectedWindow.building.gameObject.name}",
            () => { isWindowSelected = false; },
            ("1. 🛡️ GUETTER (Couvert -75%)", new Color(1f, 0.75f, 0.1f), () => ConfirmerAction((int)NodeAction.GarnisonFenetre)),
            ("2. 📍 CHECKPOINT SIMPLE", Color.yellow, () => ConfirmerAction((int)NodeAction.Continuer))
        );
    }

    /// <summary>Reconstruit le menu du checkpoint au sol — mêmes règles que l'ancien OnGUI (mortier
    /// / blindé / 4 variantes d'infanterie selon toit-cible, unité déjà sur un toit, unité à
    /// l'intérieur). Appelé une fois au moment du tap (voir Update()), pas chaque frame.</summary>
    private void ShowGroundCheckpointMenu()
    {
        UnitAI selectedUnitAI = uniteSelectionnee.GetComponent<UnitAI>();
        bool isMortarUnit = (selectedUnitAI != null && selectedUnitAI.isMortar);

        System.Action onCancel = () => { isGroundCheckpointSelected = false; };

        if (isMortarUnit)
        {
            ShowContextMenu("💥 ARTILLERIE : ORDRE DE TIR", onCancel,
                ("1. 🎯 TIR DE MORTIER (Zone AoE)", new Color(1f, 0.4f, 0.1f), () => ConfirmerAction((int)NodeAction.TirMortier)),
                ("2. ▶️ SE DÉPLACER (Position)", Color.white, () => ConfirmerAction((int)NodeAction.Continuer)),
                ("3. ⏳ ATTENDRE 30 SECONDES", Color.yellow, () => ConfirmerAction((int)NodeAction.Attendre30s))
            );
            return;
        }

        if (selectedUnitAI != null && selectedUnitAI.isTank)
        {
            ShowContextMenu("🛡️ BLINDÉ : ORDRE DE MANOEUVRE", onCancel,
                ("1. ▶️ AVANCER (Déplacement)", Color.white, () => ConfirmerAction((int)NodeAction.Continuer)),
                ("2. 🛡️ GUETTER (Surveillance)", Color.cyan, () => ConfirmerAction((int)NodeAction.Guetter)),
                ("3. ⏳ ATTENDRE 30 SECONDES", Color.yellow, () => ConfirmerAction((int)NodeAction.Attendre30s))
            );
            return;
        }

        // Menu Fantassin
        UnitAI uAI = uniteSelectionnee.GetComponent<UnitAI>();
        bool isTargetOnRoof = positionClicTemporaire.y > 1.8f;

        bool unitIsAlreadyOnRoof = (uAI != null && (uAI.isRooftopSniper || uAI.transform.position.y > 2.0f));
        if (uAI != null && uAI.tacticalPath.Count > 0)
        {
            var lastN = uAI.tacticalPath[uAI.tacticalPath.Count - 1];
            if (lastN.action == NodeAction.Escalade || lastN.position.y > 2.0f) unitIsAlreadyOnRoof = true;
        }

        bool unitIsInsideBuilding = (uAI != null && uAI.currentBuilding != null && !uAI.isRooftopSniper);
        if (uAI != null && uAI.tacticalPath.Count > 0)
        {
            var lastN = uAI.tacticalPath[uAI.tacticalPath.Count - 1];
            if (lastN.action == NodeAction.EntrerBatiment) unitIsInsideBuilding = true;
            else if (lastN.action == NodeAction.SortirBatiment) unitIsInsideBuilding = false;
        }

        if (isTargetOnRoof)
        {
            if (unitIsAlreadyOnRoof)
            {
                ShowContextMenu("🏃 INFANTERIE : DÉPLACEMENT TOIT", onCancel,
                    ("1. ▶️ CONTINUER SUR LE TOIT", Color.white, () => ConfirmerAction((int)NodeAction.Continuer)),
                    ("2. 🛡️ GUETTER SUR LE TOIT", Color.cyan, () => ConfirmerAction((int)NodeAction.Guetter)),
                    ("3. ⏳ ATTENDRE 30 SECONDES", Color.yellow, () => ConfirmerAction((int)NodeAction.Attendre30s))
                );
            }
            else
            {
                ShowContextMenu("🧗 INFANTERIE : ESCALADE DE FAÇADE", onCancel,
                    ("1. 🧗 ESCALADER SUR LE TOIT", new Color(0.2f, 0.9f, 0.4f), () => ConfirmerAction((int)NodeAction.Escalade)),
                    ("2. 🛡️ GUETTER (+50% Défense)", Color.cyan, () => ConfirmerAction((int)NodeAction.Guetter)),
                    ("3. ⏳ ATTENDRE 30 SECONDES", Color.yellow, () => ConfirmerAction((int)NodeAction.Attendre30s))
                );
            }
            return;
        }

        if (unitIsAlreadyOnRoof)
        {
            ShowContextMenu("🧗 INFANTERIE : DESCENTE VERS RUE", onCancel,
                ("1. 🧗 DESCENDRE DU TOIT", new Color(1f, 0.6f, 0.2f), () => ConfirmerAction((int)NodeAction.Escalade)),
                ("2. 🛡️ GUETTER (+50% Défense)", Color.cyan, () => ConfirmerAction((int)NodeAction.Guetter)),
                ("3. ⏳ ATTENDRE 30 SECONDES", Color.yellow, () => ConfirmerAction((int)NodeAction.Attendre30s))
            );
            return;
        }

        if (unitIsInsideBuilding)
        {
            ShowContextMenu("🏢 INFANTERIE : DÉPLACEMENT INTÉRIEUR", onCancel,
                ("1. ▶️ SE DÉPLACER À L'INTÉRIEUR", Color.white, () => ConfirmerAction((int)NodeAction.Continuer)),
                ("2. 🛡️ GUETTER INTÉRIEUR", Color.cyan, () => ConfirmerAction((int)NodeAction.Guetter)),
                ("3. ⏳ ATTENDRE 30 SECONDES", Color.yellow, () => ConfirmerAction((int)NodeAction.Attendre30s))
            );
            return;
        }

        if (isNearBuildingWall)
        {
            ShowContextMenu("🎖️ INFANTERIE : ORDRE TACTIQUE", onCancel,
                ("1. ▶️ CONTINUER (Mouvement)", Color.white, () => ConfirmerAction((int)NodeAction.Continuer)),
                ("2. 🛡️ GUETTER (+50% Défense)", Color.cyan, () => ConfirmerAction((int)NodeAction.Guetter)),
                ("3. ⏳ ATTENDRE 30 SECONDES", Color.yellow, () => ConfirmerAction((int)NodeAction.Attendre30s)),
                ("4. 🥷 SE CACHER (Contre mur)", Color.green, () => ConfirmerAction((int)NodeAction.SeCacher))
            );
        }
        else
        {
            ShowContextMenu("🎖️ INFANTERIE : ORDRE TACTIQUE", onCancel,
                ("1. ▶️ CONTINUER (Mouvement)", Color.white, () => ConfirmerAction((int)NodeAction.Continuer)),
                ("2. 🛡️ GUETTER (+50% Défense)", Color.cyan, () => ConfirmerAction((int)NodeAction.Guetter)),
                ("3. ⏳ ATTENDRE 30 SECONDES", Color.yellow, () => ConfirmerAction((int)NodeAction.Attendre30s))
            );
        }
    }
#endif
}