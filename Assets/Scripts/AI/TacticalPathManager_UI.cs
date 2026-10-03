using System.Collections.Generic;
using UnityEngine;
#if !UNITY_SERVER
using UnityEngine.UIElements;
#endif

public partial class TacticalPathManager
{
    // =====================================================================
    // UI Toolkit (barre du bas + menus contextuels) — remplace l'ancien OnGUI().
    // Le contenu d'un menu contextuel est construit une seule fois, au moment précis où le
    // joueur tape (aux points où isXSelected passe à true, voir HandlePointerInput() dans
    // TacticalPathManager_Input.cs) — pas chaque frame comme le faisait OnGUI, pour ne jamais
    // reconstruire un bouton pendant qu'il est en train d'être touché. RefreshTacticalUI()
    // (appelé chaque frame) ne fait que basculer de la visibilité, jamais de reconstruction,
    // donc sans risque d'interrompre un tap en cours.
    // =====================================================================
#if !UNITY_SERVER
    private bool tacticalUiBound = false;
    private VisualElement bottomBarRoot, contextMenuRoot, planGroupEl, execGroupEl, squadBarEl, topActionsRowEl;
    private VisualElement topDashboardEl;
    private VisualElement buttonContainerEl;
    private Button view3dButtonEl, cancelBtnEl, confirmBtnEl;
    /// <summary>RETIRER LE POINT : seule façon d'annuler un point posé sur un téléphone (le clic
    /// droit n'existe qu'à la souris).</summary>
    private Button undoNodeButtonEl;
    // ACCÉLÉRER : Solo uniquement (en ligne, le rythme du rejeu est celui du serveur).
    private Button skipButtonEl;
    private Button endTurnButtonEl;
    // BLESSÉS (n) : sélectionne tour à tour vos unités blessées ; masqué s'il n'y en a aucune.
    private Button woundedButtonEl;
    private Label turnHintEl;
    private Label menuTitleEl;
    private Label invalidTapToastEl;
    private float invalidTapToastTimer = 0f;
    private System.Action currentMenuCancelAction;
    private System.Action currentMenuConfirmAction;

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
            view3dButtonEl = bottomBarRoot.Q<Button>("view3d-button");
            squadBarEl = bottomBarRoot.Q<VisualElement>("squad-bar");
            topActionsRowEl = bottomBarRoot.Q<VisualElement>("top-actions-row");
            topDashboardEl = bottomBarRoot.Q<VisualElement>("top-dashboard");
            invalidTapToastEl = bottomBarRoot.Q<Label>("invalid-tap-toast");
            if (invalidTapToastEl == null) { Debug.LogError("[TacticalPathManager] Élément 'invalid-tap-toast' introuvable dans le UXML instancié."); return; }

            endTurnButtonEl = bottomBarRoot.Q<Button>("end-turn-button");
            if (endTurnButtonEl == null) { Debug.LogError("[TacticalPathManager] Bouton 'end-turn-button' introuvable dans le UXML instancié."); return; }
            endTurnButtonEl.clicked += LancerExecutionTour;

            undoNodeButtonEl = bottomBarRoot.Q<Button>("undo-node-button");
            if (undoNodeButtonEl == null) { Debug.LogError("[TacticalPathManager] Bouton 'undo-node-button' introuvable dans le UXML instancié."); return; }
            undoNodeButtonEl.clicked += AnnulerDernierPoint;

            woundedButtonEl = bottomBarRoot.Q<Button>("wounded-button");
            if (woundedButtonEl == null) { Debug.LogError("[TacticalPathManager] Bouton 'wounded-button' introuvable dans le UXML instancié."); return; }
            woundedButtonEl.clicked += SelectionnerProchaineUniteBlessee;

            turnHintEl = bottomBarRoot.Q<Label>("turn-hint");
            if (turnHintEl == null) { Debug.LogError("[TacticalPathManager] Élément 'turn-hint' introuvable dans le UXML instancié."); return; }

