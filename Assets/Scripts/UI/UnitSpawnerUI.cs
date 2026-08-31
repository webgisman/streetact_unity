using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using UnityEngine.UIElements;

/// <summary>
/// Menu tactique de déploiement et de Drag & Drop des unités sur le champ de bataille (Max 6 unités).
/// Permet de placer Fantassins, Chars Leopard 2 et Véhicules Canons pour les deux camps.
/// </summary>
public class UnitSpawnerUI : MonoBehaviour
{
    public static UnitSpawnerUI Instance { get; private set; }

    public enum UnitType { Fantassin, CharLeopard, VehiculeCanon, Mortier, BarricadeRoutiere }

    [Header("Configuration")]
    public int maxUnitsPerTeam = 12;
    public int selectedTeam = 1; // 1 = Joueur (Bleu), 2 = Ennemi (Rouge)

    // Les barricades n'ont pas de UnitAI (voir RoadBarrier), donc jamais comptées par
    // GetTeamLivingUnitsCount/maxUnitsPerTeam ci-dessus — sans cette limite dédiée, un camp pouvait
    // en poser un nombre illimité.
    [Header("Stock Barricades")]
    public int maxBarricadesPerTeam = 8;

    // État du Drag & Drop / Placement
    public static bool IsPlacingUnit = false;
    // Marqué à Time.frameCount à chaque frame où HandlePlacementPreview traite une entrée —
    // TacticalPathManager.HandlePointerInput lit AUSSI la souris dans son propre Update(), sur la
    // MÊME frame. Un tap simple pose une unité puis appelle CancelPlacement() (IsPlacingUnit →
    // false) ; si TacticalPathManager s'exécute ensuite dans cette même frame, son garde-fou
    // `if (IsPlacingUnit) return;` ne voit plus que IsPlacingUnit est déjà retombé à false, et
    // retraite ce même relâchement de clic comme un tap normal sur l'unité qui vient d'être posée
    // (ex: ouvre le menu RETIRER d'une barricade fraîchement déployée). Ce marqueur, vérifié EN
    // PLUS de IsPlacingUnit, ferme cette fenêtre de course indépendamment de l'ordre d'exécution
    // des scripts.
    public static int lastPlacementActionFrame = -1;
    private UnitType? activePlacingType = null;
    private GameObject previewRing;
    private Material previewMat;

    // UI State
    private bool isPanelOpen = false; // Fermé par défaut pour libérer l'écran
    // Suivi de transition pour le contournement du bug de rendu du dock (voir RefreshDeploymentDockUI).
    private bool dockShownLastFrame = false;
    private bool dockPanelOpenLastFrame = false;
    private string statusMessage = "";
    private float statusMessageTimer = 0f;
    private float ignorePlacementTime = 0f;
    public static float lastUIClickTime = 0f;

    void Awake()
    {
        Instance = this;
#if !UNITY_SERVER
        BindDeploymentUI();
#endif
    }

    void Start()
    {
        // Création de l'anneau holographique de prévisualisation au sol
        previewRing = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        previewRing.name = "DeploymentPreviewRing";
        Destroy(previewRing.GetComponent<Collider>());
        previewRing.transform.localScale = new Vector3(2.0f, 0.02f, 2.0f);

        previewMat = SafeMaterialFactory.CreateUnlit(new Color(0f, 1f, 0.4f, 0.6f));
        previewRing.GetComponent<MeshRenderer>().sharedMaterial = previewMat;
        previewRing.SetActive(false);

        // Pas de déploiement automatique au lancement : le joueur choisit lui-même où placer
        // chaque unité via le dock "QG Renforts" (voir HandlePlacementPreview, qui vérifie déjà
        // qu'on ne pose jamais un char/canon sur un toit). L'ancien auto-spawn plaçait les deux
        // camps sur des points calculés par plus-proche-NavMesh, sans cette vérification —
        // d'où des unités qui apparaissaient parfois à l'intérieur des bâtiments générés.
    }

    void Update()
    {
        if (statusMessageTimer > 0f)
        {
            statusMessageTimer -= Time.deltaTime;
            if (statusMessageTimer <= 0f) statusMessage = "";
        }

        // Gestion de la prévisualisation et du placement
        if (IsPlacingUnit && activePlacingType.HasValue)
        {
            HandlePlacementPreview();

            // Clic Droit ou Échap pour annuler
            if ((Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame) ||
                (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame))
            {
                CancelPlacement();
            }
        }

#if !UNITY_SERVER
        RefreshDeploymentDockUI();
#endif
    }

    private Vector2 placementDownPos;
    private bool isPlacementPointerDown = false;

