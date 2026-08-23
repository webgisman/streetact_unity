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

    // État du Drag & Drop / Placement
    public static bool IsPlacingUnit = false;
    private UnitType? activePlacingType = null;
    private GameObject previewRing;
    private Material previewMat;

    // UI State
    private bool isPanelOpen = false; // Fermé par défaut pour libérer l'écran
    private string statusMessage = "";
    private float statusMessageTimer = 0f;
    private float ignorePlacementTime = 0f;
    public static float lastUIClickTime = 0f;

    public bool IsPanelOpen()
    {
        return isPanelOpen;
    }

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

        StartCoroutine(AutoSpawnInitialUnitsRoutine());
    }

    private System.Collections.IEnumerator AutoSpawnInitialUnitsRoutine()
    {
        yield return new WaitForSeconds(0.8f);
        if (GetTotalLivingUnitsCount() == 0)
        {
            AutoDeployBattlefield();
        }
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
                        // Ignorer les clics sur le bouton d'annulation en haut à gauche
                        Vector2 guiPos = new Vector2(pointerPos.x, Screen.height - pointerPos.y);
                        float uiScale = Mathf.Clamp(Screen.width / 480f, 1.35f, 2.2f);
                        Rect cancelBtnRect = new Rect(15 * uiScale, 15 * uiScale, 230 * uiScale, 44 * uiScale);
                        
                        if (!cancelBtnRect.Contains(guiPos))
                        {
                            if (tappedOnExistingUnit)
                            {
                                // On ne redéploie jamais une unité par-dessus une autre déjà posée :
                                // on annule le placement pour que le prochain tap serve à la sélectionner.
                                CancelPlacement();
                                ShowMessage("Emplacement occupé — placement annulé. Retape sur l'unité pour la sélectionner.", 2.5f);
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
                                    ? "⚠️ Les véhicules et canons doivent être placés sur la rue, pas sur les toits !"
                                    : "Emplacement hors-carte ! Touchez une rue pour déployer l'unité.";
                                ShowMessage(errMsg, 2.5f);
                            }
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

    public void StartPlacingUnit(UnitType type)
    {
        int teamCount = GetTeamLivingUnitsCount(selectedTeam);
        if (teamCount >= maxUnitsPerTeam)
        {
            string teamName = (selectedTeam == 1) ? "Joueur (Bleu)" : "Ennemi (Rouge)";
            ShowMessage($"Limite atteinte pour l'équipe {teamName} ({maxUnitsPerTeam} unités max par camp) !", 3.0f);
            return;
        }

        activePlacingType = type;
        IsPlacingUnit = true;
        isPanelOpen = false; // Ferme le dock pour libérer tout l'écran tactile
        ignorePlacementTime = Time.time + 0.35f; // Délai anti-misfire
        isPlacementPointerDown = false;
        
        string unitName = (type == UnitType.CharLeopard) ? "Char Leopard 2" : (type == UnitType.VehiculeCanon ? "Véhicule Canon" : (type == UnitType.Mortier ? "Mortier" : "Fantassin"));
        ShowMessage($"📍 Touchez une rue pour déployer : {unitName}", 4.0f);
        AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateHoverSound(), Camera.main.transform.position);
    }

    public void CancelPlacement()
    {
        IsPlacingUnit = false;
        activePlacingType = null;
        if (previewRing != null) previewRing.SetActive(false);
    }

    public void SpawnUnitAt(UnitType type, Vector3 position, int team)
    {
        int teamCount = GetTeamLivingUnitsCount(team);
        if (teamCount >= maxUnitsPerTeam)
        {
            string teamName = (team == 1) ? "Joueur (Bleu)" : "Ennemi (Rouge)";
            ShowMessage($"Limite de {maxUnitsPerTeam} unités atteinte pour l'équipe {teamName} !", 3.0f);
            return;
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
            newUnitObj.name = $"Fantassin_{team}_{(teamCount + 1)}";
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
            newUnitObj.name = $"Leopard2_{team}_{(teamCount + 1)}";
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
            newUnitObj.name = $"Canon_Vehicule_{team}_{(teamCount + 1)}";
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

            newUnitObj.name = $"Mortier_{team}_{(teamCount + 1)}";
        }
        else if (type == UnitType.BarricadeRoutiere)
        {
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
            newUnitObj.name = $"Barricade_{team}_{(RoadBarrier.AllBarriers.Count)}";

            // Son de pose de barricade
            AudioClip clickClip = ProceduralAudioBuilder.CreateTargetConfirmedSound();
            if (clickClip != null) AudioSource.PlayClipAtPoint(clickClip, Camera.main.transform.position, 0.8f);
            ShowMessage($"🚧 Barricade routière déployée avec succès !", 2.0f);
            return;
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
            unitAI.isPlayerControlled = (team == 1);
            unitAI.isDead = false;

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

            // Placement propre sur NavMesh
            if (NavMesh.SamplePosition(position, out NavMeshHit hit, 5f, NavMesh.AllAreas))
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

            // Son de confirmation de déploiement
            AudioClip confirmClip = ProceduralAudioBuilder.CreateTargetConfirmedSound();
            if (confirmClip != null) AudioSource.PlayClipAtPoint(confirmClip, Camera.main.transform.position, 0.8f);
            ShowMessage($"✅ {newUnitObj.name} déployé avec succès !", 2.0f);
        }
    }

    public void ClearAllUnits()
    {
        for (int i = UnitAI.AllLivingUnits.Count - 1; i >= 0; i--)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null) Destroy(u.gameObject);
        }

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

        ShowMessage("🤖 [IA] Escouade ennemie complète déployée sur le champ de bataille !", 3.5f);
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
        ShowMessage("⚡ Champ de bataille prêt : Escouades Joueur & IA déployées !", 3.5f);
    }

    private void ShowMessage(string msg, float duration)
    {
        statusMessage = msg;
        statusMessageTimer = duration;
    }

