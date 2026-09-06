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
    private VisualElement buttonContainerEl;
    private Button view3dButtonEl, cancelBtnEl, confirmBtnEl;
    // Conservé pour pouvoir masquer "Passer" en multijoueur (voir RefreshTacticalUI) : il n'y abrège
    // rien, c'est le serveur qui décide de la fin du tour.
    private Button skipButtonEl;
    private Label menuTitleEl;
    private Label notifBellBadgeEl;
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
            invalidTapToastEl = bottomBarRoot.Q<Label>("invalid-tap-toast");
            if (invalidTapToastEl == null) { Debug.LogError("[TacticalPathManager] Élément 'invalid-tap-toast' introuvable dans le UXML instancié."); return; }

            Button endTurnBtn = bottomBarRoot.Q<Button>("end-turn-button");
            if (endTurnBtn == null) { Debug.LogError("[TacticalPathManager] Bouton 'end-turn-button' introuvable dans le UXML instancié."); return; }
            endTurnBtn.clicked += LancerExecutionTour;

            Button notifBellBtn = bottomBarRoot.Q<Button>("notif-bell-button");
            notifBellBadgeEl = bottomBarRoot.Q<Label>("notif-bell-badge");
            if (notifBellBtn == null) { Debug.LogError("[TacticalPathManager] Bouton 'notif-bell-button' introuvable dans le UXML instancié."); return; }
            notifBellBtn.clicked += SelectionnerProchaineUniteBlessee;

            Button skipBtn = bottomBarRoot.Q<Button>("skip-button");
            if (skipBtn == null) { Debug.LogError("[TacticalPathManager] Bouton 'skip-button' introuvable dans le UXML instancié."); return; }
            skipButtonEl = skipBtn;
            // "Passer" ne vaut qu'en SOLO. En multijoueur, la fin du tour est décidée par le serveur :
            // ForcerFinExecution est purement local (il repasse en Planification et efface les ordres),
            // donc appuyer dessus pendant que le serveur résout ou que le rejeu tourne faisait
            // replanifier le joueur par-dessus des unités encore en train de bouger, puis effaçait
            // d'un coup tout ce qu'il venait de tracer à la fin du rejeu — voire envoyait un second
            // "submit_turn" pour le tour suivant. On le neutralise donc hors solo (il est masqué juste
            // en dessous, ce garde couvre le cas où il resterait cliquable).
            skipBtn.clicked += () =>
            {
                if (Novgov.Network.MultiplayerMatchController.IsFlowActive)
                {
                    Debug.Log("[TacticalPathManager] \"Passer\" ignoré en multijoueur : la fin du tour est décidée par le serveur.");
                    return;
                }
                ForcerFinExecution();
            };

            if (view3dButtonEl == null) { Debug.LogError("[TacticalPathManager] Élément 'view3d-button' introuvable dans le UXML instancié."); return; }
            view3dButtonEl.clicked += () =>
            {
                if (CameraStateManager.Instance != null && uniteSelectionnee != null)
                {
                    suppressPointerInputUntilFrame = Time.frameCount;
                    CameraStateManager.Instance.Enter3DView(uniteSelectionnee.transform);
                    FermerMenuContextuel(invokeCancelAction: true);
                    if (menuPanel != null) menuPanel.SetActive(false);
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
            || menuTitleEl == null || buttonContainerEl == null || cancelBtnEl == null || confirmBtnEl == null)
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

        // "Passer" abrège l'exécution — un geste qui n'a de sens qu'en solo, où c'est le client qui
        // arbitre la durée du tour. En multijoueur il n'a aucun effet (voir le garde à son câblage) :
        // autant ne pas le proposer plutôt que d'afficher un bouton qui ne fait rien.
        if (skipButtonEl != null)
        {
            skipButtonEl.style.display = Novgov.Network.MultiplayerMatchController.IsFlowActive
                ? DisplayStyle.None
                : DisplayStyle.Flex;
        }

        bool showContext = isPlanification && uniteSelectionnee != null && !hideBottomBar;
        view3dButtonEl.style.display = (is2DMode && showContext) ? DisplayStyle.Flex : DisplayStyle.None;

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
