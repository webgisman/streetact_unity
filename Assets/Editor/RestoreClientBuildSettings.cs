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
    // 2026-09-06 : ajout du [MenuItem] — jusque-là cet utilitaire n'était accessible qu'en ligne de
    // commande Éditeur fermé (voir doc ci-dessus). Or ServerBuildScript laisse standaloneBuildSubtarget
    // sur Server de façon persistante, ce qui définit UNITY_SERVER pour TOUTE session Play Mode
    // suivante — y compris chaque instance Multiplayer Play Mode (Éditeur principal ET Joueurs
    // Virtuels, quel que soit leur "Multiplayer Role" respectif, qui ne contrôle pas cette sous-cible)
    // — et fait tourner GameServerBootstrap (JWT_SECRET manquant en local) partout au lieu du seul
    // vrai serveur sur le VPS. Un menu direct évite d'avoir à fermer l'Éditeur juste pour ce toggle.
    [MenuItem("Novgov/Restaurer le Build Client (désactive UNITY_SERVER)")]
    public static void Run()
    {
        EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
        Debug.Log($"[RestoreClientBuildSettings] standaloneBuildSubtarget = {EditorUserBuildSettings.standaloneBuildSubtarget}, activeBuildTarget = {EditorUserBuildSettings.activeBuildTarget}");
    }
}
