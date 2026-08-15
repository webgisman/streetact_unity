using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

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
    private bool isPanelOpen = true;
    private string statusMessage = "";
    private float statusMessageTimer = 0f;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        // Création de l'anneau holographique de prévisualisation au sol
        previewRing = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        previewRing.name = "DeploymentPreviewRing";
        Destroy(previewRing.GetComponent<Collider>());
        previewRing.transform.localScale = new Vector3(2.0f, 0.02f, 2.0f);

        Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Transparent");
        previewMat = new Material(unlit);
        previewMat.SetColor("_BaseColor", new Color(0f, 1f, 0.4f, 0.6f));
        if (previewMat.HasProperty("_Color")) previewMat.SetColor("_Color", new Color(0f, 1f, 0.4f, 0.6f));
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
    }

    private void HandlePlacementPreview()
    {
        Vector2 pointerPos = Vector2.zero;
        bool hasPointer = false;

        if (Pointer.current != null)
        {
            pointerPos = Pointer.current.position.ReadValue();
            hasPointer = true;
        }
        else if (Input.touchCount > 0)
        {
            pointerPos = Input.GetTouch(0).position;
            hasPointer = true;
        }
        else if (Input.mousePresent)
        {
            pointerPos = Input.mousePosition;
            hasPointer = true;
        }

        if (!hasPointer) return;

        Ray ray = Camera.main.ScreenPointToRay(pointerPos);
        if (Physics.Raycast(ray, out RaycastHit hit, 500f))
        {
            // Vérifier si le point est sur le NavMesh avec rayon élargi (8.0m)
            bool isValid = NavMesh.SamplePosition(hit.point, out NavMeshHit navHit, 8.0f, NavMesh.AllAreas);

            if (previewRing != null)
            {
                previewRing.SetActive(true);
                Vector3 ringPos = isValid ? navHit.position : hit.point;
                ringPos.y += 0.05f;
                previewRing.transform.position = ringPos;

                // Cyan si joueur, Rouge/Orange si ennemi, Rouge foncé si invalide
                Color ringCol = isValid ? (selectedTeam == 1 ? new Color(0f, 0.9f, 1f, 0.8f) : new Color(1f, 0.35f, 0.2f, 0.8f)) : new Color(1f, 0f, 0f, 0.6f);
                if (previewMat.HasProperty("_BaseColor")) previewMat.SetColor("_BaseColor", ringCol);
                if (previewMat.HasProperty("_Color")) previewMat.SetColor("_Color", ringCol);
            }

            // Détection du Clic ou Touch Tap pour déposer l'unité (PC & Mobile)
            bool isActionPressed = false;
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) isActionPressed = true;
            if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame) isActionPressed = true;
            if (Input.GetMouseButtonDown(0)) isActionPressed = true;
            if (Input.touchCount > 0 && Input.GetTouch(0).phase == UnityEngine.TouchPhase.Began) isActionPressed = true;

            if (isActionPressed)
            {
                // Ignorer si on a cliqué sur un bouton d'interface
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

                if (isValid)
                {
                    SpawnUnitAt(activePlacingType.Value, navHit.position, selectedTeam);
                    CancelPlacement();
                    if (Application.isMobilePlatform) Handheld.Vibrate();
                }
                else
                {
                    ShowMessage("Emplacement invalide ! Touchez une rue ou un carrefour pour déposer l'unité.", 2.5f);
                }
            }
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
            // 1. Charger en priorité absolue le prefab d'origine tout neuf
            GameObject prefab = null;
#if UNITY_EDITOR
            prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Kucher/Tank Leopard2/Prefabs/Leopard2.prefab");
