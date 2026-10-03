using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Crée le GameObject UIDocument/UIScreenManager entièrement par code au démarrage — pas besoin
/// de le placer à la main dans chaque scène, et rien à câbler manuellement dans l'Éditeur au-delà
/// du PanelSettings (voir Assets/Editor/UIToolkitSetup.cs, menu Novgov > UI > Créer le
/// PanelSettings, à exécuter une fois).
/// </summary>
public static class UIBootstrap
{
#if !UNITY_SERVER
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init()
    {
        if (UIScreenManager.Instance != null) return;

        PanelSettings settings = Resources.Load<PanelSettings>("UI/GamePanelSettings");
        if (settings == null)
        {
            Debug.LogError("[UIBootstrap] Resources/UI/GamePanelSettings introuvable — dans l'Éditeur, lance le menu Novgov > UI > Créer le PanelSettings une fois avant de builder.");
            return;
        }

        // Copie : l'échelle est ajustée à l'exécution (orientation, voir UIScreenManager.ApplyUiScale)
        // — modifier l'asset partagé en Play Mode l'aurait réécrit sur le disque.
        settings = Object.Instantiate(settings);

        GameObject go = new GameObject("UIRoot");
        Object.DontDestroyOnLoad(go);
        UIDocument doc = go.AddComponent<UIDocument>();
        doc.panelSettings = settings;
        go.AddComponent<UIScreenManager>();
    }
#endif
}
