using System;
using System.Collections.Generic;
using System.Reflection;
using Novgov.Server;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Utilitaires communs aux tests Éditeur AUTOMATISÉS (BuildingNavMeshAutoTest,
/// ServerOrderValidationAutoTest, DeploymentHandshakeAutoTest, TacticalSelectionAutoTest,
/// TacticalNetworkReplayAutoTest) — factorisé le 2026-09-19 après qu'un review de code a trouvé
/// les mêmes helpers (RunIsolated, MakeSquareBuilding, MakeRealManagerWithoutStart) copiés-collés
/// à l'identique dans chacun de ces fichiers.
/// </summary>
public static class EditorAutoTestHarness
{
    /// <summary>Nouvelle scène vide, puis exécute test() en capturant toute exception comme un
    /// échec. <paramref name="logPrefix"/> est le nom du fichier appelant (ex.
    /// "BuildingNavMeshAutoTest"), utilisé tel quel dans les logs pour rester filtrable comme
    /// avant la factorisation.</summary>
    public static void RunIsolated(string logPrefix, string label, Func<bool> test, ref int passed, ref int failed)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        bool ok;
        try { ok = test(); }
        catch (Exception e)
        {
            Debug.LogError($"[{logPrefix}] EXCEPTION pendant '{label}' : {e}");
            ok = false;
        }
        if (ok) { passed++; Debug.Log($"[{logPrefix}] OK — {label}"); }
        else { failed++; Debug.LogError($"[{logPrefix}] ECHEC — {label}"); }
    }

    /// <summary>Bâtiment carré minimal enregistré dans BuildingStructure.AllBuildings —
    /// BuildingStructure.ContainsPoint2D est une donnée pure (empreinte 2D), pas besoin d'un vrai
    /// mesh/collider pour les tests. OnEnable() est invoqué par réflexion car non fiable via
    /// AddComponent seul en mode batch (constaté empiriquement : AllBuildings.Count restait à 0
    /// malgré AddComponent).</summary>
    public static BuildingStructure MakeSquareBuilding(Vector2 center, float halfSize)
    {
        GameObject buildingGo = new GameObject("TestBuilding");
        BuildingStructure structure = buildingGo.AddComponent<BuildingStructure>();
        var footprint = new List<Vector2>
        {
            new Vector2(center.x - halfSize, center.y - halfSize),
            new Vector2(center.x + halfSize, center.y - halfSize),
            new Vector2(center.x + halfSize, center.y + halfSize),
            new Vector2(center.x - halfSize, center.y + halfSize),
        };
        structure.InitPolygon(footprint, 6f);

        if (!BuildingStructure.AllBuildings.Contains(structure))
        {
            MethodInfo onEnable = typeof(BuildingStructure).GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance);
            onEnable?.Invoke(structure, null);
        }
        return structure;
    }

    /// <summary>MatchSessionManager réel dont Start() (4 coroutines de fond : SetupWorldOnce,
    /// heartbeat, revenu de zone, résolution de siège) n'a jamais tourné — ne JAMAIS l'invoquer par
    /// réflexion ici : ces coroutines ne seraient jamais pompées et ajouteraient du bruit sans
    /// rapport avec le test appelant. Awake() suffit et s'exécute déjà à l'AddComponent.</summary>
    public static MatchSessionManager MakeRealManagerWithoutStart()
    {
        GameObject go = new GameObject("TestMatchSessionManager");
        return go.AddComponent<MatchSessionManager>();
    }
}
