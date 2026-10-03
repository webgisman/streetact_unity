using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Point d'entrée unique de l'UI Toolkit runtime, remplaçant les OnGUI() dispersés du projet.
/// Un seul UIDocument, un conteneur par écran instancié depuis Resources/UI/&lt;nom&gt;.uxml.
/// Les classes contrôleurs existantes (MultiplayerMatchController, GameManagerUI, etc.) gardent
/// toute leur logique/état — elles appellent juste UIScreenManager.Show(...)/GetScreen(...) au
/// lieu de dessiner en OnGUI.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class UIScreenManager : MonoBehaviour
{
    public static UIScreenManager Instance { get; private set; }

    /// <summary>Racine brute du UIDocument, indépendante du système d'écrans ci-dessous — utile
    /// pour un overlay de diagnostic qui doit s'afficher même si le chargement d'un écran échoue.</summary>
    public VisualElement RootVisualElement { get; private set; }

    private readonly Dictionary<string, VisualElement> screens = new Dictionary<string, VisualElement>();

    // Nom logique -> chemin Resources (sans extension) du UXML.
    private static readonly (string name, string resourcePath)[] ScreenDefinitions =
    {
        ("Loading", "UI/LoadingScreen"),
        ("Error", "UI/ErrorScreen"),
        ("StartupMenu", "UI/StartupMenuScreen"),
        // Écran CONQUÊTE (2026-09-30) : remplace l'ancien hub "ModeSelect", la carte "ZoneMap" et le
        // choix de bataille "BattleSelect" — voir ConquestScreen.uxml.
        ("Conquest", "UI/ConquestScreen"),
        ("LocationPrompt", "UI/LocationPromptScreen"),
        ("Roster", "UI/RosterScreen"),
        ("Buildings", "UI/BuildingsScreen"),
        ("Notifications", "UI/NotificationsScreen"),
        ("Sieges", "UI/SiegesScreen"),
        ("ZoneResult", "UI/ZoneResultScreen"),
        ("Auth", "UI/AuthScreen"),
        ("Waiting", "UI/WaitingScreen"),
        ("InMatchHud", "UI/InMatchHudScreen"),
        ("MatchOver", "UI/MatchOverScreen"),
        ("GameOver", "UI/GameOverScreen"),
        ("Leaderboard", "UI/LeaderboardScreen"),
        ("ActionViewBack", "UI/ActionViewBackScreen"),
        ("TacticalBottomBar", "UI/TacticalBottomBarScreen"),
        ("DeploymentDock", "UI/DeploymentDockScreen"),
        ("ContextMenu", "UI/ContextMenuScreen"),
        // Déclaré en DERNIER : superposé par SetVisible (jamais Show) par-dessus le HUD tactique,
        // il doit être ajouté après lui dans l'arbre pour être dessiné au-dessus.
        ("PauseMenu", "UI/PauseMenuScreen"),
    };

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        var document = GetComponent<UIDocument>();
        VisualElement root = document.rootVisualElement;
        RootVisualElement = root;
        root.style.flexGrow = 1;
        root.pickingMode = PickingMode.Ignore; // laisse passer les clics vers la scène 3D là où aucun écran n'est actif
        ApplyUiScale();
        ApplySafeAreaPadding(root);

        foreach (var (name, resourcePath) in ScreenDefinitions)
        {
            VisualTreeAsset asset = Resources.Load<VisualTreeAsset>(resourcePath);
            if (asset == null)
            {
                Debug.LogWarning($"[UIScreenManager] UXML introuvable : Resources/{resourcePath}.uxml");
                continue;
            }
            VisualElement instance = asset.Instantiate();
            instance.style.flexGrow = 1;
            instance.style.position = Position.Absolute;
            instance.style.left = 0; instance.style.right = 0; instance.style.top = 0; instance.style.bottom = 0;
            instance.style.display = DisplayStyle.None;
            // Ce wrapper (TemplateContainer créé par Instantiate) n'est pas l'élément "root" déclaré
            // dans chaque UXML — c'est un conteneur plein écran supplémentaire ajouté par-dessus.
            // Sans Ignore ici, il reste pickable par défaut et plein écran en permanence dès que
            // l'écran est affiché, ce qui absorbe tous les clics/taps même là où le contenu réel de
            // l'écran (ex: DeploymentDock) n'occupe qu'un coin — chaque UXML gère déjà lui-même le
            // picking-mode de son propre contenu, ce wrapper ne doit jamais interférer.
            instance.pickingMode = PickingMode.Ignore;

            // La propriété USS "picking-mode: ...;" déclarée dans le style inline d'un UXML ne
            // s'applique pas de façon fiable au premier Instantiate() — vérifié empiriquement : un
            // clic continue de trouver l'élément en PickingMode.Position malgré une déclaration
            // "Ignore" dans le UXML. Ce n'était corrigé ici QUE pour l'élément nommé "root" de
            // chaque écran (une ligne, un nom) — tout le reste de l'arbre de CHAQUE UXML restait
            // exposé au même bug, silencieusement. Rapport joueur du 2026-09-19 : "top-bar" dans
            // InMatchHudScreen.uxml (déclaré "picking-mode: Ignore", un simple conteneur de mise en
            // page autour de la bannière d'équipe) absorbait quand même des taps destinés à la
            // scène 3D, exactement ce bug, juste sur un nom différent — et rien ne garantit qu'il
            // soit le seul dans les ~20 écrans du projet — voir ForcePickingModeIgnore ci-dessous
            // pour pourquoi ceci reste une liste de noms plutôt qu'une détection automatique.
            ForcePickingModeIgnore(instance, "root", "top-bar");

            // Transition d'apparition (2026-09-13) — voir Theme.tss ".screen-fade" pour le pourquoi.
            // État de départ "invisible/légèrement réduit" : Show()/SetVisible() l'amènent à
            // opacity:1/scale:1 sur UNE FRAME PLUS TARD (jamais la même frame que display:Flex),
            // sans quoi la transition USS n'a rien à animer.
            instance.AddToClassList("screen-fade");
            instance.style.opacity = 0f;
            instance.style.scale = new StyleScale(new Scale(new Vector3(0.97f, 0.97f, 1f)));

            root.Add(instance);
            screens[name] = instance;
        }

        BuildDebugOverlay(root);
    }

    /// <summary>Force PickingMode.Ignore sur les éléments nommés de <paramref name="instance"/> : la
    /// valeur "picking-mode" déclarée en UXML n'est pas toujours respectée (constaté pour le "root" de
    /// chaque écran et le "top-bar" du HUD) et n'est pas lisible depuis le code. Ajouter ici tout
    /// nouvel élément touché par ce défaut.</summary>
    private static void ForcePickingModeIgnore(VisualElement instance, params string[] elementNames)
    {
        foreach (string elementName in elementNames)
        {
            VisualElement found = instance.Q<VisualElement>(elementName);
            if (found != null) found.pickingMode = PickingMode.Ignore;
        }
    }

    // --- Outil de vérification (diagnostic écrans superposés) ---------------------------------
    // Étiquette toujours au sommet de la pile (ajoutée en dernier) listant les écrans actuellement
    // en display:Flex — sert à confirmer/infirmer empiriquement un chevauchement plutôt que de le
    // deviner depuis une capture d'écran. Désactivable via DebugOverlayEnabled.
    public static bool DebugOverlayEnabled = false;
    private Label debugOverlayLabel;

    private void BuildDebugOverlay(VisualElement root)
    {
        debugOverlayLabel = new Label();
        debugOverlayLabel.style.position = Position.Absolute;
        debugOverlayLabel.style.left = 4;
        debugOverlayLabel.style.top = 4;
        debugOverlayLabel.style.color = Color.yellow;
        debugOverlayLabel.style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.6f));
        debugOverlayLabel.style.fontSize = 16;
        debugOverlayLabel.style.paddingLeft = 4;
        debugOverlayLabel.style.paddingRight = 4;
        debugOverlayLabel.pickingMode = PickingMode.Ignore;
        debugOverlayLabel.style.display = DebugOverlayEnabled ? DisplayStyle.Flex : DisplayStyle.None;
        root.Add(debugOverlayLabel);
    }

    // Résolution au dernier calcul du padding de zone sûre (voir ApplySafeAreaPadding) — Awake()
    // tourne parfois avant que la fenêtre Android n'ait fini de se dimensionner (barres système pas
    // encore posées), donc Screen.width/height à cet instant peuvent différer de la résolution
    // réelle finale. Un padding calculé en pourcentage sur cette taille provisoire reste ensuite
    // figé à un pourcentage devenu FAUX une fois la fenêtre stabilisée sur sa vraie taille : tout le
    // reste de la mise en page (bottom:X%, etc.) se recalcule en continu sur la bonne résolution,
    // mais pas ce padding — d'où un décalage grandissant entre la position VISUELLE d'un élément
    // ancré en bord d'écran (qui suit le padding figé) et sa zone RÉELLEMENT cliquable (worldBound,
    // qui suit le padding + la vraie résolution) — confirmé en dur sur le bouton "DÉPLOIEMENT" du
    // dock (cliquable uniquement près du vrai bord bas de l'écran, rendu visuellement ~35% plus haut).
    private int lastSafeAreaScreenW = -1;
    private int lastSafeAreaScreenH = -1;

    private void Update()
    {
        if (Screen.width != lastSafeAreaScreenW || Screen.height != lastSafeAreaScreenH)
        {
            ApplyUiScale();
            ApplySafeAreaPadding(RootVisualElement);
        }

        if (debugOverlayLabel == null) return;
        debugOverlayLabel.style.display = DebugOverlayEnabled ? DisplayStyle.Flex : DisplayStyle.None;
        if (!DebugOverlayEnabled) return;

        var visible = new List<string>();
        foreach (var kv in screens)
            if (kv.Value.style.display == DisplayStyle.Flex) visible.Add(kv.Key);
        debugOverlayLabel.text = "ÉCRANS VISIBLES: " + (visible.Count > 0 ? string.Join(", ", visible) : "(aucun)");
    }

    /// <summary>Petit côté de l'écran pour lequel toutes les tailles en px des écrans (polices de
    /// Theme.tss, hauteurs de boutons...) ont été réglées : un téléphone 1080p.</summary>
    private const float DesignShortSidePx = 1080f;

    /// <summary>Échelle de l'UI proportionnelle au PETIT côté de l'écran (référence 1080 px) : identique sur
    /// un téléphone 1080p, réduite dans une petite fenêtre PC (le texte y paraissait énorme en « Constant
    /// Pixel Size »), agrandie sur tablette.</summary>
    private void ApplyUiScale()
    {
        PanelSettings settings = GetComponent<UIDocument>()?.panelSettings;
        if (settings == null || Screen.width <= 0 || Screen.height <= 0) return;
        settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
        settings.referenceResolution = new Vector2Int((int)DesignShortSidePx, (int)DesignShortSidePx);
        settings.match = Screen.width >= Screen.height ? 1f : 0f; // 1 = hauteur (paysage), 0 = largeur (portrait)
    }

    /// <summary>Écarte l'interface des zones non sûres de l'écran (encoche, coins arrondis, barre de
    /// navigation), en POURCENTAGE de l'écran ; recalculé quand la taille de l'écran change réellement
    /// (au lancement, la fenêtre Android n'a pas toujours sa taille finale).</summary>
    private void ApplySafeAreaPadding(VisualElement root)
    {
        lastSafeAreaScreenW = Screen.width;
        lastSafeAreaScreenH = Screen.height;
        if (Screen.width <= 0 || Screen.height <= 0) return;

        Rect safe = Screen.safeArea;
        float leftPct = (safe.xMin / Screen.width) * 100f;
        float rightPct = ((Screen.width - safe.xMax) / Screen.width) * 100f;
        float bottomPct = (safe.yMin / Screen.height) * 100f;
        float topPct = ((Screen.height - safe.yMax) / Screen.height) * 100f;

        root.style.paddingLeft = new StyleLength(Length.Percent(leftPct));
        root.style.paddingRight = new StyleLength(Length.Percent(rightPct));
        root.style.paddingTop = new StyleLength(Length.Percent(topPct));
        root.style.paddingBottom = new StyleLength(Length.Percent(bottomPct));
    }

    /// <summary>Renvoie la racine d'un écran pour que son contrôleur y fasse ses Query&lt;T&gt;() et bindings d'événements.</summary>
    public VisualElement GetScreen(string name) => screens.TryGetValue(name, out var el) ? el : null;

    private static readonly Scale ScreenScaleHidden = new Scale(new Vector3(0.97f, 0.97f, 1f));
    private static readonly Scale ScreenScaleShown = new Scale(Vector3.one);

    /// <summary>Rend un écran instantanément invisible (pas d'animation de sortie — voir FadeIn pour
    /// pourquoi seule l'ENTRÉE est animée) et réinitialise son opacity/scale à l'état "caché" pour la
    /// prochaine fois qu'il sera montré.</summary>
    private static void HideInstant(VisualElement el)
    {
        el.style.display = DisplayStyle.None;
        el.style.opacity = 0f;
        el.style.scale = new StyleScale(ScreenScaleHidden);
    }

    /// <summary>Anime l'apparition (opacity+scale, voir Theme.tss ".screen-fade") — display:Flex est
    /// posé IMMÉDIATEMENT (sinon l'écran ne fait rien du tout), mais opacity/scale ne passent à leur
    /// valeur finale qu'un tick plus tard : UI Toolkit n'anime jamais un changement de style appliqué
    /// dans le même passage que la valeur de départ, il faut que la valeur de départ ait été rendue au
    /// moins une fois avant que la cible ne change (même contrainte qu'en CSS web).</summary>
    private static void FadeIn(VisualElement el)
    {
        el.style.display = DisplayStyle.Flex;
        el.style.opacity = 0f;
        el.style.scale = new StyleScale(ScreenScaleHidden);
        el.schedule.Execute(() =>
        {
            el.style.opacity = 1f;
            el.style.scale = new StyleScale(ScreenScaleShown);
        }).ExecuteLater(1);
    }

    /// <summary>Affiche un seul écran (avec une animation d'apparition, voir FadeIn), masque tous les
    /// autres écrans "plein cadre" gérés ici (instantanément — seule l'ENTRÉE est animée, une sortie
    /// animée obligerait à retarder display:None après la fin de la transition pour chaque écran
    /// qu'on quitte, complexité non justifiée ici puisqu'un nouvel écran plein cadre le recouvre de
    /// toute façon immédiatement).</summary>
    public void Show(string name)
    {
        foreach (var kv in screens)
        {
            if (kv.Key != name) HideInstant(kv.Value);
        }
        if (screens.TryGetValue(name, out var el)) FadeIn(el);
    }

    /// <summary>Affiche/masque un écran indépendamment des autres (ex: superposer une bannière sur le HUD).
    /// IDEMPOTENT (2026-09-13, correctif "icônes qui vibrent") : plusieurs appelants (TacticalPathManager_UI.
    /// Update -> TacticalBottomBar/ContextMenu, UnitSpawnerUI.RefreshDeploymentDockUI -> DeploymentDock)
    /// réaffirment volontairement la même visibilité À CHAQUE FRAME (voir leurs commentaires respectifs :
    /// c'est délibéré, pour survivre à un Show() ailleurs qui masquerait cet écran une fois pour toutes).
    /// Sans ce garde, chaque frame relançait FadeIn() depuis zéro (opacity 0 -> planifié 1 une frame plus
    /// tard -> re-remis à 0 la frame suivante avant même d'y arriver) : un scintillement continu à 60 Hz
    /// sur CHAQUE écran concerné, exactement ce qui ressemblait à des "icônes qui vibrent" en jeu.</summary>
    public void SetVisible(string name, bool visible)
    {
        if (!screens.TryGetValue(name, out var el)) return;
        bool currentlyVisible = el.style.display == DisplayStyle.Flex;
        if (visible == currentlyVisible) return;
        if (visible) FadeIn(el); else HideInstant(el);
    }

    /// <summary>Vrai si l'écran est actuellement affiché (display:Flex).</summary>
    public bool IsVisible(string name) => screens.TryGetValue(name, out var el) && el.style.display == DisplayStyle.Flex;

    public void HideAll()
    {
        foreach (var kv in screens) HideInstant(kv.Value);
    }
}
