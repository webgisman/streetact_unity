using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Build headless du serveur de jeu Linux. Réutilise EXACTEMENT les mêmes scènes que le build
/// client (voir Assets/_ServerDocs/multiplayer/04-unity-headless-server.md, "Séparation
/// client/serveur dans le code" — une seule scène partagée, du code isolé par #if UNITY_SERVER
/// là où c'était nécessaire, pas de scène serveur séparée).
///
/// IMPORTANT — build Standalone classique, PAS le sous-cible "Dedicated Server" :
/// `StandaloneBuildSubtarget.Server` provoque, de façon reproductible sur ce projet précis, des
/// erreurs de compilation CS0121 "ambiguous method" dans des packages Unity (InputSystem, TMP,
/// Collections) sans rapport avec le code du projet — piste explorée en détail (cache Bee
/// projet ET global, PackageCache, réimport complet, résolution de version de com.unity.collections,
/// retrait de com.unity.multiplayer.center) sans résolution fiable. On construit donc un Standalone
/// Linux64 ordinaire, lancé ensuite avec "-batchmode -nographics" au runtime (voir Dockerfile) —
/// c'est l'approche standard utilisée pour les serveurs headless Unity avant même l'existence du
/// sous-cible officiel "Dedicated Server", et elle fonctionne nativement puisque tout le code
/// gameplay tourne indépendamment du rendu. Conséquence : UNITY_SERVER n'est jamais défini, donc
/// les gardes #if UNITY_SERVER ajoutés dans GameManagerUI/UnitSpawnerUI/TacticalPathManager ne
/// s'activent pas — sans incidence : IMGUI ne plante pas en -nographics, il ne fait juste rien
/// d'utile visuellement, ce qui est exactement le comportement voulu côté serveur.
///
/// Utilisation en ligne de commande :
///   Unity.exe -batchmode -nographics -quit -buildTarget Linux64 -projectPath "<chemin du projet>" -executeMethod ServerBuildScript.BuildLinuxServer -logFile build_server.log
/// </summary>
public static class ServerBuildScript
{
    public static void BuildLinuxServer()
    {
        string outputPath = "build/LinuxServer/StreetActServer.x86_64";

        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            Debug.LogError("[ServerBuildScript] Aucune scène activée dans Build Settings — impossible de builder.");
            EditorApplication.Exit(1);
            return;
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.None
        };

        var report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.LogError($"[ServerBuildScript] Échec du build : {report.summary.result} ({report.summary.totalErrors} erreurs).");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log($"[ServerBuildScript] Build serveur réussi : {outputPath} ({report.summary.totalSize / (1024 * 1024)} Mo).");
    }
}
