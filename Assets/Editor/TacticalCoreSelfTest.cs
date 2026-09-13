using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Novgov.TacticalCore;

/// <summary>
/// Auto-test du moteur logique déterministe restant (Assets/Scripts/TacticalCore/) — ne touche à
/// AUCUNE scène ni composant Unity vivant, purement des structures de données synthétiques.
/// TacticalResolver.cs/LineOfSight.cs et tout ce qui les testait ont été supprimés le 2026-09-13
/// (zéro appelant en production depuis le passage de tous les modes au vrai moteur Unity — voir
/// Assets/_ServerDocs/multiplayer/00-current-architecture-2026-09-13.md) : seul ce qui teste du
/// code encore vivant (A*/TacticalGrid, logique client pure) survit ici.
/// </summary>
public static partial class TacticalCoreSelfTest
{
    [MenuItem("Novgov/Tests/TacticalCore Self-Test")]
    public static void RunAll()
    {
        int passed = 0, failed = 0;

        Run("Pathfinding contourne un bâtiment", TestPathfindingAvoidsBuilding, ref passed, ref failed);

        // Logique CLIENT pure (sentinelles, A* non-régression, hash déterministe) — voir
        // TacticalCoreSelfTest_Client.cs.
        RunClientLogicTests(ref passed, ref failed);

        Debug.Log(passed == 0 && failed == 0
            ? "[TacticalCoreSelfTest] Aucun test exécuté."
            : $"<color={(failed == 0 ? "green" : "red")}>[TacticalCoreSelfTest] {passed} réussi(s), {failed} échoué(s).</color>");
    }

    private static void Run(string name, System.Func<bool> test, ref int passed, ref int failed)
    {
        bool ok;
        try { ok = test(); }
        catch (System.Exception e)
        {
            Debug.LogError($"[TacticalCoreSelfTest] '{name}' a levé une exception : {e}");
            failed++;
            return;
        }

        if (ok) { Debug.Log($"<color=green>[TacticalCoreSelfTest] OK — {name}</color>"); passed++; }
        else { Debug.LogError($"[TacticalCoreSelfTest] ÉCHEC — {name}"); failed++; }
    }

    private static bool TestPathfindingAvoidsBuilding()
    {
        var footprint = new List<Vector2> {
            new Vector2(-5, -5), new Vector2(5, -5), new Vector2(5, 5), new Vector2(-5, 5)
        };
        var grid = new TacticalGrid(-30f, -30f, 60, 60);
        grid.CarveBuildingInteriors(new List<List<Vector2>> { footprint });

        var path = Pathfinding.FindPath(grid, new Vector2(-10, 0), new Vector2(10, 0));
        if (path.Count == 0) return false;

        foreach (var point in path)
        {
            if (GeometryMath.PointInPolygon(footprint, point)) return false; // le chemin ne doit jamais traverser le bâtiment
        }
        return true;
    }
}
