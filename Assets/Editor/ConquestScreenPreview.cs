using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Captures de l'écran CONQUÊTE (2026-09-30) sans téléphone, sans compte et sans serveur de jeu :
/// lance la scène en Play Mode, affiche l'écran avec des propriétaires fictifs (voir
/// Novgov.UI.ZoneMapController.EditorPreview — les tuiles OpenStreetMap sont les vraies), rend l'UI
/// dans une RenderTexture à des résolutions de téléphone (paysage ET portrait — l'appli tourne dans
/// les deux sens) et écrit des PNG, puis quitte l'Éditeur.
///
///   Unity.exe -projectPath "..." -executeMethod ConquestScreenPreview.Run -previewOut "C:\dossier"
///
/// Pas en -batchmode : il faut le rendu GPU du Play Mode.
/// </summary>
[InitializeOnLoad]
public static class ConquestScreenPreview
{
    private const string OutDirPrefKey = "Novgov.ConquestPreview.OutDir";
    private const string ActivePrefKey = "Novgov.ConquestPreview.Active";

    private static readonly (string name, int w, int h, int tab, int selDx, int selDy)[] Shots =
    {
        ("paysage_carte_ennemi", 2400, 1080, 0, 1, 0),
        ("paysage_carte_libre", 2400, 1080, 0, 0, -1),
        ("portrait_carte_libre", 1080, 2400, 0, -1, 0),
        ("paysage_gestion", 2400, 1080, 1, 0, 0),
        ("portrait_gestion", 1080, 2400, 1, 0, 0),
    };

    static ConquestScreenPreview()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    public static void Run()
    {
        string outDir = null;
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-previewOut") outDir = args[i + 1];
        if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(Application.dataPath, "..", "build", "preview");
        Directory.CreateDirectory(outDir);
        EditorPrefs.SetString(OutDirPrefKey, outDir);
        EditorPrefs.SetBool(ActivePrefKey, true);

        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        EditorApplication.EnterPlaymode();
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || !EditorPrefs.GetBool(ActivePrefKey, false)) return;
        EditorPrefs.SetBool(ActivePrefKey, false);
        var go = new GameObject("ConquestScreenPreviewRunner");
        go.AddComponent<Runner>().outDir = EditorPrefs.GetString(OutDirPrefKey);
    }

    private class Runner : MonoBehaviour
    {
        public string outDir;

        private System.Collections.IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(3f); // UIScreenManager + menu de démarrage prêts

            var ui = UIScreenManager.Instance;
            var doc = ui.GetComponent<UIDocument>();
            // Copie des PanelSettings : ne jamais modifier l'asset partagé (GamePanelSettings) en Play Mode.
            var settings = Object.Instantiate(doc.panelSettings);
            doc.panelSettings = settings;

            var zm = Novgov.Generation.ZoneManager.EnsureInstance();
            int cx = zm.HasHomeZone ? zm.HomeTileX : 66648, cy = zm.HasHomeZone ? zm.HomeTileY : 44111;
            var map = Novgov.UI.ZoneMapController.EnsureInstance();

            ui.Show("Conquest");
            VisualElement root = ui.GetScreen("Conquest");
            root.Q<Label>("lbl-username").text = "Commandant Test";
            root.Q<Label>("lbl-action-points").text = "250 PA";
            var notifBadge = root.Q<Label>("lbl-notifications-badge");
            notifBadge.text = "2"; notifBadge.style.display = DisplayStyle.Flex;

            foreach (var shot in Shots)
            {
                var rt = new RenderTexture(shot.w, shot.h, 24, RenderTextureFormat.ARGB32);
                rt.Create();
                settings.targetTexture = rt;

                if (shot.tab == 0) map.EditorPreview(cx, cy, (shot.selDx, shot.selDy));
                else map.EditorPreviewTab(Novgov.UI.ZoneMapController.Tab.Manage);

                // Laisse le temps : mise en page responsive (GeometryChangedEvent), fondu d'apparition,
                // téléchargement des tuiles OpenStreetMap.
                yield return new WaitForSecondsRealtime(shot.name == Shots[0].name ? 8f : 2.5f);
                yield return new WaitForEndOfFrame();

                RenderTexture.active = rt;
                var tex = new Texture2D(shot.w, shot.h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, shot.w, shot.h), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(outDir, shot.name + ".png"), tex.EncodeToPNG());
                Debug.Log($"[ConquestScreenPreview] {shot.name}.png écrit ({shot.w}x{shot.h}).");
                Destroy(tex);
                settings.targetTexture = null;
                rt.Release();
            }

            Debug.Log("[ConquestScreenPreview] Terminé.");
            EditorApplication.ExitPlaymode();
            EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }
    }
}
