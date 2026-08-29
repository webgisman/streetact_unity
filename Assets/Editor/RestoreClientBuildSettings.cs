using UnityEditor;
using UnityEngine;

/// <summary>
/// Utilitaire ponctuel : remet la sous-cible Standalone sur "Player" (client normal) après un build
/// serveur (voir ServerBuildScript.cs, qui force explicitement StandaloneBuildSubtarget.Server —
/// réglage persistant d'une session Éditeur à l'autre dans Library/EditorUserBuildSettings.asset).
/// Sans ça, Play Mode dans l'Éditeur compile avec UNITY_SERVER défini et tout le code client
/// (#if !UNITY_SERVER : GameManagerUI, menu de démarrage, etc.) est absent de la compilation.
///
/// Utilisation en ligne de commande (Éditeur fermé, aucune instance ouverte sur ce projet) :
///   Unity.exe -batchmode -nographics -quit -projectPath "<chemin du projet>"
///     -executeMethod RestoreClientBuildSettings.Run -logFile restore_client_build_settings.log
/// </summary>
public static class RestoreClientBuildSettings
{
    public static void Run()
    {
        EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
        Debug.Log($"[RestoreClientBuildSettings] standaloneBuildSubtarget = {EditorUserBuildSettings.standaloneBuildSubtarget}, activeBuildTarget = {EditorUserBuildSettings.activeBuildTarget}");
    }
}