    private void HandlePlacementPreview()
    {
        // Voir le commentaire sur lastPlacementActionFrame : marqué ici, avant toute autre chose,
        // pour couvrir tous les types d'unité traités ci-dessous, barricades comprises — MÊME
        // méthode de placement que les autres (un tap = un point testé), pas de glisser : une
        // barricade n'est qu'un type d'unité de plus ici, avec une seule différence de
        // comportement une fois le point validé (voir plus bas, propose un menu au lieu de poser
        // immédiatement, et reste en mode placement pour enchaîner la suivante).
        lastPlacementActionFrame = Time.frameCount;

        try
        {
            Vector2 pointerPos = Vector2.zero;
            bool hasPointer = false;
            bool wasPressed = false;
            bool wasReleased = false;

            if (Touchscreen.current != null && Touchscreen.current.touches.Count > 0)
            {
                var touch = Touchscreen.current.touches[0];
                pointerPos = touch.position.ReadValue();
                hasPointer = true;
                wasPressed = touch.press.wasPressedThisFrame;
                wasReleased = touch.press.wasReleasedThisFrame;
            }
            else if (Pointer.current != null)
            {
                pointerPos = Pointer.current.position.ReadValue();
                hasPointer = true;
                wasPressed = Pointer.current.press.wasPressedThisFrame;
                wasReleased = Pointer.current.press.wasReleasedThisFrame;
            }

            if (!hasPointer) return;

            Camera cam = Camera.main ?? FindAnyObjectByType<Camera>();
            if (cam == null) return;

            // Calcul du point d'impact 3D (Raycast ou Plan de sol de secours)
            Ray ray = cam.ScreenPointToRay(pointerPos);
            Vector3 targetWorldPos = Vector3.zero;
            bool hasGroundPos = false;

            if (Physics.Raycast(ray, out RaycastHit hit, 500f))
            {
                targetWorldPos = hit.point;
                hasGroundPos = true;
            }
            else
            {
                Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
                if (groundPlane.Raycast(ray, out float enter))
                {
                    targetWorldPos = ray.GetPoint(enter);
                    hasGroundPos = true;
                }
            }

            if (hasGroundPos)
            {
                // Détecter si on clique sur un bâtiment ou un toit
                bool isBuildingOrRoof = false;
                if (Physics.Raycast(ray, out RaycastHit hitInfo, 500f))
                {
                    if (hitInfo.collider != null)
                    {
                        if (hitInfo.collider.GetComponentInParent<BuildingStructure>() != null ||
                            hitInfo.collider.name.ToLower().Contains("building") ||
                            hitInfo.collider.name.ToLower().Contains("roof") ||
                            hitInfo.point.y > 1.8f)
                        {
                            isBuildingOrRoof = true;
                        }
                    }
                }

                bool tappedOnExistingUnit = hitInfo.collider != null && hitInfo.collider.GetComponentInParent<UnitAI>() != null;

                bool isHeavyUnit = (activePlacingType == UnitType.CharLeopard ||
                                    activePlacingType == UnitType.VehiculeCanon ||
                                    activePlacingType == UnitType.Mortier ||
                                    activePlacingType == UnitType.BarricadeRoutiere);

                bool isValid = false;
                NavMeshHit navHit = default;

                if (isHeavyUnit && isBuildingOrRoof)
                {
                    isValid = false; // Les véhicules/chars sont formellement interdits sur les toits !
                }
                else
                {
                    // Vérifier si le point est sur ou proche d'une rue NavMesh (rayon de 8m au sol, hauteur cohérente)
                    isValid = NavMesh.SamplePosition(targetWorldPos, out navHit, 8.0f, NavMesh.AllAreas) &&
                              Mathf.Abs(targetWorldPos.y - navHit.position.y) < 2.5f;
                }

                if (previewRing != null)
                {
                    previewRing.SetActive(true);
                    Vector3 ringPos = isValid ? navHit.position : targetWorldPos;
                    ringPos.y += 0.05f;
                    previewRing.transform.position = ringPos;

                    if (previewMat != null)
                    {
                        Color ringCol = isValid ? (selectedTeam == 1 ? new Color(0f, 0.9f, 1f, 0.8f) : new Color(1f, 0.35f, 0.2f, 0.8f)) : new Color(1f, 0f, 0f, 0.6f);
                        if (previewMat.HasProperty("_BaseColor")) previewMat.SetColor("_BaseColor", ringCol);
                        else if (previewMat.HasProperty("_Color")) previewMat.color = ringCol;
                    }
                }

                // Détection du Tap propre (relâchement après clic au sol)
                if (wasPressed)
                {
                    isPlacementPointerDown = true;
                    placementDownPos = pointerPos;
                    Debug.Log($"<color=cyan>[UnitSpawnerUI] 🎯 Doigt posé à {pointerPos}</color>");
                }
                else if (wasReleased && isPlacementPointerDown)
                {
                    isPlacementPointerDown = false;
                    float dragDist = Vector2.Distance(placementDownPos, pointerPos);
                    Debug.Log($"<color=yellow>[UnitSpawnerUI] 👆 Doigt relâché (déplacement: {dragDist:F1}px, valide={isValid})</color>");

                    // Ignorer si on a glissé pour bouger la caméra (plus de 45 pixels)
                    if (dragDist < 45f)
                    {
                        if (tappedOnExistingUnit)
                        {
                            // On ne redéploie jamais une unité par-dessus une autre déjà posée :
                            // on annule le placement pour que le prochain tap serve à la sélectionner.
                            CancelPlacement();
                            ShowMessage("Emplacement occupé — placement annulé. Retape sur l'unité pour la sélectionner.", 2.5f);
                        }
                        else if (isValid && activePlacingType.HasValue && activePlacingType.Value == UnitType.BarricadeRoutiere)
                        {
                            if (RemainingBarricadeStock(selectedTeam) <= 0)
                            {
                                ShowMessage($"Nombre insuffisant : stock de barricades épuisé ({maxBarricadesPerTeam} max par camp) !", 2.5f);
                            }
                            else if (!lastPlacedBarricadeAnchor.HasValue)
                            {
                                // Toute première barricade de cette session : posée DIRECTEMENT,
                                // exactement comme n'importe quelle autre unité — aucun menu, aucun
                                // calcul de trajectoire. Reste en mode placement (contrairement aux
                                // autres types) pour permettre une extension à partir d'ici.
                                Debug.Log($"<color=lime>[UnitSpawnerUI] 🚀 Barricade posée en {navHit.position} pour équipe {selectedTeam} !</color>");
                                SpawnUnitAt(UnitType.BarricadeRoutiere, navHit.position, selectedTeam);
                                lastPlacedBarricadeAnchor = navHit.position;
                            }
                            else
                            {
                                // Extension : ce tap prolonge la ligne depuis la dernière barricade
                                // posée. Calcule le trajet entre les deux, en évitant les bâtiments,
                                // dans la limite du stock restant — SEULEMENT dans ce cas le menu de
                                // confirmation s'affiche (voir ShowBarricadeExtensionMenu).
                                List<Vector3> extension = ComputeBarricadeExtensionPositions(lastPlacedBarricadeAnchor.Value, navHit.position, BARRICADE_SPACING, RemainingBarricadeStock(selectedTeam));
                                if (extension.Count == 0)
                                {
                                    ShowMessage("Nombre insuffisant ou aucun emplacement faisable pour cette extension (bâtiments sur le trajet) — retape ailleurs.", 2.5f);
                                }
                                else
                                {
                                    pendingBarricadeExtension = extension;
#if !UNITY_SERVER
                                    TacticalPathManager.Instance?.ShowBarricadeExtensionMenu(extension.Count);
#endif
                                }
                            }
                        }
                        else if (isValid && activePlacingType.HasValue)
                        {
                            Debug.Log($"<color=lime>[UnitSpawnerUI] 🚀 DÉPLOIEMENT : {activePlacingType.Value} en position {navHit.position} pour équipe {selectedTeam} !</color>");
                            SpawnUnitAt(activePlacingType.Value, navHit.position, selectedTeam);
                            CancelPlacement();
#if UNITY_ANDROID || UNITY_IOS
                            if (Application.isMobilePlatform) Handheld.Vibrate();
#endif
                        }
                        else
                        {
                            string errMsg = (isHeavyUnit && isBuildingOrRoof)
                                ? "Les véhicules et canons doivent être placés sur la rue, pas sur les toits !"
                                : "Emplacement hors-carte ! Touchez une rue pour déployer l'unité.";
                            ShowMessage(errMsg, 2.5f);
                        }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[UnitSpawnerUI Placement Error] {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Espacement approximatif d'une barricade posée (voir échelle x1.3 dans SpawnUnitAt).
    private const float BARRICADE_SPACING = 3.5f;

    // Position de la DERNIÈRE barricade posée pendant cette session de placement — null tant
    // qu'aucune n'a encore été posée. La toute première barricade se pose directement, comme
    // n'importe quelle autre unité (voir HandlePlacementPreview) : aucun menu, aucun calcul de
    // trajectoire. Ce n'est qu'à partir de la DEUXIÈME barricade (une "extension" de la première)
    // que le trajet entre cette ancre et le nouveau point tapé est calculé, avec vérification de
    // faisabilité (évite les bâtiments) et de stock ; le menu ne s'affiche QUE pour cette
    // confirmation d'extension, jamais pour un simple premier placement.
    private Vector3? lastPlacedBarricadeAnchor = null;

    // Trajet d'extension calculé, PAS ENCORE posé — en attente du menu DÉPLOYER/ANNULER (voir
    // TacticalPathManager.ShowBarricadeExtensionMenu).
    private List<Vector3> pendingBarricadeExtension = null;

    /// <summary>DÉPLOYER : pose réellement les barricades de l'extension proposée, avance l'ancre
    /// jusqu'au bout de ce tracé (pour permettre d'enchaîner une NOUVELLE extension à partir de
    /// là), et reste en mode placement.</summary>
    public void ConfirmPendingBarricadeExtension()
    {
        if (pendingBarricadeExtension == null) return;
        int count = pendingBarricadeExtension.Count;
        foreach (var p in pendingBarricadeExtension)
        {
            SpawnUnitAt(UnitType.BarricadeRoutiere, p, selectedTeam);
        }
        lastPlacedBarricadeAnchor = pendingBarricadeExtension[count - 1];
        pendingBarricadeExtension = null;
        ShowMessage($"{count} barricade(s) ajoutée(s) le long du tracé !", 2.5f);
    }

    /// <summary>ANNULER : abandonne CETTE extension proposée sans rien poser, garde l'ancre
    /// existante et reste en mode placement pour que le joueur retape un autre point.</summary>
    public void CancelPendingBarricadeExtension()
    {
        pendingBarricadeExtension = null;
    }

    /// <summary>Rue/NavMesh valide, pas sur un bâtiment ou un toit — même principe que le test de
    /// faisabilité du tap direct dans HandlePlacementPreview (raycast vertical + collider
    /// réellement touché, pas BuildingStructure.FindBuildingAt dont l'empreinte 2D déborde souvent
    /// sur la rue adjacente), réappliqué ici à CHAQUE point intermédiaire d'une extension : le
    /// trajet entre l'ancre et le point tapé peut très bien longer un bâtiment sur une partie de
    /// son parcours sans que les deux bouts ne soient concernés.</summary>
    private bool TryFindFeasibleBarricadeSpot(Vector3 worldPos, out Vector3 result)
    {
        result = worldPos;
        if (!NavMesh.SamplePosition(worldPos, out NavMeshHit navHit, 3.0f, NavMesh.AllAreas)) return false;
        if (Mathf.Abs(worldPos.y - navHit.position.y) > 2.5f) return false;

        if (Physics.Raycast(navHit.position + Vector3.up * 5f, Vector3.down, out RaycastHit downHit, 10f))
        {
            if (downHit.collider != null)
            {
                bool isBuildingOrRoof = downHit.collider.GetComponentInParent<BuildingStructure>() != null ||
                                        downHit.collider.name.ToLower().Contains("building") ||
                                        downHit.collider.name.ToLower().Contains("roof") ||
                                        downHit.point.y > 1.8f;
                if (isBuildingOrRoof) return false;
            }
        }

        result = navHit.position;
        return true;
    }

    /// <summary>Échantillonne les points d'une extension entre l'ancre (DÉJÀ posée, donc exclue —
    /// la boucle part de i=1) et le nouveau point tapé, ne retient que ceux réellement faisables,
    /// et s'arrête dès que maxCount (stock restant) est atteint.</summary>
    private List<Vector3> ComputeBarricadeExtensionPositions(Vector3 anchor, Vector3 target, float spacing, int maxCount)
    {
        var results = new List<Vector3>();
        if (maxCount <= 0) return results;

        float totalDist = Vector3.Distance(anchor, target);
        int steps = Mathf.Max(1, Mathf.RoundToInt(totalDist / spacing));
        Vector3 lastAccepted = anchor;

        for (int i = 1; i <= steps && results.Count < maxCount; i++)
        {
            float t = (float)i / steps;
            Vector3 candidate = Vector3.Lerp(anchor, target, t);
            if (!TryFindFeasibleBarricadeSpot(candidate, out Vector3 feasible)) continue;
            if (Vector3.Distance(lastAccepted, feasible) < spacing * 0.6f) continue;
            results.Add(feasible);
            lastAccepted = feasible;
        }
        return results;
    }

    public void StartPlacingUnit(UnitType type)
    {
        int teamCount = GetTeamLivingUnitsCount(selectedTeam);
        if (teamCount >= maxUnitsPerTeam)
        {
            string teamName = (selectedTeam == 1) ? "Joueur (Bleu)" : "Ennemi (Rouge)";
            ShowMessage($"Limite atteinte pour l'équipe {teamName} ({maxUnitsPerTeam} unités max par camp) !", 3.0f);
            return;
        }

        if (type == UnitType.BarricadeRoutiere && RemainingBarricadeStock(selectedTeam) <= 0)
        {
            ShowMessage($"Stock de barricades épuisé ({maxBarricadesPerTeam} max par camp) !", 3.0f);
            return;
        }

        // Armer le placement d'un AUTRE type pendant qu'une ancre de barricade est encore active
        // (barricade posée puis dock retapé sur un autre type sans avoir confirmé de nouveau
        // placement au sol) laissait l'ancre en place ; un retour ultérieur sur "Barricade" traitait
        // alors le tap suivant comme une extension de cette ligne périmée au lieu d'un placement
        // neuf. CancelPlacement() gère déjà le cas d'un placement confirmé/annulé explicitement.
        if (type != UnitType.BarricadeRoutiere)
        {
            lastPlacedBarricadeAnchor = null;
            pendingBarricadeExtension = null;
        }

        activePlacingType = type;
        IsPlacingUnit = true;
        isPanelOpen = false; // Ferme le dock pour libérer tout l'écran tactile
        ignorePlacementTime = Time.time + 0.35f; // Délai anti-misfire
        isPlacementPointerDown = false;

        if (type == UnitType.BarricadeRoutiere)
        {
            ShowMessage($"Touchez une rue pour poser une barricade ({RemainingBarricadeStock(selectedTeam)} restantes) — retapez pour en aligner d'autres à la suite.", 4.0f);
        }
        else
        {
            string unitName = (type == UnitType.CharLeopard) ? "Char Leopard 2" : (type == UnitType.VehiculeCanon ? "Véhicule Canon" : (type == UnitType.Mortier ? "Mortier" : "Fantassin"));
            ShowMessage($"Touchez une rue pour déployer : {unitName}", 4.0f);
        }
        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateHoverSound(), Camera.main.transform.position);
    }

    public void CancelPlacement()
    {
        IsPlacingUnit = false;
        activePlacingType = null;
        lastPlacedBarricadeAnchor = null;
        pendingBarricadeExtension = null;
        if (previewRing != null) previewRing.SetActive(false);
        // Si l'annulation vient d'ailleurs que le bouton ANNULER du menu DÉPLOYER lui-même (clic
        // droit, Échap, bouton DÉPLOIEMENT) — sans ça la fenêtre resterait affichée, orpheline.
#if !UNITY_SERVER
        TacticalPathManager.Instance?.CloseBarricadeDeployMenuIfOpen();
#endif
    }

    /// <param name="forcedName">
    /// Utilisé UNIQUEMENT côté client en multijoueur, en rejouant "deployment_result" : le nom doit
    /// être EXACTEMENT celui que le serveur a assigné (voir MatchSessionManager.ResolveDeployment)
    /// pour que PlaySnapshotsCoroutine retrouve la bonne unité par nom plus tard. Laissé à null
    /// partout ailleurs (solo, hotseat, et les appels serveur eux-mêmes) : le nom auto-généré
    /// habituel (ex: "Fantassin_1_2") reste inchangé.
    /// </param>
    public UnitAI SpawnUnitAt(UnitType type, Vector3 position, int team, string forcedName = null)
    {
        int teamCount = GetTeamLivingUnitsCount(team);
        if (teamCount >= maxUnitsPerTeam)
        {
            string teamName = (team == 1) ? "Joueur (Bleu)" : "Ennemi (Rouge)";
            ShowMessage($"Limite de {maxUnitsPerTeam} unités atteinte pour l'équipe {teamName} !", 3.0f);
            return null;
        }

        GameObject newUnitObj = null;

        if (type == UnitType.Fantassin)
        {
            // Chercher une unité vivante comme modèle ou charger le modèle neuf
            GameObject template = null;
            foreach (var u in FindObjectsByType<UnitAI>(FindObjectsInactive.Include))
            {
                if (!u.isTank && !u.isDead) { template = u.gameObject; break; }
            }
            if (template == null) template = GameObject.Find("Unite_1") ?? GameObject.Find("Unite_2");

            if (template != null)
            {
                newUnitObj = Instantiate(template, position, Quaternion.identity);
            }
            else
            {
                newUnitObj = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                newUnitObj.AddComponent<NavMeshAgent>();
                newUnitObj.AddComponent<UnitAI>();
            }
            newUnitObj.name = forcedName ?? $"Fantassin_{team}_{(teamCount + 1)}";
        }
        else if (type == UnitType.CharLeopard)
        {
            // 1. Charger en priorité absolue le prefab d'origine tout neuf depuis les Ressources
            GameObject prefab = Resources.Load<GameObject>("Kucher/Tank Leopard2/Prefabs/Leopard2");

            if (prefab != null)
            {
                newUnitObj = Instantiate(prefab, position, Quaternion.identity);
            }
            else
            {
                // Chercher un char vivant dans la scène
                GameObject tankTemplate = null;
                foreach (var u in FindObjectsByType<UnitAI>(FindObjectsInactive.Include))
                {
                    if (u.gameObject.name.ToLower().Contains("leopard") && !u.isDead)
                    {
                        tankTemplate = u.gameObject;
                        break;
                    }
                }
                if (tankTemplate == null) tankTemplate = GameObject.Find("Leopard2") ?? GameObject.Find("Leopard_1");

                if (tankTemplate != null) newUnitObj = Instantiate(tankTemplate, position, Quaternion.identity);
                else
                {
                    newUnitObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    newUnitObj.transform.localScale = new Vector3(3f, 2f, 5f);
                    newUnitObj.AddComponent<NavMeshAgent>();
                    newUnitObj.AddComponent<UnitAI>();
                }
            }
            newUnitObj.name = forcedName ?? $"Leopard2_{team}_{(teamCount + 1)}";
        }
        else if (type == UnitType.VehiculeCanon)
        {
            GameObject prefab = Resources.Load<GameObject>("engins/canon-vehicle");
            if (prefab != null)
            {
                newUnitObj = Instantiate(prefab, position, Quaternion.identity);
            }
            else
            {
                GameObject tankPrefab = Resources.Load<GameObject>("Kucher/Tank Leopard2/Prefabs/Leopard2");
                if (tankPrefab != null) newUnitObj = Instantiate(tankPrefab, position, Quaternion.identity);
                else
                {
                    newUnitObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    newUnitObj.transform.localScale = new Vector3(2.5f, 1.8f, 4.5f);
                    newUnitObj.AddComponent<NavMeshAgent>();
                    newUnitObj.AddComponent<UnitAI>();
                }
            }
            newUnitObj.name = forcedName ?? $"Canon_Vehicule_{team}_{(teamCount + 1)}";
        }
        else if (type == UnitType.Mortier)
        {
            GameObject turretPrefab = Resources.Load<GameObject>("lowpoly_turret");
            if (turretPrefab != null)
            {
                newUnitObj = Instantiate(turretPrefab, position, Quaternion.identity);
                newUnitObj.transform.localScale = Vector3.one * 1.6f;
            }
            else
            {
                newUnitObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                newUnitObj.transform.localScale = new Vector3(2f, 1.2f, 2f);
            }

            // Application immédiate de la texture gradientTexturelar.png
            Texture2D gradTex = Resources.Load<Texture2D>("gradientTexturelar");
            if (gradTex == null)
            {
                string texPath = Application.dataPath + "/gradientTexturelar.png";
                if (System.IO.File.Exists(texPath))
                {
                    byte[] rawData = System.IO.File.ReadAllBytes(texPath);
                    gradTex = new Texture2D(2, 2);
                    gradTex.LoadImage(rawData);
                }
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (gradTex != null && shader != null)
            {
                Material mat = new Material(shader);
                mat.mainTexture = gradTex;
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", gradTex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", gradTex);

                foreach (Renderer r in newUnitObj.GetComponentsInChildren<Renderer>())
                {
                    if (r.gameObject.name.Contains("Health") || r.gameObject.name.Contains("selectionRing")) continue;
                    r.sharedMaterial = mat;
                }
            }

            // Collider pour la sélection et clics
            BoxCollider box = newUnitObj.GetComponent<BoxCollider>();
            if (box == null) box = newUnitObj.AddComponent<BoxCollider>();
            box.center = new Vector3(0, 0.8f, 0);
            box.size = new Vector3(2.2f, 1.6f, 2.2f);

            NavMeshAgent agent = newUnitObj.GetComponent<NavMeshAgent>();
            if (agent == null) agent = newUnitObj.AddComponent<NavMeshAgent>();
            agent.speed = 2.5f;
            agent.angularSpeed = 180f;
            agent.acceleration = 8f;
            agent.radius = 1.1f;
            agent.height = 2f;
            agent.stoppingDistance = 0.5f;

            newUnitObj.name = forcedName ?? $"Mortier_{team}_{(teamCount + 1)}";
        }
        else if (type == UnitType.BarricadeRoutiere)
        {
            if (RemainingBarricadeStock(team) <= 0)
            {
                ShowMessage($"Stock de barricades épuisé ({maxBarricadesPerTeam} max par camp) !", 2.5f);
                return null;
            }

            GameObject barrierPrefab = Resources.Load<GameObject>("Road_barrier");
            if (barrierPrefab != null)
            {
                newUnitObj = Instantiate(barrierPrefab, position, Quaternion.identity);
                newUnitObj.transform.localScale = Vector3.one * 1.3f;
            }
            else
            {
                newUnitObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                newUnitObj.transform.localScale = new Vector3(3f, 1.2f, 0.8f);
            }

            RoadBarrier barrier = newUnitObj.GetComponent<RoadBarrier>();
            if (barrier == null) barrier = newUnitObj.AddComponent<RoadBarrier>();
            barrier.teamID = team;
            newUnitObj.name = forcedName ?? $"Barricade_{team}_{(RoadBarrier.AllBarriers.Count)}";

            // Son de pose de barricade — jamais sur le serveur headless (voir le garde équivalent
            // plus bas dans cette méthode : pas de device audio en mode Dedicated Server, chaque
            // AudioClip.Create()/SetData() y échoue avec "AudioClip contains no data", un warning
            // répété à CHAQUE unité/barricade déployée, sur TOUTES les parties, en continu — trouvé
            // en simulant une partie complète et en lisant les vrais logs serveur, 2026-08-30).
#if !UNITY_SERVER
            AudioClip clickClip = ProceduralAudioBuilder.CreateTargetConfirmedSound();
            if (clickClip != null) AudioSource.PlayClipAtPoint(clickClip, Camera.main.transform.position, 0.8f);
#endif
            ShowMessage($"Barricade routière déployée avec succès !", 2.0f);
            return null; // une barricade n'est pas une UnitAI
        }

        if (newUnitObj != null)
        {
            newUnitObj.SetActive(true);
            newUnitObj.transform.position = position;

            // Supprimer tout résidu de fumée ou particule de mort si le modèle a été cloné
            Transform residualSmoke = newUnitObj.transform.Find("BlackSmoke");
            if (residualSmoke != null) Destroy(residualSmoke.gameObject);
            foreach (var ps in newUnitObj.GetComponentsInChildren<ParticleSystem>()) Destroy(ps.gameObject);

            UnitAI unitAI = newUnitObj.GetComponent<UnitAI>();
            if (unitAI == null) unitAI = newUnitObj.AddComponent<UnitAI>();

            unitAI.teamID = team;
            // Mode solo strictement joueur (équipe 1) contre IA (équipe 2, voir TacticalAIPlanner).
            unitAI.isPlayerControlled = (team == 1);
            unitAI.teamAssignedBySpawner = true;
            unitAI.isDead = false;

            // Le Fantassin est cloné depuis une unité vivante existante (voir plus haut) pour
            // récupérer son rig/mesh — sans ce nettoyage, l'unité fraîchement déployée hérite de
            // l'état "monde" du modèle (à l'intérieur d'un bâtiment, planquée à une fenêtre,
            // garnison active), invisible à l'œil puisqu'elle apparaît bien dans la rue, mais qui
            // fausse silencieusement le menu contextuel, la réduction de dégâts de garnison et
            // l'angle de tir à la fenêtre pour la nouvelle unité.
            unitAI.currentBuilding = null;
            unitAI.currentWindow = null;
            unitAI.isGarrisoned = false;
            unitAI.isGuarding = false;
            unitAI.isCamouflaged = false;

            if (type == UnitType.CharLeopard)
            {
                unitAI.isTank = true;
                unitAI.maxHealth = 500f;
                unitAI.health = 500;
            }
            else if (type == UnitType.VehiculeCanon)
            {
                unitAI.isTank = true;
                unitAI.maxHealth = 250f;
                unitAI.health = 250;
            }
            else if (type == UnitType.Mortier)
            {
                unitAI.isTank = true;
                unitAI.isMortar = true;
                unitAI.porteeDetection = 120f;
                unitAI.maxHealth = 350f;
                unitAI.health = 350;
            }
            else
            {
                unitAI.isTank = false;
                unitAI.maxHealth = 100f;
                unitAI.health = 100;
            }

            // Réinitialisation de l'animateur pour le fantassin
            Animator anim = newUnitObj.GetComponentInChildren<Animator>();
            if (anim != null)
            {
                anim.Rebind();
                anim.Update(0f);
            }

            // Placement propre sur NavMesh — FindGroundLevelNavPoint (pas un simple
            // NavMesh.SamplePosition) : ce dernier renvoie le point de NavMesh le plus PROCHE, qui
            // peut être un toit de bâtiment praticable pour l'IA mais faux pour un spawn (voir le
            // doc-comment de FindGroundLevelNavPoint plus bas) — c'était la cause des unités
            // "posées sur des polygones, difficiles à manier" rapportée en jeu.
            Vector3 groundPos = FindGroundLevelNavPoint(position, 5f);
            if (NavMesh.SamplePosition(groundPos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
            {
                NavMeshAgent agent = newUnitObj.GetComponent<NavMeshAgent>();
                if (agent != null)
                {
                    agent.enabled = true;
                    agent.Warp(hit.position);
                    agent.isStopped = false;
                }
            }

            unitAI.ResetOrderState();
            unitAI.OnNavMeshReady();
            unitAI.SetupHealthBar();

            if (newUnitObj.GetComponent<FogOfWarEntity>() == null)
            {
                newUnitObj.AddComponent<FogOfWarEntity>();
            }

            // Son de confirmation de déploiement — jamais sur le serveur headless (voir commentaire
            // équivalent sur la branche Barricade plus haut : pas de device audio en Dedicated
            // Server, ce warning se répétait à CHAQUE unité déployée sur TOUTES les parties).
#if !UNITY_SERVER
            AudioClip confirmClip = ProceduralAudioBuilder.CreateTargetConfirmedSound();
            if (confirmClip != null) AudioSource.PlayClipAtPoint(confirmClip, Camera.main.transform.position, 0.8f);
#endif
            ShowMessage($"{newUnitObj.name} déployé avec succès !", 2.0f);
            return unitAI;
        }
        return null;
    }

    public void ClearAllUnits()
    {
        for (int i = UnitAI.AllLivingUnits.Count - 1; i >= 0; i--)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null) Destroy(u.gameObject);
        }
        UnitAI.AllLivingUnits.Clear();

        for (int i = RoadBarrier.AllBarriers.Count - 1; i >= 0; i--)
        {
            RoadBarrier b = RoadBarrier.AllBarriers[i];
            if (b != null) Destroy(b.gameObject);
        }
        RoadBarrier.AllBarriers.Clear();

        ShowMessage("Toutes les unités et barricades ont été retirées.", 2.0f);
    }

    public int GetTotalLivingUnitsCount()
    {
        int count = 0;
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null && !u.isDead) count++;
        }
        return count;
    }

    public int GetTeamLivingUnitsCount(int team)
    {
        int count = 0;
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null && !u.isDead && u.teamID == team) count++;
        }
        return count;
    }

    public int CountBarricadesForTeam(int team)
    {
        int count = 0;
        for (int i = 0; i < RoadBarrier.AllBarriers.Count; i++)
        {
            RoadBarrier b = RoadBarrier.AllBarriers[i];
            if (b != null && b.teamID == team) count++;
        }
        return count;
    }

    public int RemainingBarricadeStock(int team) => Mathf.Max(0, maxBarricadesPerTeam - CountBarricadesForTeam(team));

    /// <summary>
    /// Échantillonne plusieurs points de NavMesh autour de "desired" (le point visé lui-même, puis
    /// un anneau de points à distances/angles croissants) et retourne celui avec le Y le plus bas.
    /// Un simple NavMesh.SamplePosition() renvoie le point le plus proche, qui peut être un toit de
    /// bâtiment si celui-ci est géométriquement plus près du point visé que la rue — le toit est
    /// praticable pour l'IA (snipers), donc valide pour le NavMesh mais faux pour une zone de spawn.
    /// </summary>
    public static Vector3 FindGroundLevelNavPoint(Vector3 desired, float searchRadius)
    {
        var candidates = new List<Vector3> { desired };
        const int ringSteps = 8;
        for (int ring = 1; ring <= 3; ring++)
        {
            float radius = searchRadius * ring / 3f;
            for (int i = 0; i < ringSteps; i++)
            {
                float angle = i * (360f / ringSteps) * Mathf.Deg2Rad;
                candidates.Add(desired + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius));
            }
        }

        Vector3 best = desired;
        float bestY = float.MaxValue;
        bool found = false;

        foreach (var candidate in candidates)
        {
            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, searchRadius, NavMesh.AllAreas))
            {
                if (!found || hit.position.y < bestY)
                {
                    bestY = hit.position.y;
                    best = hit.position;
                    found = true;
                }
            }
        }

        return found ? best : desired;
    }

    /// <summary>
    /// Déploie instantanément une escouade ennemie IA (Fantassins, Char, Mortier) sur les routes.
    /// </summary>
    public void SpawnEnemyWave()
    {
        Vector3 enemyBase = FindGroundLevelNavPoint(new Vector3(25f, 0f, 25f), 40f);

        SpawnUnitAt(UnitType.Fantassin, enemyBase + new Vector3(-3f, 0, 3f), 2);
        SpawnUnitAt(UnitType.Fantassin, enemyBase + new Vector3(3f, 0, -3f), 2);
        SpawnUnitAt(UnitType.CharLeopard, enemyBase + new Vector3(6f, 0, 4f), 2);
        SpawnUnitAt(UnitType.Mortier, enemyBase + new Vector3(-6f, 0, 5f), 2);
        SpawnUnitAt(UnitType.BarricadeRoutiere, enemyBase + new Vector3(0f, 0, -8f), 2);

        ShowMessage("[IA] Escouade ennemie complète déployée sur le champ de bataille !", 3.5f);
        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
    }

    /// <summary>
    /// Déploie automatiquement les deux camps (Joueur + IA) pour lancer la bataille.
    /// </summary>
    public void AutoDeployBattlefield()
    {
        // Camp Joueur (Sud-Ouest)
        Vector3 playerPos = FindGroundLevelNavPoint(new Vector3(-25f, 0f, -25f), 40f);

        if (GetTeamLivingUnitsCount(1) == 0)
        {
            SpawnUnitAt(UnitType.Fantassin, playerPos + new Vector3(-2f, 0, -2f), 1);
            SpawnUnitAt(UnitType.Fantassin, playerPos + new Vector3(2f, 0, 2f), 1);
            SpawnUnitAt(UnitType.CharLeopard, playerPos + new Vector3(5f, 0, -3f), 1);
            SpawnUnitAt(UnitType.Mortier, playerPos + new Vector3(-5f, 0, -4f), 1);
        }

        // Camp Ennemi IA (Nord-Est)
        SpawnEnemyWave();
        ShowMessage("Champ de bataille prêt : Escouades Joueur & IA déployées !", 3.5f);
    }

    private void ShowMessage(string msg, float duration)
    {
        statusMessage = msg;
        statusMessageTimer = duration;
    }

    // Mêmes points d'ancrage que AutoDeployBattlefield/SpawnEnemyWave, mais scopés à un seul camp —
    // utilisé par MatchSessionManager.ResolveDeployment comme repli serveur si un joueur n'a pas
    // soumis de placement manuel valide avant l'expiration du timer de déploiement (voir
    // 03-network-protocol.md, "submit_deployment"/"deployment_result").
    public void AutoDeployTeamFallback(int team, int extraInfantry = 0)
    {
        Vector3 anchor = FindGroundLevelNavPoint(team == 1 ? new Vector3(-25f, 0f, -25f) : new Vector3(25f, 0f, 25f), 40f);
        if (team == 1)
        {
            SpawnUnitAt(UnitType.Fantassin, anchor + new Vector3(-2f, 0, -2f), 1);
            SpawnUnitAt(UnitType.Fantassin, anchor + new Vector3(2f, 0, 2f), 1);
            SpawnUnitAt(UnitType.CharLeopard, anchor + new Vector3(5f, 0, -3f), 1);
            SpawnUnitAt(UnitType.Mortier, anchor + new Vector3(-5f, 0, -4f), 1);
        }
        else
        {
            SpawnUnitAt(UnitType.Fantassin, anchor + new Vector3(-3f, 0, 3f), 2);
            SpawnUnitAt(UnitType.Fantassin, anchor + new Vector3(3f, 0, -3f), 2);
            SpawnUnitAt(UnitType.CharLeopard, anchor + new Vector3(6f, 0, 4f), 2);
            SpawnUnitAt(UnitType.Mortier, anchor + new Vector3(-6f, 0, 5f), 2);
        }

        // Renfort de garnison (conquête, voir MatchSessionManager.GarrisonExtraInfantryForZoneCount) :
        // fantassins supplémentaires disposés en éventail autour de l'ancrage, à un rayon plus large
        // que l'escouade de base ci-dessus pour ne jamais se superposer avec elle.
        for (int i = 0; i < extraInfantry; i++)
        {
            float angle = i * 47f; // pas non-régulier : évite un alignement visuel trop mécanique
            Vector3 offset = Quaternion.Euler(0f, angle, 0f) * new Vector3(8f, 0f, 0f);
            SpawnUnitAt(UnitType.Fantassin, anchor + offset, team);
        }
    }

    /// <summary>Appelé par MultiplayerMatchController au tout début de la phase de déploiement PvP :
    /// ouvre directement le dock (pas besoin de taper sur l'onglet DÉPLOIEMENT) et verrouille
    /// l'équipe sur celle du joueur local — impossible pour un client de placer des unités pour
    /// l'équipe adverse depuis son propre appareil (le serveur ignorerait de toute façon toute
    /// unité hors de la zone/de l'effectif autorisés pour ce camp, voir MatchSessionManager).</summary>
    public void OpenDockForMultiplayerDeployment(int team)
    {
        selectedTeam = team;
        isPanelOpen = true;
        CancelPlacement();
    }

