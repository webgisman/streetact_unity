using System;
using System.Reflection;
using Novgov.Server;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Test Éditeur AUTOMATISÉ (2026-09-19, soir) — garde-fou de non-régression pour le bug "je pose un
/// canon, il se retransforme en char après le déploiement" (retour joueur en pleine partie).
///
/// Root cause : UnitSpawnerUI.SpawnUnitAt réglait `isTank = true` SYNCHRONE pour CharLeopard,
/// VehiculeCanon ET Mortier, et `isMortar = true` SYNCHRONE pour Mortier — mais PAS
/// `isCanonVehicle = true` SYNCHRONE pour VehiculeCanon (réglé seulement plus tard, par la détection
/// sur le nom dans UnitAI.Start(), qu'Unity ne déclenche qu'à la frame suivante). Tout code lisant
/// UnitTypeStats.InferType() dans la MÊME frame que le spawn (ex: MatchSessionManager_Deployment.
/// ResolveDeployment, qui construit "deployment_result" juste après avoir spawné les unités) voyait
/// donc isTank=true / isCanonVehicle=false et retombait à tort sur CharLeopard — le client
/// respawnait alors le mauvais modèle 3D à la réception de deployment_result, alors même que le NOM
/// de l'unité (unit_id, construit séparément) restait correct, ce qui masquait le bug dans les logs.
///
/// Ce test reproduit EXACTEMENT ce timing : spawn via SpawnUnitAt, lecture IMMÉDIATE de InferType,
/// JAMAIS d'invocation de UnitAI.Start() (contrairement à d'autres tests de ce projet qui l'invoquent
/// exprès par réflexion) — c'est précisément l'absence de Start() qui reproduit la course d'origine.
///
/// Usage : "Unity.exe -batchmode -nographics -quit -buildTarget StandaloneWindows64
/// -standaloneBuildSubtarget Player -projectPath ... -executeMethod UnitTypeSyncAutoTest.RunAll
/// -logFile chemin.log" — cherche "[UnitTypeSyncAutoTest]" dans le log.
/// </summary>
public static class UnitTypeSyncAutoTest
{
    public static void RunAll()
    {
        int passed = 0, failed = 0;
        Debug.Log("=== UnitTypeSyncAutoTest : le type d'une unité doit être correct DÈS le spawn, avant même Start() ===");

        EditorAutoTestHarness.RunIsolated("UnitTypeSyncAutoTest", "Un Véhicule Canon fraîchement posé est identifié comme VehiculeCanon avant que Start() n'ait tourné (régression 2026-09-19)",
            () => TestImmediateTypeAfterSpawn(UnitSpawnerUI.UnitType.VehiculeCanon, UnitSpawnerUI.UnitType.VehiculeCanon), ref passed, ref failed);
        EditorAutoTestHarness.RunIsolated("UnitTypeSyncAutoTest", "Un Char Leopard fraîchement posé est identifié comme CharLeopard avant que Start() n'ait tourné (non-régression)",
            () => TestImmediateTypeAfterSpawn(UnitSpawnerUI.UnitType.CharLeopard, UnitSpawnerUI.UnitType.CharLeopard), ref passed, ref failed);
        EditorAutoTestHarness.RunIsolated("UnitTypeSyncAutoTest", "Un Mortier fraîchement posé est identifié comme Mortier avant que Start() n'ait tourné (non-régression)",
            () => TestImmediateTypeAfterSpawn(UnitSpawnerUI.UnitType.Mortier, UnitSpawnerUI.UnitType.Mortier), ref passed, ref failed);
        EditorAutoTestHarness.RunIsolated("UnitTypeSyncAutoTest", "Un Fantassin fraîchement posé est identifié comme Fantassin avant que Start() n'ait tourné (non-régression)",
            () => TestImmediateTypeAfterSpawn(UnitSpawnerUI.UnitType.Fantassin, UnitSpawnerUI.UnitType.Fantassin), ref passed, ref failed);

        Debug.Log($"[UnitTypeSyncAutoTest] {passed} réussi(s), {failed} échoué(s).");
        if (failed > 0)
        {
            throw new Exception($"[UnitTypeSyncAutoTest] {failed} test(s) échoué(s) — voir le log ci-dessus pour le détail.");
        }
    }

    private static UnitSpawnerUI MakeSpawnerWithoutStart()
    {
        // SpawnUnitAt joue un son via Camera.main.transform.position (confirmation de déploiement)
        // — sans caméra taguée "MainCamera" dans cette scène de test minimale, Camera.main vaut
        // null et cette ligne lève une NullReferenceException (même piège déjà documenté dans
        // BuildingNavMeshAutoTest.MakeManagerWithSelectedUnit).
        GameObject camGo = new GameObject("TestCamera");
        camGo.AddComponent<Camera>().tag = "MainCamera";

        GameObject spawnerGo = new GameObject("TestUnitSpawnerUI");
        UnitSpawnerUI spawnerUI = spawnerGo.AddComponent<UnitSpawnerUI>();
        // Awake() n'est pas exécuté immédiatement par AddComponent hors Play Mode (même piège que
        // DeploymentHandshakeAutoTest) — UnitSpawnerUI.Instance en a besoin (SpawnUnitAt le référence).
        MethodInfo awake = typeof(UnitSpawnerUI).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance);
        if (awake == null) throw new Exception("UnitSpawnerUI.Awake() introuvable par réflexion — a-t-elle été renommée ?");
        awake.Invoke(spawnerUI, null);
        if (UnitSpawnerUI.Instance == null)
            throw new Exception("UnitSpawnerUI.Instance toujours NULL après invocation manuelle de Awake().");
        return spawnerUI;
    }

    private static bool TestImmediateTypeAfterSpawn(UnitSpawnerUI.UnitType spawnedAs, UnitSpawnerUI.UnitType expectedInferredType)
    {
        MakeSpawnerWithoutStart();

        // skipSafeSpawnAdjustment: true — on ne teste ici QUE la synchronisation des drapeaux de
        // type, pas le repositionnement NavMesh (déjà couvert par d'autres tests).
        UnitAI unit = UnitSpawnerUI.Instance.SpawnUnitAt(spawnedAs, Vector3.zero, team: 1, skipSafeSpawnAdjustment: true);
        if (unit == null)
        {
            Debug.LogError($"[UnitTypeSyncAutoTest] SpawnUnitAt({spawnedAs}) a renvoyé null.");
            return false;
        }

        // AUCUN appel à UnitAI.Start() ici, exprès : c'est précisément l'écart entre SpawnUnitAt
        // (synchrone) et Start() (une frame plus tard) qui reproduit le bug d'origine — voir l'en-
        // tête de ce fichier.
        UnitSpawnerUI.UnitType inferred = UnitTypeStats.InferType(unit);
        if (inferred != expectedInferredType)
        {
            Debug.LogError($"[UnitTypeSyncAutoTest] Posé comme {spawnedAs}, mais UnitTypeStats.InferType() (lu IMMÉDIATEMENT après SpawnUnitAt, avant Start()) rapporte {inferred} au lieu de {expectedInferredType} — isTank={unit.isTank}, isCanonVehicle={unit.isCanonVehicle}, isMortar={unit.isMortar}.");
            return false;
        }
        return true;
    }
}
