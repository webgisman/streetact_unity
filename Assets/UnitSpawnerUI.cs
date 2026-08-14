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

    public enum UnitType { Fantassin, CharLeopard, VehiculeCanon }

    [Header("Configuration")]
    public int maxUnits = 6;
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
        if (Pointer.current == null) return;

        Ray ray = Camera.main.ScreenPointToRay(Pointer.current.position.ReadValue());
        if (Physics.Raycast(ray, out RaycastHit hit, 500f))
        {
            // Vérifier si le point est sur le NavMesh
            bool isValid = NavMesh.SamplePosition(hit.point, out NavMeshHit navHit, 3.0f, NavMesh.AllAreas);

            if (previewRing != null)
            {
                previewRing.SetActive(true);
                Vector3 ringPos = isValid ? navHit.position : hit.point;
                ringPos.y += 0.05f;
                previewRing.transform.position = ringPos;

                // Vert si valide, Rouge si hors NavMesh
                Color ringCol = isValid ? (selectedTeam == 1 ? new Color(0f, 0.8f, 1f, 0.7f) : new Color(1f, 0.3f, 0.2f, 0.7f)) : new Color(1f, 0f, 0f, 0.5f);
                if (previewMat.HasProperty("_BaseColor")) previewMat.SetColor("_BaseColor", ringCol);
                if (previewMat.HasProperty("_Color")) previewMat.SetColor("_Color", ringCol);
            }

            // Clic Gauche pour déposer l'unité
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                // Ignorer si on a cliqué sur un bouton d'interface
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

                if (isValid)
                {
                    SpawnUnitAt(activePlacingType.Value, navHit.position, selectedTeam);
                    CancelPlacement();
                }
                else
                {
                    ShowMessage("Emplacement invalide ! Déposez l'unité sur une zone de route praticable.", 2.5f);
                }
            }
        }
    }

    public void StartPlacingUnit(UnitType type)
    {
        int currentCount = GetTotalLivingUnitsCount();
        if (currentCount >= maxUnits)
        {
            ShowMessage($"Limite atteinte ! Vous ne pouvez pas dépasser {maxUnits} unités au total.", 3.0f);
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
        int count = GetTotalLivingUnitsCount();
        if (count >= maxUnits)
        {
            ShowMessage($"Limite de {maxUnits} unités atteinte !", 3.0f);
            return;
        }

        GameObject newUnitObj = null;

        if (type == UnitType.Fantassin)
        {
            // Chercher une unité d'infanterie existante dans la scène comme modèle
            GameObject template = GameObject.Find("Unite_1") ?? GameObject.Find("Unite_2");
            if (template != null)
            {
                newUnitObj = Instantiate(template, position, Quaternion.identity);
            }
            else
            {
                // Fallback de création dynamique
                newUnitObj = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                newUnitObj.AddComponent<NavMeshAgent>();
                newUnitObj.AddComponent<UnitAI>();
            }
            newUnitObj.name = $"Fantassin_{team}_{(count + 1)}";
        }
        else if (type == UnitType.CharLeopard)
        {
            // Chercher le char Leopard dans la scène ou charger le prefab
            GameObject tankTemplate = null;
            foreach (var u in FindObjectsByType<UnitAI>(FindObjectsInactive.Include))
            {
                if (u.gameObject.name.ToLower().Contains("leopard"))
                {
                    tankTemplate = u.gameObject;
                    break;
                }
            }

            if (tankTemplate != null)
            {
                newUnitObj = Instantiate(tankTemplate, position, Quaternion.identity);
            }
            else
            {
                GameObject prefab = Resources.Load<GameObject>("Kucher/Tank Leopard2/Prefabs/Leopard2");
                if (prefab != null) newUnitObj = Instantiate(prefab, position, Quaternion.identity);
                else
                {
                    newUnitObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    newUnitObj.transform.localScale = new Vector3(3f, 2f, 5f);
                    newUnitObj.AddComponent<NavMeshAgent>();
                    newUnitObj.AddComponent<UnitAI>();
                }
            }
            newUnitObj.name = $"Leopard2_{team}_{(count + 1)}";
        }
        else if (type == UnitType.VehiculeCanon)
        {
            GameObject canonTemplate = null;
            foreach (var u in FindObjectsByType<UnitAI>(FindObjectsInactive.Include))
            {
                if (u.gameObject.name.ToLower().Contains("canon"))
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
                // Cloner le char en adaptant ses propriétés
                GameObject tankTemplate = GameObject.Find("Leopard2") ?? GameObject.Find("Leopard_1");
                if (tankTemplate != null) newUnitObj = Instantiate(tankTemplate, position, Quaternion.identity);
                else
                {
                    newUnitObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    newUnitObj.transform.localScale = new Vector3(2.5f, 1.8f, 4.5f);
                    newUnitObj.AddComponent<NavMeshAgent>();
                    newUnitObj.AddComponent<UnitAI>();
                }
            }
            newUnitObj.name = $"Canon_Vehicule_{team}_{(count + 1)}";
        }

        if (newUnitObj != null)
        {
            newUnitObj.SetActive(true);
            newUnitObj.transform.position = position;

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
            else
            {
                unitAI.isTank = false;
                unitAI.maxHealth = 100f;
                unitAI.health = 100;
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

            unitAI.ResetOrderState(); // Réinitialise proprement les ordres pour éviter que l'unité ne soit bloquée
            unitAI.OnNavMeshReady();
            unitAI.SetupHealthBar(); // Régénère une barre de vie neuve et 100% pleine avec la bonne couleur

            // Son de confirmation de déploiement
            AudioClip confirmClip = ProceduralAudioBuilder.CreateTargetConfirmedSound();
            if (confirmClip != null) AudioSource.PlayClipAtPoint(confirmClip, Camera.main.transform.position, 0.8f);
            ShowMessage($"✅ {newUnitObj.name} déployé avec succès !", 2.0f);
        }
    }

    public void ClearAllUnits()
    {
        UnitAI[] allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude);
        foreach (var u in allUnits)
        {
            Destroy(u.gameObject);
        }
        ShowMessage("Toutes les unités ont été retirées.", 2.0f);
    }

    public int GetTotalLivingUnitsCount()
    {
        UnitAI[] allUnits = FindObjectsByType<UnitAI>(FindObjectsInactive.Exclude);
        int count = 0;
        foreach (var u in allUnits)
        {
            if (u != null && !u.isDead) count++;
        }
        return count;
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

        int livingCount = GetTotalLivingUnitsCount();

        // 1. Bouton d'ouverture/fermeture du Dock de déploiement
        string tabText = isPanelOpen ? "▼ Masquer Déploiement" : $"🎖️ Déployer Unités ({livingCount}/{maxUnits})";
        if (GUI.Button(new Rect(20, 20, 240, 40), tabText))
        {
            isPanelOpen = !isPanelOpen;
            AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
        }

        // 2. Panneau de déploiement
        if (isPanelOpen)
        {
            GUIStyle panelStyle = new GUIStyle(GUI.skin.box);
            panelStyle.fontSize = 14;
            panelStyle.normal.textColor = Color.white;

            GUI.Box(new Rect(20, 65, 270, 310), "QG TACTIQUE : DÉPLOIEMENT", panelStyle);

            // Compteur d'unités
            GUIStyle counterStyle = new GUIStyle(GUI.skin.label);
            counterStyle.fontStyle = FontStyle.Bold;
            counterStyle.normal.textColor = (livingCount >= maxUnits) ? Color.red : Color.cyan;
            GUI.Label(new Rect(35, 95, 240, 25), $"Effectifs : {livingCount} / {maxUnits} Unités Max", counterStyle);

            // Sélecteur d'Équipe
            GUI.Label(new Rect(35, 125, 100, 25), "Équipe :");
            string team1Label = (selectedTeam == 1) ? "🔵 JOUEUR (Allié)" : "Joueur";
            string team2Label = (selectedTeam == 2) ? "🔴 ENNEMI (IA)" : "Ennemi";

            if (GUI.Button(new Rect(105, 122, 85, 28), team1Label))
            {
                selectedTeam = 1;
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
            }
            if (GUI.Button(new Rect(195, 122, 80, 28), team2Label))
            {
                selectedTeam = 2;
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
            }

            GUI.Label(new Rect(35, 155, 240, 20), "Cliquez pour placer sur la carte :");

            // Bouton 1 : Fantassin
            if (GUI.Button(new Rect(35, 180, 240, 35), "🎖️ Fantassin (100 PV | Fusil)"))
            {
                StartPlacingUnit(UnitType.Fantassin);
            }

            // Bouton 2 : Char Leopard 2
            if (GUI.Button(new Rect(35, 220, 240, 35), "🛡️ Char Leopard 2 (500 PV | Obus)"))
            {
                StartPlacingUnit(UnitType.CharLeopard);
            }

            // Bouton 3 : Véhicule Canon
            if (GUI.Button(new Rect(35, 260, 240, 35), "💥 Véhicule Canon (250 PV)"))
            {
                StartPlacingUnit(UnitType.VehiculeCanon);
            }

            // Bouton Retirer / Effacer
            if (GUI.Button(new Rect(35, 310, 240, 30), "🧹 Nettoyer le Terrain"))
            {
                ClearAllUnits();
            }
        }

        // 3. Indicateur de Placement actif
        if (IsPlacingUnit && activePlacingType.HasValue)
        {
            GUIStyle placingStyle = new GUIStyle(GUI.skin.box);
            placingStyle.fontSize = 16;
            placingStyle.normal.textColor = (selectedTeam == 1) ? Color.cyan : Color.red;
            GUI.Box(new Rect(Screen.width / 2 - 220, 20, 440, 50), $"MODE PLACEMENT : {activePlacingType.Value}\n[Clic Gauche] Déposer | [Clic Droit] Annuler", placingStyle);
        }

        // 4. Message d'alerte / feedback
        if (!string.IsNullOrEmpty(statusMessage))
        {
            GUIStyle msgStyle = new GUIStyle(GUI.skin.box);
            msgStyle.fontSize = 15;
            msgStyle.normal.textColor = Color.yellow;
            GUI.Box(new Rect(Screen.width / 2 - 250, Screen.height - 70, 500, 40), statusMessage, msgStyle);
        }
    }
}
