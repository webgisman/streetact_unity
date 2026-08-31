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
        ("ModeSelect", "UI/ModeSelectScreen"),
        ("ZoneMap", "UI/ZoneMapScreen"),
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

            // La propriété USS "picking-mode: Ignore;" déclarée dans le style inline de chaque
            // UXML (sur son propre élément "root") ne s'applique pas de façon fiable — vérifié
            // empiriquement : un clic continue de la trouver en PickingMode.Position malgré la
            // déclaration USS. On le force donc ici en C#, qui n'a besoin d'aucun parsing de
            // feuille de style pour être correct. Ne concerne QUE l'élément nommé "root" propre à
            // l'écran (le contenu réel à l'intérieur gère son propre picking, ex: chaque Button).
            VisualElement innerRoot = instance.Q<VisualElement>("root");
            if (innerRoot != null) innerRoot.pickingMode = PickingMode.Ignore;

            root.Add(instance);
            screens[name] = instance;
        }

        BuildDebugOverlay(root);
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
            ApplySafeAreaPadding(RootVisualElement);

        if (debugOverlayLabel == null) return;
        debugOverlayLabel.style.display = DebugOverlayEnabled ? DisplayStyle.Flex : DisplayStyle.None;
        if (!DebugOverlayEnabled) return;

        var visible = new List<string>();
        foreach (var kv in screens)
            if (kv.Value.style.display == DisplayStyle.Flex) visible.Add(kv.Key);
        debugOverlayLabel.text = "ÉCRANS VISIBLES: " + (visible.Count > 0 ? string.Join(", ", visible) : "(aucun)");
    }

    /// <summary>
    /// Sur beaucoup d'appareils Android/iOS, une zone de l'écran n'est pas "sûre" (encoche caméra,
    /// coins arrondis, barre de navigation gestuelle en bas) et peut recouvrir purement et
    /// simplement des éléments ancrés en bord d'écran (ex: le bouton "Fin de tour" à 16px du bord
    /// bas-droit) — invisible dans l'Éditeur, où cette zone n'existe pas, donc jamais repéré en dev.
    /// On calcule l'inset ici en POURCENTAGE de l'écran (jamais en pixels/points) : ça évite toute
    /// conversion écran→panel dépendante du mode de scaling du PanelSettings, puisque
    /// Screen.safeArea et Screen.width/height utilisent déjà le même repère. Rappelé depuis Update()
    /// (voir lastSafeAreaScreenW/H) à chaque fois que Screen.width/height changent réellement — pas
    /// seulement une fois dans Awake(), qui peut tourner avant que la fenêtre Android n'ait fini de
    /// se dimensionner et fige alors un pourcentage calculé sur une résolution provisoire.
    /// </summary>
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

    /// <summary>Affiche un seul écran, masque tous les autres écrans "plein cadre" gérés ici.</summary>
    public void Show(string name)
    {
        foreach (var kv in screens) kv.Value.style.display = DisplayStyle.None;
        if (screens.TryGetValue(name, out var el)) el.style.display = DisplayStyle.Flex;
    }

    /// <summary>Affiche/masque un écran indépendamment des autres (ex: superposer une bannière sur le HUD).</summary>
    public void SetVisible(string name, bool visible)
    {
        if (screens.TryGetValue(name, out var el)) el.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }

    public void HideAll()
    {
        foreach (var kv in screens) kv.Value.style.display = DisplayStyle.None;
    }
}