            Button hudMenuBtn = bottomBarRoot.Q<Button>("hud-menu-button");
            if (hudMenuBtn == null) { Debug.LogError("[TacticalPathManager] Bouton 'hud-menu-button' introuvable dans le UXML instancié."); return; }
            Novgov.UI.InGameMenuController.BindHudButton(hudMenuBtn);

            skipButtonEl = bottomBarRoot.Q<Button>("skip-button");
            if (skipButtonEl == null) { Debug.LogError("[TacticalPathManager] Bouton 'skip-button' introuvable dans le UXML instancié."); return; }
            // ACCÉLÉRER (Solo) : jusqu'au 2026-10-03 ce bouton s'appelait « Passer » et ARRÊTAIT le
            // tour — les unités s'immobilisaient sur place et perdaient leurs ordres. Il fait
            // maintenant ce qu'un joueur attend : la suite du tour, en accéléré.
            skipButtonEl.clicked += ToggleSoloSpeed;

            if (view3dButtonEl == null) { Debug.LogError("[TacticalPathManager] Élément 'view3d-button' introuvable dans le UXML instancié."); return; }
            view3dButtonEl.clicked += () =>
            {
                if (CameraStateManager.Instance != null && uniteSelectionnee != null)
                {
                    suppressPointerInputUntilFrame = Time.frameCount;
                    CameraStateManager.Instance.Enter3DView(uniteSelectionnee.transform);
                    FermerMenuContextuel(invokeCancelAction: true);
                }
            };

            // Le menu contextuel (ContextMenu) n'apparaît QUE lorsqu'un endroit valide a été tapé
            // sur la carte pour l'unité sélectionnée (voir HandlePointerInput() et
            // ShowContextMenu()) — jamais à la simple sélection d'une unité (qui ne fait
            // qu'activer son cercle de sélection, voir SelectionnerUnite()). Chaque option de ce
            // menu se "propose" au tap (surlignée, pas encore appliquée) ; TERMINÉ verrouille
            // l'option en surbrillance dans la trajectoire de l'unité, ANNULER referme le menu
            // sans rien changer pour retaper un autre endroit.
            contextMenuRoot = UIScreenManager.Instance.GetScreen("ContextMenu");
            if (contextMenuRoot == null)
            {
                Debug.LogError("[TacticalPathManager] Écran 'ContextMenu' introuvable (UXML non chargé) — les menus d'action (porte/toit/bâtiment) resteront inopérants.");
                return;
            }
            menuTitleEl = contextMenuRoot.Q<Label>("menu-title");
            if (menuTitleEl == null) { Debug.LogError("[TacticalPathManager] Élément 'menu-title' introuvable dans ContextMenu."); return; }
            buttonContainerEl = contextMenuRoot.Q<VisualElement>("button-container");
            if (buttonContainerEl == null) { Debug.LogError("[TacticalPathManager] Élément 'button-container' introuvable dans ContextMenu."); return; }