#if !UNITY_SERVER
    private VisualElement dockPanel;
    private Button tabButton, team1Button, team2Button;
    private Button aiSquadButton, autoDeployButton, mpConfirmButton;
    private Label effectifsLabel, placingBanner, statusMessageLabel, tabButtonLabel;
    private bool deploymentUiBound = false;

    private void BindDeploymentUI()
    {
        if (UIScreenManager.Instance == null)
        {
            Debug.LogError("[UnitSpawnerUI] UIScreenManager.Instance introuvable — UIBootstrap ne s'est-il pas exécuté avant cette scène ?");
            return;
        }

        // Protégé par un try/catch (comme TacticalPathManager.BindTacticalUI) : sans lui, un seul
        // élément introuvable dans le UXML (ex: tout juste ajouté, Éditeur pas encore réimporté)
        // levait une exception qui coupait net TOUS les bindings suivants dans cette méthode —
        // Fantassin/Char/Canon/Mortier/Barricade compris, pas seulement le bouton concerné.
        try
        {
            VisualElement root = UIScreenManager.Instance.GetScreen("DeploymentDock");
            tabButton = root.Q<Button>("tab-button");
            tabButtonLabel = root.Q<Label>("tab-button-label");
            if (tabButtonLabel == null)
            {
                // N'interrompt PAS le binding (contrairement à un null-deref immédiat) — mais sans
                // ce garde-fou, RefreshDeploymentDockUI() (jamais protégée par un try/catch, appelée
                // chaque frame depuis Update) plantait silencieusement sur tabButtonLabel.text à
                // CHAQUE frame dès que ce champ était introuvable, coupant tout le reste de la
                // méthode (dockPanel, effectifs, bannière...) — pas seulement l'étiquette du bouton.
                Debug.LogError("[UnitSpawnerUI] Label 'tab-button-label' introuvable dans DeploymentDockScreen.uxml (le bouton DÉPLOIEMENT ne pourra pas afficher son texte) — relance l'Éditeur/Play Mode pour forcer une réimportation du UXML.");
            }
            dockPanel = root.Q<VisualElement>("dock-panel");
            effectifsLabel = root.Q<Label>("effectifs-label");
            team1Button = root.Q<Button>("team1-button");
            team2Button = root.Q<Button>("team2-button");
            placingBanner = root.Q<Label>("placing-banner");
            statusMessageLabel = root.Q<Label>("status-message");

            tabButton.clicked += () =>
            {
                lastUIClickTime = Time.time;
                if (IsPlacingUnit)
                {
                    CancelPlacement();
                    ShowMessage("Placement annulé.", 1.5f);
                }
                else
                {
                    isPanelOpen = !isPanelOpen;
                }
                // Protégé (voir team1Button/team2Button/etc. plus bas, non protégés) : un
                // Camera.main introuvable (ex: transition de caméra en cours) ou un souci
                // d'initialisation audio ne doit jamais empêcher le dock de s'ouvrir/fermer.
                try
                {
                    AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
                }
                catch (System.Exception) { }
            };

            team1Button.clicked += () => { lastUIClickTime = Time.time; selectedTeam = 1; AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position); };
            team2Button.clicked += () => { lastUIClickTime = Time.time; selectedTeam = 2; AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position); };

            root.Q<Button>("btn-fantassin").clicked += () => { lastUIClickTime = Time.time; StartPlacingUnit(UnitType.Fantassin); };
            root.Q<Button>("btn-leopard").clicked += () => { lastUIClickTime = Time.time; StartPlacingUnit(UnitType.CharLeopard); };
            root.Q<Button>("btn-canon").clicked += () => { lastUIClickTime = Time.time; StartPlacingUnit(UnitType.VehiculeCanon); };
            root.Q<Button>("btn-mortier").clicked += () => { lastUIClickTime = Time.time; StartPlacingUnit(UnitType.Mortier); };
            root.Q<Button>("btn-barricade").clicked += () => { lastUIClickTime = Time.time; StartPlacingUnit(UnitType.BarricadeRoutiere); };
            aiSquadButton = root.Q<Button>("btn-ai-squad");
            aiSquadButton.clicked += () => { lastUIClickTime = Time.time; SpawnEnemyWave(); };
            autoDeployButton = root.Q<Button>("btn-auto-deploy");
            autoDeployButton.clicked += () => { lastUIClickTime = Time.time; AutoDeployBattlefield(); };
            root.Q<Button>("btn-clear").clicked += () => { lastUIClickTime = Time.time; ClearAllUnits(); };

            // Multijoueur uniquement (voir OpenDockForMultiplayerDeployment) : bouton ajouté dans
            // DeploymentDockScreen.uxml, caché par défaut (display:none), affiché UNIQUEMENT pendant
            // la phase de déploiement PvP (voir RefreshDeploymentDockUI) — btn-ai-squad/btn-auto-
            // deploy n'ont pas de sens en PvP (ils manipuleraient le camp adverse depuis mon propre
            // appareil) et sont donc masqués à la place pendant cette même phase.
            mpConfirmButton = root.Q<Button>("btn-mp-confirm");
            if (mpConfirmButton != null)
            {
                mpConfirmButton.clicked += () =>
                {
                    lastUIClickTime = Time.time;
                    Novgov.Network.MultiplayerMatchController.Instance?.SubmitLocalDeployment();
                };
            }
            else
            {
                Debug.LogError("[UnitSpawnerUI] Bouton 'btn-mp-confirm' introuvable dans DeploymentDockScreen.uxml — la confirmation de déploiement multijoueur restera inopérante.");
            }

            // Pas de SetVisible(true) ici : RefreshDeploymentDockUI() (appelé chaque frame depuis
            // Update) décide seul de la visibilité dès la première frame, startup menu inclus.
            deploymentUiBound = true;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[UnitSpawnerUI] BindDeploymentUI() a levé une exception : {ex.GetType().Name} — {ex.Message}");
        }
    }

    /// <summary>Remplace l'ancien OnGUI() — met à jour l'affichage sans reconstruire l'UI chaque frame.</summary>
    private void RefreshDeploymentDockUI()
    {
        if (!deploymentUiBound) return;

        // Garde-fou : un rechargement de scripts survenu pendant que le Play Mode tourne encore
        // réinitialise les champs statiques (dont UIScreenManager.Instance) sans redéclencher
        // UIBootstrap.Init() (BeforeSceneLoad ne se relance pas en cours de session) — cette
        // instance de UnitSpawnerUI, elle, survit et planterait sinon ici à chaque frame.
        if (UIScreenManager.Instance == null)
        {
            deploymentUiBound = false;
            return;
        }

        // Masqué en vue 3D Action, pendant l'exécution du tour, et pendant tout écran multijoueur
        // SAUF la phase de déploiement PvP elle-même (voir OpenDockForMultiplayerDeployment/
        // MultiplayerMatchController.IsDeploymentPhaseActive) — login/mode/matchmaking/HUD/fin de
        // partie restent masqués comme avant, seule cette phase précise réutilise ce même dock,
        // verrouillé sur le camp local (voir plus bas).
        bool hidden = (CameraStateManager.Instance != null && CameraStateManager.Instance.CurrentState == CameraStateManager.CameraState.Action);
        TacticalPathManager pathManager = TacticalPathManager.Instance;
        if (pathManager != null && pathManager.phaseActuelle == TacticalPathManager.GamePhase.Execution) hidden = true;
        bool mpDeployment = Novgov.Network.MultiplayerMatchController.IsDeploymentPhaseActive;
        if (Novgov.Network.MultiplayerMatchController.IsFlowActive && !mpDeployment) hidden = true;
        // Le menu de démarrage (choix de carte hors-ligne/GPS/multijoueur) n'a encore chargé aucune
        // carte ni unité — sans ce garde-fou, ce dock plein écran (même vide) reste au-dessus du
        // menu de démarrage dans l'arbre UI Toolkit et intercepte silencieusement tous les taps
        // destinés à ses boutons.
        if (GameManagerUI.Instance != null && GameManagerUI.Instance.IsStartupSelectionActive) hidden = true;
        if (TacticalPathManager.IsSoloGameOver) hidden = true;

        UIScreenManager.Instance.SetVisible("DeploymentDock", !hidden);

        // Un écart entre la position visuelle du tab-button et sa vraie zone cliquable (worldBound)
        // a été observé sur un émulateur Android 16 pendant les tests du 2026-08-31, non reproduit
        // sur appareil réel — probablement un artefact du rendu logiciel de l'émulateur plutôt qu'un
        // vrai bug du jeu. Ce MarkDirtyRepaint() sur transition (jamais chaque frame) ne coûte rien
        // et sert de filet de sécurité au cas où un appareil réel présenterait un jour un symptôme
        // similaire (ex: après un redimensionnement de fenêtre en mode multi-fenêtré Android).
        bool justShown = !hidden && dockShownLastFrame == false;
        dockShownLastFrame = !hidden;
        if (hidden)
        {
            dockPanelOpenLastFrame = false; // repartira à zéro à la prochaine ouverture
            return;
        }
        bool panelToggled = isPanelOpen != dockPanelOpenLastFrame;
        dockPanelOpenLastFrame = isPanelOpen;
        if (justShown || panelToggled)
        {
            tabButton.MarkDirtyRepaint();
            VisualElement wrapper = dockPanel.parent;
            wrapper?.MarkDirtyRepaint();
            UIScreenManager.Instance.RootVisualElement.MarkDirtyRepaint();
        }

        // Verrouillage du choix de camp + boutons solo-only pendant le déploiement PvP : impossible
        // de basculer sur le camp adverse, et ESCOUADE IA/DÉPLOIEMENT AUTO n'ont pas de sens ici
        // (ils manipuleraient l'équipe adverse depuis mon propre appareil) — remplacés par
        // CONFIRMER LE DÉPLOIEMENT (voir mpConfirmButton, câblé dans BindDeploymentUI).
        team1Button.style.display = mpDeployment ? DisplayStyle.None : DisplayStyle.Flex;
        team2Button.style.display = mpDeployment ? DisplayStyle.None : DisplayStyle.Flex;
        if (aiSquadButton != null) aiSquadButton.style.display = mpDeployment ? DisplayStyle.None : DisplayStyle.Flex;
        if (autoDeployButton != null) autoDeployButton.style.display = mpDeployment ? DisplayStyle.None : DisplayStyle.Flex;
        if (mpConfirmButton != null) mpConfirmButton.style.display = mpDeployment ? DisplayStyle.Flex : DisplayStyle.None;

        int playerUnits = GetTeamLivingUnitsCount(1);
        int enemyUnits = GetTeamLivingUnitsCount(2);

        if (tabButtonLabel != null)
        {
            // "X vs Y" a du sens en Solo (comparer son escouade à celle, fixe, de l'IA) mais pas en
            // multijoueur PvP : "enemyUnits" y vaut certes 0 pendant le déploiement depuis le
            // brouillard de guerre réseau (l'adversaire n'existe pas encore côté client, voir
            // MatchSessionManager.ComputeVisibleUnitIds), mais afficher "0" prête à confusion ("j'ai
            // déjà gagné ?") plutôt que de simplement ne rien dire sur un camp qu'on ne peut pas voir.
            bool isMultiplayer = Novgov.Network.MultiplayerMatchController.IsFlowActive;
            string label = isMultiplayer ? $"DÉPLOIEMENT ({playerUnits}/{maxUnitsPerTeam})" : $"DÉPLOIEMENT ({playerUnits} vs {enemyUnits})";
            tabButtonLabel.text = IsPlacingUnit ? "Annuler Placement" : (isPanelOpen ? "Fermer Menu" : label);
        }
        dockPanel.style.display = isPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;

        if (isPanelOpen)
        {
            int currentTeamCount = (selectedTeam == 1) ? playerUnits : enemyUnits;
            effectifsLabel.text = $"Effectifs : {currentTeamCount} / {maxUnitsPerTeam}";
            effectifsLabel.style.color = new StyleColor(currentTeamCount >= maxUnitsPerTeam ? NovgovTheme.Danger : NovgovTheme.Info);

            team1Button.text = $"Joueur ({playerUnits})";
            team2Button.text = $"IA ({enemyUnits})";
            team1Button.EnableInClassList("dock-team-btn--active-p1", selectedTeam == 1);
            team2Button.EnableInClassList("dock-team-btn--active-p2", selectedTeam == 2);
        }

        if (pendingBarricadeExtension != null)
        {
            // Le menu DÉPLOYER/ANNULER de TacticalPathManager (voir ShowBarricadeExtensionMenu)
            // communique déjà l'état en attente — cette bannière ferait doublon par-dessus.
            placingBanner.style.display = DisplayStyle.None;
        }
        else if (IsPlacingUnit && activePlacingType.HasValue)
        {
            placingBanner.text = (activePlacingType.Value == UnitType.BarricadeRoutiere)
                ? $"MODE PLACEMENT : Barricade ({RemainingBarricadeStock(selectedTeam)} restantes)\n[Touchez la rue] Poser | [Annuler]"
                : $"MODE PLACEMENT : {activePlacingType.Value}\n[Touchez la rue] Poser | [Annuler]";
            placingBanner.style.color = new StyleColor(selectedTeam == 1 ? NovgovTheme.TeamPlayer : NovgovTheme.TeamEnemy);
            placingBanner.style.display = DisplayStyle.Flex;
        }
        else
        {
            placingBanner.style.display = DisplayStyle.None;
        }

        if (!string.IsNullOrEmpty(statusMessage))
        {
            statusMessageLabel.text = statusMessage;
            statusMessageLabel.style.display = DisplayStyle.Flex;
        }
        else
        {
            statusMessageLabel.style.display = DisplayStyle.None;
        }
    }