#endif
            if (prefab == null) prefab = Resources.Load<GameObject>("Kucher/Tank Leopard2/Prefabs/Leopard2");

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
            GameObject canonTemplate = null;
            foreach (var u in FindObjectsByType<UnitAI>(FindObjectsInactive.Include))
            {
                if (u.gameObject.name.ToLower().Contains("canon") && !u.isDead)
                {
                    canonTemplate = u.gameObject;
                    break;
                }
            }

            if (canonTemplate != null)
            {
                newUnitObj = Instantiate(canonTemplate, position, Quaternion.identity);
            }
            else
            {
                GameObject prefab = null;
#if UNITY_EDITOR
                prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Kucher/Tank Leopard2/Prefabs/Leopard2.prefab");
#endif
                if (prefab != null) newUnitObj = Instantiate(prefab, position, Quaternion.identity);
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

            if (gradTex != null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Unlit");
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
    /// Déploie instantanément une escouade ennemie IA (Fantassins, Char, Mortier) sur les routes.
    /// </summary>
    public void SpawnEnemyWave()
    {
        Vector3 enemyBase = new Vector3(25f, 0f, 25f);
        if (NavMesh.SamplePosition(enemyBase, out NavMeshHit enh, 40f, NavMesh.AllAreas))
        {
            enemyBase = enh.position;
        }

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
        Vector3 playerPos = new Vector3(-25f, 0f, -25f);
        if (NavMesh.SamplePosition(playerPos, out NavMeshHit pnh, 40f, NavMesh.AllAreas))
        {
            playerPos = pnh.position;
        }

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

    void OnGUI()
    {
        // Ne pas afficher pendant l'exécution du tour
        TacticalPathManager pathManager = FindAnyObjectByType<TacticalPathManager>();
        if (pathManager != null && pathManager.phaseActuelle == TacticalPathManager.GamePhase.Execution) return;

        // Mise à l'échelle automatique +50% pour écrans mobiles (Portrait & Paysage)
        Matrix4x4 origMat = GUI.matrix;
        float uiScale = Mathf.Clamp(Screen.width / 480f, 1.35f, 2.2f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));

        int playerUnits = GetTeamLivingUnitsCount(1);
        int enemyUnits = GetTeamLivingUnitsCount(2);

        // 1. Bouton d'ouverture/fermeture du Dock de déploiement (Gros bouton tactile)
        string tabText = isPanelOpen ? "▼ Masquer Déploiement" : $"🎖️ Déployer Unités ({playerUnits} vs {enemyUnits})";
        if (GUI.Button(new Rect(15, 15, 230, 44), tabText))
        {
            isPanelOpen = !isPanelOpen;
            AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
        }

        // 2. Panneau de déploiement
        if (isPanelOpen)
        {
            GUIStyle panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.fontSize = 13;
            panelStyle.normal.textColor = Color.white;

            GUI.Box(new Rect(15, 65, 250, 430), "QG TACTIQUE : DÉPLOIEMENT", panelStyle);

            // Compteur par équipe
            GUIStyle counterStyle = new GUIStyle(GUI.skin.label);
            counterStyle.fontStyle = FontStyle.Bold;
            counterStyle.fontSize = 12;
            int currentTeamCount = (selectedTeam == 1) ? playerUnits : enemyUnits;
            counterStyle.normal.textColor = (currentTeamCount >= maxUnitsPerTeam) ? Color.red : Color.cyan;
            GUI.Label(new Rect(25, 92, 230, 22), $"Effectifs : {currentTeamCount} / {maxUnitsPerTeam}", counterStyle);

            // Sélecteur d'Équipe
            string team1Label = (selectedTeam == 1) ? $"🔵 Joueur ({playerUnits})" : $"Joueur ({playerUnits})";
            string team2Label = (selectedTeam == 2) ? $"🔴 IA ({enemyUnits})" : $"IA ({enemyUnits})";

            if (GUI.Button(new Rect(25, 116, 110, 32), team1Label))
            {
                selectedTeam = 1;
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
            }
            if (GUI.Button(new Rect(140, 116, 110, 32), team2Label))
            {
                selectedTeam = 2;
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
            }

            // Boutons de Sélection d'Unités (+50% de hauteur pour le tactile)
            if (GUI.Button(new Rect(25, 154, 225, 32), "🎖️ Fantassin (Fusil)"))
            {
                StartPlacingUnit(UnitType.Fantassin);
            }

            if (GUI.Button(new Rect(25, 190, 225, 32), "🛡️ Char Leopard 2 (Obus)"))
            {
                StartPlacingUnit(UnitType.CharLeopard);
            }

            if (GUI.Button(new Rect(25, 226, 225, 32), "💥 Véhicule Canon"))
            {
                StartPlacingUnit(UnitType.VehiculeCanon);
            }

            if (GUI.Button(new Rect(25, 262, 225, 32), "🎯 Mortier Lourd (120m)"))
            {
                StartPlacingUnit(UnitType.Mortier);
            }

            if (GUI.Button(new Rect(25, 298, 225, 32), "🚧 Barricade Routière"))
            {
                StartPlacingUnit(UnitType.BarricadeRoutiere);
            }

            // Actions rapides IA et Déploiement Auto
            GUIStyle aiBtnStyle = new GUIStyle(GUI.skin.button);
            aiBtnStyle.fontStyle = FontStyle.Bold;
            aiBtnStyle.fontSize = 12;
            aiBtnStyle.normal.textColor = new Color(1f, 0.35f, 0.25f);

            if (GUI.Button(new Rect(25, 336, 225, 34), "🤖 ESCOUADE IA (Rouge)", aiBtnStyle))
            {
                SpawnEnemyWave();
            }

            GUIStyle autoBtnStyle = new GUIStyle(GUI.skin.button);
            autoBtnStyle.fontStyle = FontStyle.Bold;
            autoBtnStyle.fontSize = 12;
            autoBtnStyle.normal.textColor = Color.yellow;

            if (GUI.Button(new Rect(25, 374, 225, 34), "⚡ DÉPLOIEMENT AUTO", autoBtnStyle))
            {
                AutoDeployBattlefield();
            }

            // Bouton Nettoyer
            if (GUI.Button(new Rect(25, 412, 225, 28), "🧹 Nettoyer le Terrain"))
            {
                ClearAllUnits();
            }
        }

        // 3. Indicateur de Placement actif
        if (IsPlacingUnit && activePlacingType.HasValue)
        {
            GUIStyle placingStyle = new GUIStyle(GUI.skin.box);
            placingStyle.fontSize = 14;
            placingStyle.fontStyle = FontStyle.Bold;
            placingStyle.normal.textColor = (selectedTeam == 1) ? Color.cyan : Color.red;
            float virtualW = Screen.width / uiScale;
            GUI.Box(new Rect(virtualW / 2 - 160, 15, 320, 48), $"MODE PLACEMENT : {activePlacingType.Value}\n[Touchez la rue] Poser | [Annuler]", placingStyle);
        }

        // 4. Message d'alerte / feedback
        if (!string.IsNullOrEmpty(statusMessage))
        {
            GUIStyle msgStyle = new GUIStyle(GUI.skin.box);
            msgStyle.fontSize = 13;
            msgStyle.normal.textColor = Color.yellow;
            float virtualW = Screen.width / uiScale;
            float virtualH = Screen.height / uiScale;
            GUI.Box(new Rect(virtualW / 2 - 180, virtualH - 60, 360, 40), statusMessage, msgStyle);
        }

        GUI.matrix = origMat;
    }
}
