using System;
using System.Reflection;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Test Éditeur AUTOMATISÉ (2026-09-19) — répond au rapport "je tape dans un bâtiment et ça marche
/// pour une unité lourde, alors que dans le vide parfois rien ne se passe". Root cause confirmée en
/// lisant CityGenerator.cs : le sol 2D de CHAQUE bâtiment ("Footprint_2D") porte un vrai MeshCollider
/// plein, jamais isTrigger, et NavMeshSurface.useGeometry = PhysicsColliders (même fichier) bake TOUS
/// les colliders de la scène — le NavMesh marche donc, à tort, sur l'intérieur de n'importe quel
/// bâtiment exactement comme sur une vraie rue. TryFallbackAuSolPourBlinde (TacticalPathManager_
/// ContextMenu.cs), pensé pour rattraper un tap qui rase juste la FAÇADE d'un bâtiment PROCHE en vue
/// oblique, acceptait donc aussi n'importe quel tap tombant en PLEIN MILIEU d'un bâtiment — laissant
/// un blindé y recevoir un ordre de déplacement au sol comme si de rien n'était.
///
/// Ce test reproduit la situation avec un vrai NavMesh bâti EXACTEMENT comme CityGenerator (même
/// NavMeshSurface.useGeometry = PhysicsColliders) et un vrai BuildingStructure — sans avoir besoin de
/// reproduire tout CityGenerator, puisque BuildingStructure.ContainsPoint2D est une donnée pure
/// (empreinte 2D), indépendante de tout collider de mesh réel.
///
/// Usage : "Unity.exe -batchmode -nographics -quit -buildTarget StandaloneWindows64
/// -standaloneBuildSubtarget Player -projectPath ... -executeMethod BuildingNavMeshAutoTest.RunAll
/// -logFile chemin.log" — cherche "[BuildingNavMeshAutoTest]" dans le log.
/// </summary>
public static class BuildingNavMeshAutoTest
{
    public static void RunAll()
    {
        int passed = 0, failed = 0;
        Debug.Log("=== BuildingNavMeshAutoTest : repli sol pour blindé vs empreinte de bâtiment ===");

        EditorAutoTestHarness.RunIsolated("BuildingNavMeshAutoTest", "Un blindé ne peut PAS se replier au sol EN PLEIN MILIEU d'un bâtiment (root cause du bug rapporté)",
            TestTankCannotFallbackInsideBuilding, ref passed, ref failed);
        EditorAutoTestHarness.RunIsolated("BuildingNavMeshAutoTest", "Un blindé PEUT toujours se replier au sol juste À CÔTÉ d'un bâtiment (non-régression du cas d'origine : façade rasée par la vue oblique)",
            TestTankCanFallbackNearBuildingButOutside, ref passed, ref failed);
        EditorAutoTestHarness.RunIsolated("BuildingNavMeshAutoTest", "Un FANTASSIN peut toujours se replier au sol MÊME SI ce point tombe dans un bâtiment (régression 2026-09-19 : le garde ci-dessus ne doit viser QUE les unités lourdes)",
            TestInfantryCanFallbackEvenInsideBuilding, ref passed, ref failed);

        Debug.Log($"[BuildingNavMeshAutoTest] {passed} réussi(s), {failed} échoué(s).");
        if (failed > 0)
        {
            throw new Exception($"[BuildingNavMeshAutoTest] {failed} test(s) échoué(s) — voir le log ci-dessus pour le détail.");
        }
    }

    /// <summary>Sol RÉELLEMENT walkable partout (grand plan, comme MapTileLoader.GenerateQuadMesh) —
    /// le bâtiment lui-même n'a besoin d'AUCUN collider séparé : BuildingStructure.ContainsPoint2D
    /// est une donnée pure (empreinte 2D), indépendante de tout mesh réel. Ce sol walkable partout
    /// EST déjà, à lui seul, la reproduction fidèle du bug (NavMesh ne distingue pas "sous un
    /// bâtiment" de "dans la rue") — pas besoin de recréer le collider Footprint_2D lui-même.</summary>
    private static void BakeWalkableGroundEverywhere()
    {
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "TestGround";
        ground.transform.localScale = new Vector3(20f, 1f, 20f); // Plane par défaut 10x10 -> 200x200.
        GameObjectUtility.SetStaticEditorFlags(ground, StaticEditorFlags.NavigationStatic);

        NavMeshSurface surface = ground.AddComponent<NavMeshSurface>();
        surface.collectObjects = CollectObjects.All;
        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders; // EXACT même réglage que CityGenerator.cs
        surface.defaultArea = 0;
        surface.BuildNavMesh();
    }

