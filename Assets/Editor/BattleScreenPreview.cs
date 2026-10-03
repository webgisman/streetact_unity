#if !UNITY_SERVER
// Outil client uniquement (l'assembly Éditeur est aussi compilée avec UNITY_SERVER pour le serveur).
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Captures d'une partie SOLO telle que le joueur la voit (vue 3D + interface), sans téléphone :
/// menu de démarrage, déploiement, unité sélectionnée, menu d'ordre — en paysage téléphone et en
/// fenêtre PC. Sert à vérifier la lisibilité de l'interface de bataille après une modification.
///
///   Unity.exe -projectPath "..." -executeMethod BattleScreenPreview.Run -previewOut "C:\dossier"
///
/// Pas en -batchmode : il faut le rendu GPU du Play Mode. Le radar (IMGUI) n'apparaît pas sur les
/// captures, seule l'interface UI Toolkit est composée par-dessus la vue 3D.
/// </summary>
[InitializeOnLoad]
public static class BattleScreenPreview
{
    private const string OutDirPrefKey = "Novgov.BattlePreview.OutDir";
    private const string ActivePrefKey = "Novgov.BattlePreview.Active";

    private static readonly (string suffix, int w, int h)[] Sizes =
    {
        ("telephone", 2400, 1080),
        ("pc", 1280, 720),
    };

    static BattleScreenPreview()
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
        new GameObject("BattleScreenPreviewRunner").AddComponent<Runner>().outDir = EditorPrefs.GetString(OutDirPrefKey);
    }

    private class Runner : MonoBehaviour
    {
        public string outDir;
        private PanelSettings settings;

        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(3f);
            var doc = UIScreenManager.Instance.GetComponent<UIDocument>();
            // Copie : ne jamais modifier l'asset partagé en Play Mode.
            settings = Object.Instantiate(doc.panelSettings);
            settings.clearColor = true;
            settings.colorClearValue = new Color(0f, 0f, 0f, 0f);
            doc.panelSettings = settings;

            yield return Shoot("01_menu_demarrage");

            // JOUER SOLO, comme le bouton du menu (GameManagerUI.BindStartupUI).
            Click(UIScreenManager.Instance.GetScreen("StartupMenu"), "btn-offline");
            yield return new WaitForSecondsRealtime(8f);
            yield return Shoot("02_solo_deploiement");

            // PLACER MES TROUPES AUTOMATIQUEMENT puis COMMENCER LA BATAILLE, comme le joueur.
            Click(UIScreenManager.Instance.GetScreen("DeploymentDock"), "btn-auto-deploy");
            yield return new WaitForSecondsRealtime(3f);
            yield return Shoot("03_solo_troupes_placees");
            Click(UIScreenManager.Instance.GetScreen("DeploymentDock"), "btn-confirm-deployment");
            yield return new WaitForSecondsRealtime(2f);
            yield return Shoot("03b_solo_bataille_commencee");

            UnitAI mine = null;
            foreach (var u in UnitAI.AllLivingUnits)
                if (u != null && u.teamID == 1 && !u.isTank && !u.isMortar) { mine = u; break; }
            var tpm = TacticalPathManager.Instance;
            if (mine != null && tpm != null)
            {
                typeof(TacticalPathManager).GetMethod("SelectionnerUnite", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(tpm, new object[] { mine.gameObject });
                yield return new WaitForSecondsRealtime(1.5f);
                yield return Shoot("04_unite_selectionnee");

                // Un tap au sol 12 m devant l'unité, par un vrai rayon depuis la caméra.
                Vector3 ground = mine.transform.position + mine.transform.forward * 12f;
                Vector3 cam = Camera.main.transform.position;
                if (Physics.Raycast(cam, (ground - cam).normalized, out RaycastHit hit, 500f))
                {
                    typeof(TacticalPathManager).GetMethod("RouteTapOnWorld", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(tpm, new object[] { hit, true, hit.point });
                    yield return new WaitForSecondsRealtime(1.5f);
                    yield return Shoot("05_menu_ordre");
                }

                // ANNULER, puis FIN DE TOUR deux fois (la première affiche l'avertissement « unités
                // sans ordre »), comme le joueur.
                Click(UIScreenManager.Instance.GetScreen("ContextMenu"), "cancel-button");
                Click(UIScreenManager.Instance.GetScreen("TacticalBottomBar"), "end-turn-button");
                yield return new WaitForSecondsRealtime(0.5f);
                yield return Shoot("06_avertissement_fin_de_tour");
                // Les captures ci-dessus dépassent la fenêtre de confirmation : nouvel appui puis confirmation.
                tpm.LancerExecutionTour();
                tpm.LancerExecutionTour();
                yield return new WaitForSecondsRealtime(1f);
                yield return Shoot("07_tour_en_cours");
                Debug.Log($"[BattleScreenPreview] Unités vivantes : équipe 1 = {CountTeam(1)}, équipe 2 (IA) = {CountTeam(2)}.");
            }

            Debug.Log("[BattleScreenPreview] Terminé.");
            EditorApplication.ExitPlaymode();
            EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }

        private static int CountTeam(int team)
        {
            int n = 0;
            foreach (var u in UnitAI.AllLivingUnits) if (u != null && !u.isDead && u.teamID == team) n++;
            return n;
        }

        /// <summary>Appuie sur un bouton de l'interface comme le ferait le joueur.</summary>
        private static void Click(VisualElement screen, string buttonName)
        {
            Button button = screen.Q<Button>(buttonName);
            using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = button; button.SendEvent(submit); }
        }

        private IEnumerator Shoot(string name)
        {
            foreach (var (suffix, w, h) in Sizes)
            {
                var uiRt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                uiRt.Create();
                settings.targetTexture = uiRt;
                settings.match = w >= h ? 1f : 0f; // même règle que UIScreenManager.ApplyUiScale
                yield return new WaitForSecondsRealtime(1.2f); // mise en page responsive
                yield return new WaitForEndOfFrame();

                Texture2D ui = Read(uiRt, w, h);
                var sceneRt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                Camera main = Camera.main;
                RenderTexture previous = main.targetTexture;
                main.targetTexture = sceneRt;
                main.Render();
                main.targetTexture = previous;
                Texture2D scene = Read(sceneRt, w, h);

                Color[] s = scene.GetPixels(), u = ui.GetPixels();
                for (int i = 0; i < s.Length; i++) s[i] = Color.Lerp(s[i], new Color(u[i].r, u[i].g, u[i].b, 1f), u[i].a);
                scene.SetPixels(s);
                scene.Apply();
                File.WriteAllBytes(Path.Combine(outDir, $"{name}_{suffix}.png"), scene.EncodeToPNG());
                Debug.Log($"[BattleScreenPreview] {name}_{suffix}.png écrit.");

                settings.targetTexture = null;
                uiRt.Release();
                sceneRt.Release();
                Destroy(ui);
                Destroy(scene);
            }
        }

        private static Texture2D Read(RenderTexture rt, int w, int h)
        {
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            return tex;
        }
    }
}
#endif
