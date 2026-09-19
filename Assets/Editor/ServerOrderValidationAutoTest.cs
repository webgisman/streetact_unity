using System;
using System.Collections.Generic;
using System.Reflection;
using Novgov.Network;
using Novgov.Server;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Test Éditeur AUTOMATISÉ (2026-09-19) du DURCISSEMENT SERVEUR ajouté à
/// MatchSessionManager.ApplyOrdersToUnits (MatchSessionManager_CombatLive.cs) — miroir, côté
/// autorité réseau, du correctif client TacticalPathManager_ContextMenu.TryFallbackAuSolPourBlinde
/// (voir BuildingNavMeshAutoTest pour la root cause complète : le NavMesh marche à tort sur
/// l'intérieur de chaque bâtiment). Le client corrigé ne soumettra plus un tel ordre, mais un client
/// MODIFIÉ qui parle directement le protocole réseau (voir 03-network-protocol.md) le pourrait encore
/// — le serveur, seule autorité réelle du mouvement (NavMeshAgent) en multijoueur "vivant", ne doit
/// jamais faire confiance au client sur ce point non plus.
///
/// Usage : "Unity.exe -batchmode -nographics -quit -buildTarget StandaloneWindows64
/// -standaloneBuildSubtarget Player -projectPath ... -executeMethod ServerOrderValidationAutoTest.RunAll
/// -logFile chemin.log" — cherche "[ServerOrderValidationAutoTest]" dans le log.
/// </summary>
public static class ServerOrderValidationAutoTest
{
    public static void RunAll()
    {
        int passed = 0, failed = 0;
        Debug.Log("=== ServerOrderValidationAutoTest : le serveur rejette un ordre de blindé DANS un bâtiment ===");

        EditorAutoTestHarness.RunIsolated("ServerOrderValidationAutoTest", "Un char ne peut PAS recevoir un nœud de chemin dont les coordonnées tombent dans un bâtiment",
            TestTankOrderIntoBuildingIsDropped, ref passed, ref failed);
        EditorAutoTestHarness.RunIsolated("ServerOrderValidationAutoTest", "Un char PEUT toujours recevoir un nœud de chemin normal, hors de tout bâtiment (non-régression)",
            TestTankOrderOutsideBuildingIsAccepted, ref passed, ref failed);
        EditorAutoTestHarness.RunIsolated("ServerOrderValidationAutoTest", "Un fantassin PEUT toujours recevoir un nœud DANS un bâtiment (c'est ainsi qu'il y entre — le durcissement ne vise QUE les unités isTank)",
            TestInfantryOrderIntoBuildingStillAccepted, ref passed, ref failed);

        Debug.Log($"[ServerOrderValidationAutoTest] {passed} réussi(s), {failed} échoué(s).");
        if (failed > 0)
        {
            throw new Exception($"[ServerOrderValidationAutoTest] {failed} test(s) échoué(s) — voir le log ci-dessus pour le détail.");
        }
    }

    private static UnitAI MakeUnit(string name, bool isTank)
    {
        GameObject go = GameObject.CreatePrimitive(isTank ? PrimitiveType.Cube : PrimitiveType.Capsule);
        go.name = name;
        UnitAI ai = go.AddComponent<UnitAI>();
        ai.teamID = 1;
        ai.isPlayerControlled = true;
        ai.teamAssignedBySpawner = true;
        ai.isTank = isTank;
        return ai;
    }

    private static void InvokeApplyOrdersToUnits(MatchSessionManager mgr, List<UnitAI> units, UnitOrder[] orders)
    {
        MethodInfo method = typeof(MatchSessionManager).GetMethod("ApplyOrdersToUnits", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null) throw new Exception("MatchSessionManager.ApplyOrdersToUnits introuvable par réflexion — a-t-elle été renommée ?");
        method.Invoke(mgr, new object[] { units, orders });
    }

    private static bool TestTankOrderIntoBuildingIsDropped()
    {
        EditorAutoTestHarness.MakeSquareBuilding(Vector2.zero, 5f);
        UnitAI tank = MakeUnit("TestTank", isTank: true);
        MatchSessionManager mgr = EditorAutoTestHarness.MakeRealManagerWithoutStart();

        var orders = new[]
        {
            new UnitOrder
            {
                unit_id = tank.gameObject.name,
                path = new[] { new PathNode { x = 1f, y = 0f, z = 1f, action = (int)TacticalPathManager.NodeAction.Continuer } } // en plein milieu du bâtiment
            }
        };

        InvokeApplyOrdersToUnits(mgr, new List<UnitAI> { tank }, orders);

        if (tank.tacticalPath.Count != 0)
        {
            Debug.LogError($"[ServerOrderValidationAutoTest] Le serveur a accepté un nœud DANS le bâtiment pour un char (tacticalPath.Count={tank.tacticalPath.Count}) — un client modifié pourrait faire traverser un bâtiment à un blindé.");
            return false;
        }
        return true;
    }

    private static bool TestTankOrderOutsideBuildingIsAccepted()
    {
        EditorAutoTestHarness.MakeSquareBuilding(Vector2.zero, 5f);
        UnitAI tank = MakeUnit("TestTank2", isTank: true);
        MatchSessionManager mgr = EditorAutoTestHarness.MakeRealManagerWithoutStart();

        var orders = new[]
        {
            new UnitOrder
            {
                unit_id = tank.gameObject.name,
                path = new[] { new PathNode { x = 20f, y = 0f, z = 20f, action = (int)TacticalPathManager.NodeAction.Continuer } } // largement hors du bâtiment
            }
        };

        InvokeApplyOrdersToUnits(mgr, new List<UnitAI> { tank }, orders);

        if (tank.tacticalPath.Count != 1)
        {
            Debug.LogError($"[ServerOrderValidationAutoTest] Le serveur a rejeté À TORT un nœud pourtant hors de tout bâtiment pour un char (tacticalPath.Count={tank.tacticalPath.Count}) — régression du déplacement normal.");
            return false;
        }
        return true;
    }

    private static bool TestInfantryOrderIntoBuildingStillAccepted()
    {
        EditorAutoTestHarness.MakeSquareBuilding(Vector2.zero, 5f);
        UnitAI infantry = MakeUnit("TestInfantry", isTank: false);
        MatchSessionManager mgr = EditorAutoTestHarness.MakeRealManagerWithoutStart();

        var orders = new[]
        {
            new UnitOrder
            {
                unit_id = infantry.gameObject.name,
                path = new[] { new PathNode { x = 1f, y = 0f, z = 1f, action = (int)TacticalPathManager.NodeAction.Continuer } }
            }
        };

        InvokeApplyOrdersToUnits(mgr, new List<UnitAI> { infantry }, orders);

        if (infantry.tacticalPath.Count != 1)
        {
            Debug.LogError($"[ServerOrderValidationAutoTest] Le durcissement a bloqué À TORT un nœud d'INFANTERIE dans un bâtiment (tacticalPath.Count={infantry.tacticalPath.Count}) — c'est pourtant ainsi qu'elle y entre, seules les unités isTank doivent être visées.");
            return false;
        }
        return true;
    }
}
