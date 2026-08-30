using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Build Android rapide pour un test personnel à 2 téléphones (signature debug auto-générée par
/// Unity, pas de keystore custom) — voir Assets/_ServerDocs/multiplayer/08-known-issues-and-todo.md
/// §8 "Build Android : stripEngineCode..." pour le contexte : avec Custom Keystore actif, Unity ne
/// copie le fichier .keystore dans le projet Gradle exporté QUE pour un build Release, jamais pour
/// un Development Build — même si sa config de signature Debug pointe vers ce même keystore. D'où
/// la désactivation explicite ci-dessous plutôt que de laisser le réglage du projet tel quel.
///
/// Utilisation en ligne de commande (Éditeur fermé) :
///   Unity.exe -batchmode -nographics -quit -projectPath "<chemin du projet>"
///     -executeMethod AndroidTestBuildScript.BuildDebugApk -logFile android_test_build.log
/// </summary>
public static class AndroidTestBuildScript
{
    public static void BuildDebugApk()
    {
        string outputPath = "build/Android/Novgov-Test.apk";

        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            Debug.LogError("[AndroidTestBuildScript] Aucune scène activée dans Build Settings — impossible de builder.");
            EditorApplication.Exit(1);
            return;
        }

        // Signature debug auto-générée (pas de mot de passe à saisir, impossible en batch mode de
        // toute façon) — largement suffisant pour installer sur 2 téléphones personnels via ADB,
        // aucune review Play Store en jeu ici.
        PlayerSettings.Android.useCustomKeystore = false;

        EditorUserBuildSettings.buildAppBundle = false; // .apk, pas .aab — nécessaire pour sideload direct
        EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.Android,
            options = BuildOptions.Development
        };

        var report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.LogError($"[AndroidTestBuildScript] Échec du build : {report.summary.result} ({report.summary.totalErrors} erreurs).");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log($"[AndroidTestBuildScript] Build Android réussi : {outputPath} ({report.summary.totalSize / (1024 * 1024)} Mo).");
    }
}