#endif

    /// <summary>
    /// Remplace l'ancien test par Rect codées en dur : s'appuie sur le picking natif d'UI Toolkit,
    /// donc reste correct même si la mise en page de l'UI change.
    /// Déclarée EN DEHORS du bloc #if !UNITY_SERVER (contrairement au reste du binding UI ci-
    /// dessus) : elle est appelée depuis TacticalCamera.cs et TacticalPathManager_Input.cs sans
    /// garde de compilation — un build où UNITY_SERVER est défini (ex: sous-cible Dedicated
    /// Server active par erreur, voir ServerBuildScript.cs) faisait alors échouer TOUTE la
    /// compilation avec "IsPointerOverOnGUI n'existe pas", pas seulement empêcher ce test de
    /// fonctionner. Le corps reste conditionnel : côté serveur, il n'y a de toute façon aucun
    /// UIDocument/panel UI Toolkit à interroger, donc "jamais sur l'UI" est la bonne réponse.
    /// </summary>
    public bool IsPointerOverOnGUI(Vector2 screenPos)
    {
#if UNITY_SERVER
        return false;
#else
        if (Time.time - lastUIClickTime < 0.3f) return true;

        // 1. Protection du radar tactique (TacticalRadarUI, dessiné en OnGUI — donc invisible au
        // picking UI Toolkit du bloc 2 ci-dessous, qui ne connaît que les éléments UI Toolkit).
        // BottomEdgeScreenY reflète la géométrie RÉELLE du radar (0 quand il est masqué, ex: vue 3D
        // Action) — un calcul de "facteur d'échelle" approximatif ici avait dérivé après un
        // rechargement de script en cours de Play et bloquait des clics qui marchaient l'instant
        // d'avant, sur un rectangle de zone qui ne correspondait plus à rien de réel à l'écran.
        if (TacticalRadarUI.BottomEdgeScreenY > 0f && screenPos.y >= Screen.height - TacticalRadarUI.BottomEdgeScreenY - 8f)
        {
            float radarLeftX = Screen.width - TacticalRadarUI.BottomEdgeScreenY; // le radar est ~carré, ancré en haut-droite
            if (screenPos.x >= radarLeftX - 8f) return true;
        }

        // 2. UI Toolkit picking générique pour tous les autres éléments (barre du bas, dock de
        // déploiement, menu contextuel, boutons modaux, etc.) — ce sont tous de vrais éléments UI
        // Toolkit, donc panel.Pick() les détecte correctement sans avoir besoin de zones codées en
        // dur par écran. Chaque écran a son propre picking-mode forcé à Ignore en C# sur son
        // élément "root" (voir UIScreenManager.Awake()), donc le picking ne s'arrête que sur de
        // vrais contrôles interactifs (boutons, etc.), jamais sur un simple conteneur de mise en page.
        if (UIScreenManager.Instance == null) return false;
        var uiDoc = UIScreenManager.Instance.GetComponent<UIDocument>();
        if (uiDoc == null || uiDoc.rootVisualElement?.panel == null) return false;

        Vector2 panelPos = RuntimePanelUtils.ScreenToPanel(uiDoc.rootVisualElement.panel, screenPos);
        VisualElement picked = uiDoc.rootVisualElement.panel.Pick(panelPos);

        while (picked != null)
        {
            if (picked == uiDoc.rootVisualElement) break;

            // Si on touche n'importe quel élément actif ou interactif
            if (picked.pickingMode == PickingMode.Position) return true;
            if (picked is Button || picked is ScrollView || picked is TextField || picked is Label) return true;
            if (picked.ClassListContains("context-panel") || picked.ClassListContains("dock-panel") || picked.ClassListContains("context-button") || picked.ClassListContains("context-cancel-btn")) return true;

            picked = picked.parent;
        }
        return false;
#endif
    }
}