    private static TacticalPathManager MakeManagerWithSelectedUnit(out UnitAI unit, bool isTank = true)
    {
        // TryFallbackAuSolPourBlinde joue un son via Camera.main.transform.position (voir
        // TacticalPathManager_ContextMenu.cs) — sans caméra taguée "MainCamera" dans cette scène de
        // test minimale, Camera.main vaut null et cette ligne lève une NullReferenceException avant
        // même d'atteindre le AudioSource (trouvé en conditions réelles en écrivant ce test, même
        // piège que TacticalSelectionAutoTest.TestSquadBarCycleGoesThroughAllUnits).
        GameObject camGo = new GameObject("TestCamera");
        camGo.AddComponent<Camera>().tag = "MainCamera";

        GameObject unitGo = GameObject.CreatePrimitive(isTank ? PrimitiveType.Cube : PrimitiveType.Capsule);
        unitGo.name = "TestSelectedUnit";
        unit = unitGo.AddComponent<UnitAI>();
        unit.teamID = 1;
        unit.isPlayerControlled = true;
        unit.teamAssignedBySpawner = true;
        unit.isTank = isTank;

        GameObject mgrGo = new GameObject("TestTacticalPathManager");
        TacticalPathManager mgr = mgrGo.AddComponent<TacticalPathManager>();
        // uniteSelectionnee est un champ public (voir TacticalPathManager.cs) — assignable
        // directement, pas besoin de passer par SelectionnerUnite pour ce test.
        typeof(TacticalPathManager).GetField("uniteSelectionnee").SetValue(mgr, unitGo);
        return mgr;
    }

    private static bool InvokeTryFallback(TacticalPathManager mgr, Vector3 tapPoint)
    {
        MethodInfo method = typeof(TacticalPathManager).GetMethod("TryFallbackAuSolPourBlinde", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) throw new Exception("TacticalPathManager.TryFallbackAuSolPourBlinde introuvable par réflexion — a-t-elle été renommée ?");
        return (bool)method.Invoke(mgr, new object[] { tapPoint });
    }

    private static bool TestTankCannotFallbackInsideBuilding()
    {
        BakeWalkableGroundEverywhere();
        EditorAutoTestHarness.MakeSquareBuilding(Vector2.zero, 5f);
        TacticalPathManager mgr = MakeManagerWithSelectedUnit(out UnitAI unit);

        // En plein milieu du bâtiment (empreinte [-5,5]x[-5,5]) — PAS près d'une arête.
        Vector3 tapInsideBuilding = new Vector3(1f, 0f, 1f);
        bool accepted = InvokeTryFallback(mgr, tapInsideBuilding);

        if (accepted)
        {
            Debug.LogError($"[BuildingNavMeshAutoTest] TryFallbackAuSolPourBlinde a accepté un tap EN PLEIN MILIEU du bâtiment ({tapInsideBuilding:F1}) — le blindé aurait reçu un ordre de déplacement à l'intérieur.");
            return false;
        }
        return true;
    }

    private static bool TestTankCanFallbackNearBuildingButOutside()
    {
        BakeWalkableGroundEverywhere();
        EditorAutoTestHarness.MakeSquareBuilding(Vector2.zero, 5f);
        TacticalPathManager mgr = MakeManagerWithSelectedUnit(out UnitAI unit);

        // Juste à côté de l'arête à x=5 (empreinte [-5,5]x[-5,5]) mais RÉELLEMENT hors du bâtiment.
        Vector3 tapNextToBuilding = new Vector3(6f, 0f, 0f);
        bool accepted = InvokeTryFallback(mgr, tapNextToBuilding);

        if (!accepted)
        {
            Debug.LogError($"[BuildingNavMeshAutoTest] TryFallbackAuSolPourBlinde a refusé un tap pourtant hors du bâtiment ({tapNextToBuilding:F1}, à 1m de l'arête) — régression du cas d'origine (façade rasée par la vue oblique).");
            return false;
        }
        return true;
    }

    private static bool TestInfantryCanFallbackEvenInsideBuilding()
    {
        BakeWalkableGroundEverywhere();
        EditorAutoTestHarness.MakeSquareBuilding(Vector2.zero, 5f);
        TacticalPathManager mgr = MakeManagerWithSelectedUnit(out UnitAI unit, isTank: false);

        // En plein milieu du bâtiment — refusé pour un blindé (test ci-dessus), mais un fantassin
        // PEUT légitimement s'y trouver : le garde ajouté pour les blindés ne doit pas s'appliquer.
        Vector3 tapInsideBuilding = new Vector3(1f, 0f, 1f);
        bool accepted = InvokeTryFallback(mgr, tapInsideBuilding);

        if (!accepted)
        {
            Debug.LogError($"[BuildingNavMeshAutoTest] TryFallbackAuSolPourBlinde a refusé À TORT un repli pour un FANTASSIN ({tapInsideBuilding:F1}) — régression 2026-09-19, le garde anti-blindé bloque aussi l'infanterie.");
            return false;
        }
        return true;
    }
}