            Button cancelBtn = contextMenuRoot.Q<Button>("cancel-button");
            if (cancelBtn == null) { Debug.LogError("[TacticalPathManager] Élément 'cancel-button' introuvable dans ContextMenu."); return; }
            cancelBtnEl = cancelBtn;
            cancelBtn.clicked += () =>
            {
                // Voir le commentaire dans ShowContextMenu() : ce bouton MASQUE le ContextMenu, donc
                // sans cette garde, le relâchement du clic physique peut fuiter vers un raycast 3D
                // brut au même pixel (le panel vient de disparaître sous le doigt/curseur) — une
                // seule frame suffit, pas besoin de retarder le tap suivant.
                suppressPointerInputUntilFrame = Time.frameCount;
                FermerMenuContextuel(invokeCancelAction: true);
                AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateClickSound(), Camera.main.transform.position);
            };

            Button confirmBtn = contextMenuRoot.Q<Button>("confirm-button");
            if (confirmBtn == null) { Debug.LogError("[TacticalPathManager] Élément 'confirm-button' introuvable dans ContextMenu."); return; }
            confirmBtnEl = confirmBtn;
            confirmBtn.clicked += () =>
            {
                // N'a d'effet que si une option a été surlignée (voir ShowContextMenu) — le bouton
                // reste désactivé (donc non cliquable) tant que ce n'est pas le cas. On invoque
                // l'action AVANT de fermer (FermerMenuContextuel remet currentMenuConfirmAction à
                // null) — et sans invokeCancelAction, TERMINÉ ne doit jamais annuler ce qu'il vient
                // de confirmer. Voir aussi le commentaire dans ShowContextMenu() : ce bouton MASQUE
                // le ContextMenu — sans armer cette garde d'une frame, le relâchement du même clic
                // physique fuitait vers un raycast 3D brut. Un cooldown en secondes (essayé avant)
                // bloquait aussi la sélection légitime d'une autre unité juste après un TERMINÉ
                // rapide — d'où le passage à une garde d'une seule frame.
                suppressPointerInputUntilFrame = Time.frameCount;
                currentMenuConfirmAction?.Invoke();
                FermerMenuContextuel(invokeCancelAction: false);
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
        if (UIScreenManager.Instance == null || bottomBarRoot == null || contextMenuRoot == null || planGroupEl == null
            || execGroupEl == null || view3dButtonEl == null || invalidTapToastEl == null
            || menuTitleEl == null || buttonContainerEl == null || cancelBtnEl == null || confirmBtnEl == null
            || endTurnButtonEl == null || undoNodeButtonEl == null || woundedButtonEl == null || skipButtonEl == null || turnHintEl == null)
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
        // IsFlowActive reste vrai pendant TOUTE la session multijoueur, y compris la partie
        // elle-même (InMatch) — sans l'exception IsInMatch ci-dessous, le bouton FIN DE TOUR et la
        // squad-bar restaient invisibles du premier au dernier tour d'un match PvP réel (bug trouvé
        // lors du premier vrai test à 2 téléphones), alors que le tour-par-tour utilise exactement
        // ce même TacticalBottomBar qu'en solo (voir MatchSessionManager, "simulation autoritaire").
        if (Novgov.Network.MultiplayerMatchController.IsFlowActive && !Novgov.Network.MultiplayerMatchController.IsInMatch) hideBottomBar = true;
        if (GameManagerUI.Instance != null && GameManagerUI.Instance.IsStartupSelectionActive) hideBottomBar = true;
        if (IsSoloGameOver) hideBottomBar = true;
        UIScreenManager.Instance.SetVisible("TacticalBottomBar", !hideBottomBar);

        bool isPlanification = phaseActuelle == GamePhase.Planification;
        planGroupEl.style.display = isPlanification ? DisplayStyle.Flex : DisplayStyle.None;
        execGroupEl.style.display = isPlanification ? DisplayStyle.None : DisplayStyle.Flex;

        // Pas de FIN DE TOUR tant que les troupes Solo ne sont pas placées (COMMENCER LA BATAILLE).
        endTurnButtonEl.style.display = (isPlanification && !UnitSpawnerUI.IsSoloDeploymentPhase) ? DisplayStyle.Flex : DisplayStyle.None;

        skipButtonEl.style.display = Novgov.Network.MultiplayerMatchController.IsFlowActive ? DisplayStyle.None : DisplayStyle.Flex;
        skipButtonEl.text = SoloSpeed > 1f ? "VITESSE NORMALE" : "ACCÉLÉRER";

        bool showContext = isPlanification && uniteSelectionnee != null && !hideBottomBar;
        view3dButtonEl.style.display = (is2DMode && showContext) ? DisplayStyle.Flex : DisplayStyle.None;

        // Annuler le dernier point : proposé dès que l'unité sélectionnée a au moins un point posé.
        // Sans ce bouton, l'annulation n'était accessible qu'au clic DROIT — inexistant sur mobile.
        UnitAI selectedForUndo = uniteSelectionnee != null ? uniteSelectionnee.GetComponent<UnitAI>() : null;
        bool canUndo = showContext && selectedForUndo != null && selectedForUndo.tacticalPath.Count > 0;
        undoNodeButtonEl.style.display = canUndo ? DisplayStyle.Flex : DisplayStyle.None;

        // Le ContextMenu ne montre plus qu'une seule chose : le sous-menu d'ordre ouvert au tap
        // d'un endroit valide de la carte (bâtiment/porte/fenêtre/checkpoint/barricade, construit
        // une fois par ShowContextMenu — jamais reconstruit ici). Sélectionner une unité n'affiche
        // plus rien.
        UIScreenManager.Instance.SetVisible("ContextMenu", AnyOrderMenuOpen);

        if (invalidTapToastTimer > 0f)
        {
            invalidTapToastTimer -= Time.deltaTime;
            if (invalidTapToastTimer <= 0f) invalidTapToastEl.style.display = DisplayStyle.None;
        }

        RefreshSquadBar();
        PositionTopRightCluster();

        int wounded = CountWoundedPlayerUnits();
        woundedButtonEl.style.display = (isPlanification && wounded > 0) ? DisplayStyle.Flex : DisplayStyle.None;
        woundedButtonEl.text = $"BLESSÉS ({wounded})";

        RefreshTurnHint(hideBottomBar);
    }

    /// <summary>Consigne du moment, en bas de l'écran : ce que le joueur doit faire MAINTENANT.
    /// Ajoutée le 2026-10-03 (« le joueur doit tout comprendre ») — rien n'indiquait jusque-là
    /// qu'il fallait toucher une unité, puis la carte, puis FIN DE TOUR.</summary>
    private void RefreshTurnHint(bool hideBottomBar)
    {
        string hint = null;
        bool deploying = UnitSpawnerUI.IsSoloDeploymentPhase || Novgov.Network.MultiplayerMatchController.IsDeploymentPhaseActive;
        if (!hideBottomBar && !deploying && phaseActuelle == GamePhase.Planification && !UnitSpawnerUI.IsPlacingUnit)
        {
            UnitAI selected = uniteSelectionnee != null ? uniteSelectionnee.GetComponent<UnitAI>() : null;
            if (AnyOrderMenuOpen)
                hint = "Choisissez un ordre, puis TERMINÉ.";
            else if (selected == null)
                hint = AnyPlayerUnitWithOrders()
                    ? "Touchez une autre unité pour lui donner un ordre, ou FIN DE TOUR pour lancer le tour."
                    : "Touchez une de vos unités pour lui donner un ordre.";
            else if (selected.isMortar)
                hint = "Touchez la carte : là où le mortier doit tirer, ou aller.";
            else if (selected.tacticalPath.Count == 0)
                hint = "Touchez la carte là où cette unité doit aller.";
            else
                hint = "Touchez encore la carte pour ajouter une étape, ou une autre unité. FIN DE TOUR quand vous êtes prêt.";
        }

        turnHintEl.style.display = hint != null ? DisplayStyle.Flex : DisplayStyle.None;
        if (hint != null) turnHintEl.text = hint;
    }

    private static bool AnyPlayerUnitWithOrders()
    {
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI u = UnitAI.AllLivingUnits[i];
            if (u != null && !u.isDead && u.isPlayerControlled && u.tacticalPath != null && u.tacticalPath.Count > 0) return true;
        }
        return false;
    }

    /// <summary>Décale la barre du haut sous l'encoche / la perforation caméra du téléphone
    /// (Screen.safeArea), recalculé quand la résolution change — retour joueur 2026-09-20 :
    /// « l'icône vue 3D est sous l'appareil photo, impossible d'appuyer dessus ».</summary>
    private int lastClusterSafeAreaW = -1, lastClusterSafeAreaH = -1;
    private void PositionTopRightCluster()
    {
        if (topDashboardEl == null || topActionsRowEl == null) return;
        if (Screen.width == lastClusterSafeAreaW && Screen.height == lastClusterSafeAreaH) return;
        lastClusterSafeAreaW = Screen.width;
        lastClusterSafeAreaH = Screen.height;

        Rect safe = Screen.safeArea;
        float topInset = Screen.height - safe.yMax;   // px déjà exclus par une encoche/perforation caméra en haut
        float rightInset = Screen.width - safe.xMax;   // px exclus côté droit (perforation en coin)

        // Toute la barre haute descend d'abord sous l'encoche...
        topDashboardEl.style.marginTop = topInset > 0f ? topInset + 6f : 0f;
        // ...puis le bandeau d'actions (côté droit de cette barre) s'écarte en plus
        // d'une perforation en coin, sans affecter les portraits d'escouade à gauche.
        topActionsRowEl.style.marginRight = rightInset > 0f ? rightInset + 6f : 0f;
    }

    /// <summary>Portraits par type d'unité (coin haut-gauche) avec leur nombre ; toucher un
    /// portrait sélectionne l'unité suivante de ce type. Les boutons ne sont créés qu'une fois.</summary>
    private VisualElement mortarPortraitEl;
    private Button mortarBtnEl;
    private Label mortarCountEl;
    private readonly List<UnitAI> activeMortars = new List<UnitAI>();

    private VisualElement tankPortraitEl;
    private Button tankBtnEl;
    private Label tankCountEl;
    private readonly List<UnitAI> activeTanks = new List<UnitAI>();

    private VisualElement canonPortraitEl;
    private Button canonBtnEl;
    private Label canonCountEl;
    private readonly List<UnitAI> activeCanonVehicles = new List<UnitAI>();

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
        mortarCountEl.AddToClassList("squad-count-badge");
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
        tankCountEl.AddToClassList("squad-count-badge");
        tankPortraitEl.Add(tankBtnEl);
        tankPortraitEl.Add(tankCountEl);
        squadBarEl.Add(tankPortraitEl);

        // 3. Véhicules canon — icône dédiée (GunVehicle), plus regroupés sous l'icône générique
        // "Char" : un joueur avec un véhicule canon voyait toujours l'icône d'un Leopard, jamais la
        // sienne, puisque isTank vaut true pour les deux types (voir RefreshSquadBar).
        canonPortraitEl = new VisualElement();
        canonPortraitEl.AddToClassList("squad-portrait");
        canonBtnEl = new Button(() => CycleSelectGroup(activeCanonVehicles));
        canonBtnEl.AddToClassList("squad-portrait-btn");
        canonBtnEl.style.backgroundImage = new StyleBackground(ProceduralIconFactory.GunVehicle());
        canonCountEl = new Label("0");
        canonCountEl.AddToClassList("squad-count-badge");
        canonPortraitEl.Add(canonBtnEl);
        canonPortraitEl.Add(canonCountEl);
        squadBarEl.Add(canonPortraitEl);

        // 4. Infanterie
        soldierPortraitEl = new VisualElement();
        soldierPortraitEl.AddToClassList("squad-portrait");
        soldierBtnEl = new Button(() => CycleSelectGroup(activeInfantry));
        soldierBtnEl.AddToClassList("squad-portrait-btn");
        soldierBtnEl.style.backgroundImage = new StyleBackground(ProceduralIconFactory.Soldier());
        soldierCountEl = new Label("0");
        soldierCountEl.AddToClassList("squad-count-badge");
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
        activeCanonVehicles.Clear();
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
                // Testé AVANT le bucket "Char" générique ci-dessous : isTank vaut true pour un
                // véhicule canon ET pour un Leopard (voir UnitSpawnerUI.SpawnUnitAt), donc sans
                // cette priorité, un véhicule canon retombait toujours dans le bucket "Char" et
                // affichait l'icône générique d'un Leopard, jamais la sienne.
                else if (u.isCanonVehicle || nameLower.Contains("canon"))
                {
                    activeCanonVehicles.Add(u);
                }
                else if (u.isTank || nameLower.Contains("leopard") || nameLower.Contains("char") || nameLower.Contains("tank"))
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

        // Véhicules canon
        canonPortraitEl.style.display = activeCanonVehicles.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        canonCountEl.text = activeCanonVehicles.Count.ToString();
        canonBtnEl.EnableInClassList("selected", activeCanonVehicles.Exists(u => u.gameObject == uniteSelectionnee));

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

#endif
}
