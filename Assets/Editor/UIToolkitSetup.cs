using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Génère le PanelSettings requis par le UIDocument runtime (voir Assets/UI/UIScreenManager.cs).
/// Créé via l'API plutôt qu'écrit à la main : un PanelSettings est un ScriptableObject sérialisé
/// dont on veut être certain qu'il correspond exactement à ce que cette version d'Unity attend,
/// plutôt que de risquer un YAML fait main.
/// À exécuter une seule fois (menu Novgov > UI > Créer le PanelSettings).
/// </summary>
public static class UIToolkitSetup
{
    // Sous Resources/ pour que UIBootstrap.cs puisse le charger au runtime via Resources.Load
    // sans dépendre d'une référence de scène (le UIDocument est créé entièrement par code).
    private const string PanelSettingsPath = "Assets/Resources/UI/GamePanelSettings.asset";
    private const string ThemePath = "Assets/UI/Theme.tss";

    [MenuItem("Novgov/UI/Créer le PanelSettings")]
    public static void CreatePanelSettings()
    {
        if (!Directory.Exists("Assets/Resources/UI"))
            Directory.CreateDirectory("Assets/Resources/UI");

        PanelSettings settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<PanelSettings>();
            AssetDatabase.CreateAsset(settings, PanelSettingsPath);
            Debug.Log($"[UIToolkitSetup] PanelSettings créé : {PanelSettingsPath}");
        }

        settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        settings.referenceResolution = new Vector2Int(1080, 1920);
        settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
        settings.match = 0.5f;

        ThemeStyleSheet theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
        if (theme != null)
        {
            settings.themeStyleSheet = theme;
        }
        else
        {
            Debug.LogWarning($"[UIToolkitSetup] {ThemePath} introuvable — relance ce menu après import du thème, ou vérifie qu'Unity a bien importé le fichier .tss.");
        }

        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
        Debug.Log("[UIToolkitSetup] PanelSettings configuré (ScaleWithScreenSize, 1080x1920, thème attaché).");
    }
}
