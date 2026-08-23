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

    private readonly Dictionary<string, VisualElement> screens = new Dictionary<string, VisualElement>();

    // Nom logique -> chemin Resources (sans extension) du UXML.
    private static readonly (string name, string resourcePath)[] ScreenDefinitions =
    {
        ("StartupMenu", "UI/StartupMenuScreen"),
        ("ModeSelect", "UI/ModeSelectScreen"),
        ("Auth", "UI/AuthScreen"),
        ("Waiting", "UI/WaitingScreen"),
        ("InMatchHud", "UI/InMatchHudScreen"),
        ("MatchOver", "UI/MatchOverScreen"),
        ("Leaderboard", "UI/LeaderboardScreen"),
        ("ActionViewBack", "UI/ActionViewBackScreen"),
        ("DeploymentDock", "UI/DeploymentDockScreen"),
        ("TacticalBottomBar", "UI/TacticalBottomBarScreen"),
        ("ContextMenu", "UI/ContextMenuScreen"),
    };

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        var document = GetComponent<UIDocument>();
        VisualElement root = document.rootVisualElement;
        root.style.flexGrow = 1;
        root.pickingMode = PickingMode.Ignore; // laisse passer les clics vers la scène 3D là où aucun écran n'est actif

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
            root.Add(instance);
            screens[name] = instance;
        }
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
