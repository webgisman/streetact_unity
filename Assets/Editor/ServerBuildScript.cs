using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Build headless du serveur de jeu Linux. Réutilise EXACTEMENT les mêmes scènes que le build
/// client (voir Assets/_ServerDocs/multiplayer/04-unity-headless-server.md, "Séparation
/// client/serveur dans le code" — une seule scène partagée, du code isolé par #if UNITY_SERVER
/// là où c'était nécessaire, pas de scène serveur séparée).
///
/// Sous-cible "Dedicated Server" (StandaloneBuildSubtarget.Server), et non plus un Standalone
/// classique : sur CETTE machine, seul le module "Linux Dedicated Server Build Support" est
/// réellement et complètement installé (vérifié en listant Editor/Data/PlaybackEngines/
/// LinuxStandaloneSupport/Variations — les 4 variantes linux64_server_{development,nondevelopment}
/// _{mono,il2cpp} sont complètes avec LinuxPlayer/UnityPlayer.so, alors que
/// linux64_player_development_mono n'est qu'un fragment sans binaire jouable, et qu'il n'existe
/// aucune variante "player_nondevelopment" du tout — le module "Linux Build Support" desktop n'a
/// jamais été installé sur cet Éditeur, seul celui du serveur dédié l'a été). Un ancien essai avait
/// écarté ce sous-cible à cause d'erreurs CS0121 "ambiguous method" (InputSystem/TMP/Collections)
/// qui ne se sont PAS reproduites lors des essais les plus récents : elles semblent avoir été liées
/// à un état de cache transitoire de cette session-là plutôt qu'à ce sous-cible en lui-même.
/// Ce choix définit UNITY_SERVER, dont dépendent les gardes #if UNITY_SERVER / #if !UNITY_SERVER
/// du projet (GameManagerUI/UnitSpawnerUI/TacticalPathManager/MultiplayerMatchController) pour
/// exclure tout code UI Toolkit clientonly de la compilation serveur.
///
/// Utilisation en ligne de commande :
///   Unity.exe -batchmode -nographics -quit -buildTarget Linux64 -projectPath "<chemin du projet>" -executeMethod ServerBuildScript.BuildLinuxServer -logFile build_server.log
/// </summary>
public static class ServerBuildScript
{
    public static void BuildLinuxServer()
    {
        string outputPath = "build/LinuxServer/NovgovServer.x86_64";

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

        // Forcé explicitement (ce réglage d'Éditeur est persistant d'une session à l'autre) pour
        // garantir que BuildPipeline.BuildPlayer() utilise bien la sous-cible Dedicated Server,
        // quel que soit l'état hérité de la session précédente.
        UnityEditor.EditorUserBuildSettings.standaloneBuildSubtarget = UnityEditor.StandaloneBuildSubtarget.Server;

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)UnityEditor.StandaloneBuildSubtarget.Server,
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