#if !UNITY_SERVER
    private VisualElement dockPanel;
    private Button tabButton, team1Button, team2Button;
    private Label effectifsLabel, placingBanner, statusMessageLabel;
    private bool deploymentUiBound = false;

    private void BindDeploymentUI()
    {
        if (UIScreenManager.Instance == null)
        {
            Debug.LogError("[UnitSpawnerUI] UIScreenManager.Instance introuvable — UIBootstrap ne s'est-il pas exécuté avant cette scène ?");
            return;
        }
        deploymentUiBound = true;

        VisualElement root = UIScreenManager.Instance.GetScreen("DeploymentDock");
        tabButton = root.Q<Button>("tab-button");
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
            AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
        };

        team1Button.clicked += () => { lastUIClickTime = Time.time; selectedTeam = 1; AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position); };
        team2Button.clicked += () => { lastUIClickTime = Time.time; selectedTeam = 2; AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position); };

        root.Q<Button>("btn-fantassin").clicked += () => { lastUIClickTime = Time.time; StartPlacingUnit(UnitType.Fantassin); };
        root.Q<Button>("btn-leopard").clicked += () => { lastUIClickTime = Time.time; StartPlacingUnit(UnitType.CharLeopard); };
        root.Q<Button>("btn-canon").clicked += () => { lastUIClickTime = Time.time; StartPlacingUnit(UnitType.VehiculeCanon); };
        root.Q<Button>("btn-mortier").clicked += () => { lastUIClickTime = Time.time; StartPlacingUnit(UnitType.Mortier); };
        root.Q<Button>("btn-barricade").clicked += () => { lastUIClickTime = Time.time; StartPlacingUnit(UnitType.BarricadeRoutiere); };
        root.Q<Button>("btn-ai-squad").clicked += () => { lastUIClickTime = Time.time; SpawnEnemyWave(); };
        root.Q<Button>("btn-auto-deploy").clicked += () => { lastUIClickTime = Time.time; AutoDeployBattlefield(); };
        root.Q<Button>("btn-clear").clicked += () => { lastUIClickTime = Time.time; ClearAllUnits(); };

        // Pas de SetVisible(true) ici : RefreshDeploymentDockUI() (appelé chaque frame depuis
        // Update) décide seul de la visibilité dès la première frame, startup menu inclus.
    }

    /// <summary>Remplace l'ancien OnGUI() — met à jour l'affichage sans reconstruire l'UI chaque frame.</summary>
    private void RefreshDeploymentDockUI()
    {
        if (!deploymentUiBound) return;

        // Masqué en vue 3D Action, pendant l'exécution du tour, et pendant tout écran multijoueur
        // (login/mode/matchmaking/HUD/fin de partie — le dock de déploiement manuel n'a pas de sens
        // en PvP, les unités y sont toujours auto-déployées, voir MultiplayerMatchController).
        bool hidden = (CameraStateManager.Instance != null && CameraStateManager.Instance.CurrentState == CameraStateManager.CameraState.Action);
        TacticalPathManager pathManager = TacticalPathManager.Instance;
        if (pathManager != null && pathManager.phaseActuelle == TacticalPathManager.GamePhase.Execution) hidden = true;
        if (Novgov.Network.MultiplayerMatchController.IsFlowActive) hidden = true;
        // Le menu de démarrage (choix de carte hors-ligne/GPS/multijoueur) n'a encore chargé aucune
        // carte ni unité — sans ce garde-fou, ce dock plein écran (même vide) reste au-dessus du
        // menu de démarrage dans l'arbre UI Toolkit et intercepte silencieusement tous les taps
        // destinés à ses boutons.
        if (GameManagerUI.Instance != null && GameManagerUI.Instance.IsStartupSelectionActive) hidden = true;

        UIScreenManager.Instance.SetVisible("DeploymentDock", !hidden);
        if (hidden) return;

        int playerUnits = GetTeamLivingUnitsCount(1);
        int enemyUnits = GetTeamLivingUnitsCount(2);

        tabButton.text = IsPlacingUnit ? "❌ Annuler Placement" : (isPanelOpen ? "▲ Fermer Menu" : $"🎖️ Déploiement ({playerUnits} vs {enemyUnits})");
        dockPanel.style.display = isPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;

        if (isPanelOpen)
        {
            int currentTeamCount = (selectedTeam == 1) ? playerUnits : enemyUnits;
            effectifsLabel.text = $"Effectifs : {currentTeamCount} / {maxUnitsPerTeam}";
            effectifsLabel.style.color = new StyleColor(currentTeamCount >= maxUnitsPerTeam ? Color.red : new Color(0.15f, 0.85f, 1f));

            team1Button.text = (selectedTeam == 1) ? $"🔵 Joueur ({playerUnits})" : $"Joueur ({playerUnits})";
            team2Button.text = (selectedTeam == 2) ? $"🔴 IA ({enemyUnits})" : $"IA ({enemyUnits})";
            team1Button.EnableInClassList("dock-team-btn--active-p1", selectedTeam == 1);
            team2Button.EnableInClassList("dock-team-btn--active-p2", selectedTeam == 2);
        }

        if (IsPlacingUnit && activePlacingType.HasValue)
        {
            placingBanner.text = $"MODE PLACEMENT : {activePlacingType.Value}\n[Touchez la rue] Poser | [Annuler]";
            placingBanner.style.color = new StyleColor(selectedTeam == 1 ? new Color(0.15f, 0.85f, 1f) : Color.red);
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

    /// <summary>
    /// Remplace l'ancien test par Rect codées en dur : s'appuie sur le picking natif d'UI Toolkit,
    /// donc reste correct même si la mise en page de l'UI change.
    /// </summary>
    public bool IsPointerOverOnGUI(Vector2 screenPos)
    {
        if (Time.time - lastUIClickTime < 0.4f) return true;

        if (UIScreenManager.Instance == null) return false;
        var uiDoc = UIScreenManager.Instance.GetComponent<UIDocument>();
        if (uiDoc == null || uiDoc.rootVisualElement?.panel == null) return false;

        // panel.Pick() fait un test géométrique pur et ignore picking-mode: Ignore — un simple
        // conteneur de mise en page plein écran (flex-grow: 1, sans classe, non interactif) est
        // donc renvoyé même là où il n'y a visuellement rien. On ne bloque le tap que si
        // l'élément touché est un vrai contrôle interactif (bouton), pas un conteneur vide.
        Vector2 panelPos = RuntimePanelUtils.ScreenToPanel(uiDoc.rootVisualElement.panel, screenPos);
        VisualElement picked = uiDoc.rootVisualElement.panel.Pick(panelPos);
        return picked is Button;
    }
#endif
}
