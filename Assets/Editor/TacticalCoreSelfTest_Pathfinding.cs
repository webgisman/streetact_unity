// Tests de non-regression de l'A* (2026-09-04). Meme partial class que TacticalCoreSelfTest.cs,
// lances par RunClientLogicTests() (voir TacticalCoreSelfTest_Client.cs).
//
// Ce qu'ils epinglent : avant le correctif, FindPath n'avait AUCUN test de praticabilite sur la
// cellule d'ARRIVEE. Une cellule impraticable n'etant jamais empilee, la condition d'arret
// "j'ai depile l'arrivee" ne pouvait jamais etre atteinte et A* explorait la carte ENTIERE avant
// de renvoyer une liste vide — 57 600 cellules sur la grille reelle (rayon 120 m, cellules de 1 m),
// chaque iteration balayant en plus toute la frontiere de facon LINEAIRE. Or l'interieur des
// batiments est impraticable par construction (CarveBuildingInteriors) et c'est exactement ce que
// visent l'apercu de trajectoire et l'ordre "ENTRER DANS LE BATIMENT" : chaque tap sur un batiment
// payait ce balayage complet sur le thread principal.
using System.Collections.Generic;
using UnityEngine;
using Novgov.TacticalCore;
// Alias explicite : `using System.Diagnostics;` rendrait `Debug` ambigu avec UnityEngine.Debug.
using Stopwatch = System.Diagnostics.Stopwatch;

public static partial class TacticalCoreSelfTest
{
    private static void RunPathfindingTests(ref int passed, ref int failed)
    {
        Run("A* : une arrivee impraticable renvoie un chemin vide", TestPathfindingRejectsBlockedDestination, ref passed, ref failed);
        Run("A* : une arrivee dans l'empreinte d'un batiment renvoie un chemin vide", TestPathfindingRejectsBuildingInterior, ref passed, ref failed);
        Run("A* : une arrivee impraticable n'explore PAS toute la carte (cout borne)", TestPathfindingBlockedDestinationIsCheap, ref passed, ref failed);
        Run("A* : un chemin normal est toujours trouve et reste optimal", TestPathfindingStillFindsOptimalPath, ref passed, ref failed);
        Run("A* : chemin contigu, sans saut de cellule (integrite du tas)", TestPathfindingPathIsContiguous, ref passed, ref failed);
        Run("A* : arrivee = depart renvoie un chemin d'un seul point", TestPathfindingSameCell, ref passed, ref failed);
        Run("A* : deux appels identiques donnent EXACTEMENT le meme chemin (determinisme)", TestPathfindingIsDeterministic, ref passed, ref failed);
        Run("A* : un couloir en U est bien suivi (pas de traversee de mur)", TestPathfindingFollowsUShapedCorridor, ref passed, ref failed);
    }

    /// <summary>Grille carree praticable partout, centree sur l'origine.</summary>
    private static TacticalGrid MakeOpenGrid(int cells)
    {
        return new TacticalGrid(-cells / 2f, -cells / 2f, cells, cells);
    }

    private static bool TestPathfindingRejectsBlockedDestination()
    {
        var grid = MakeOpenGrid(40);
        grid.TryWorldToCell(new Vector2(8f, 8f), out int bx, out int bz);
        grid.SetWalkable(bx, bz, false);

        var path = Pathfinding.FindPath(grid, new Vector2(-8f, -8f), new Vector2(8f, 8f));
        return path.Count == 0;
    }

    private static bool TestPathfindingRejectsBuildingInterior()
    {
        var grid = MakeOpenGrid(60);
        // Batiment carre de 10x10 m centre sur (0,0) — exactement ce que CarveBuildingInteriors
        // rend impraticable, et exactement ce qu'un tap "ENTRER DANS LE BATIMENT" designe.
        var footprint = new List<Vector2>
        {
            new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f)
        };
        grid.CarveBuildingInteriors(new List<List<Vector2>> { footprint });

        var path = Pathfinding.FindPath(grid, new Vector2(-20f, -20f), new Vector2(0f, 0f));
        return path.Count == 0;
    }

    /// <summary>Garde-fou de COUT, pas seulement de resultat : avec l'ancien code, cet appel
    /// explorait les 57 600 cellules en balayant la frontiere de facon lineaire — de l'ordre de la
    /// seconde. Le seuil est volontairement tres large (il ne s'agit pas de mesurer une machine,
    /// mais d'attraper un retour du balayage complet, qui est deux ordres de grandeur au-dessus).</summary>
    private static bool TestPathfindingBlockedDestinationIsCheap()
    {
        var grid = MakeOpenGrid(240); // la vraie taille : BuildFromScene(rayon 120 m), cellules de 1 m
        var footprint = new List<Vector2>
        {
            new Vector2(-6f, -6f), new Vector2(6f, -6f), new Vector2(6f, 6f), new Vector2(-6f, 6f)
        };
        grid.CarveBuildingInteriors(new List<List<Vector2>> { footprint });

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            var path = Pathfinding.FindPath(grid, new Vector2(-100f, -100f), new Vector2(0f, 0f));
            if (path.Count != 0) return false;
        }
        sw.Stop();

        bool ok = sw.ElapsedMilliseconds < 300; // 20 appels : ~0 ms attendu, ~20 s avec l'ancien code
        if (!ok) Debug.LogError($"[A*] 20 arrivees impraticables ont pris {sw.ElapsedMilliseconds} ms — le balayage complet est de retour.");
        return ok;
    }

    private static bool TestPathfindingStillFindsOptimalPath()
    {
        var grid = MakeOpenGrid(40);
        var path = Pathfinding.FindPath(grid, new Vector2(-10.5f, -10.5f), new Vector2(9.5f, 9.5f));
        if (path.Count < 2) return false;

        // Terrain libre : le trajet doit etre la diagonale, soit 20 pas de cellule (21 points).
        // Une valeur plus grande signifierait un chemin sous-optimal (tas mal ordonne).
        return path.Count == 21;
    }

    private static bool TestPathfindingPathIsContiguous()
    {
        var grid = MakeOpenGrid(60);
        var footprint = new List<Vector2>
        {
            new Vector2(-5f, -5f), new Vector2(5f, -5f), new Vector2(5f, 5f), new Vector2(-5f, 5f)
        };
        grid.CarveBuildingInteriors(new List<List<Vector2>> { footprint });

        var path = Pathfinding.FindPath(grid, new Vector2(-20f, 0f), new Vector2(20f, 0f));
        if (path.Count < 2) return false;

        for (int i = 1; i < path.Count; i++)
        {
            float dx = Mathf.Abs(path[i].x - path[i - 1].x);
            float dz = Mathf.Abs(path[i].y - path[i - 1].y);
            // Un pas = une cellule, en 8 directions. Toute valeur superieure est un saut.
            if (dx > TacticalGrid.CellSize + 0.01f || dz > TacticalGrid.CellSize + 0.01f) return false;
            if (dx < 0.01f && dz < 0.01f) return false; // deux fois la meme cellule
        }
        return true;
    }

    private static bool TestPathfindingSameCell()
    {
        var grid = MakeOpenGrid(20);
        var path = Pathfinding.FindPath(grid, new Vector2(1.2f, 1.4f), new Vector2(1.6f, 1.8f));
        return path.Count == 1;
    }

    private static bool TestPathfindingIsDeterministic()
    {
        var grid = MakeOpenGrid(60);
        var footprint = new List<Vector2>
        {
            new Vector2(-4f, -8f), new Vector2(4f, -8f), new Vector2(4f, 8f), new Vector2(-4f, 8f)
        };
        grid.CarveBuildingInteriors(new List<List<Vector2>> { footprint });

        var a = Pathfinding.FindPath(grid, new Vector2(-20f, 0f), new Vector2(20f, 0f));
        var b = Pathfinding.FindPath(grid, new Vector2(-20f, 0f), new Vector2(20f, 0f));
        if (a.Count == 0 || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!Mathf.Approximately(a[i].x, b[i].x) || !Mathf.Approximately(a[i].y, b[i].y)) return false;
        }
        return true;
    }

    /// <summary>Le chemin doit sortir du U par son ouverture, jamais couper les branches.</summary>
    private static bool TestPathfindingFollowsUShapedCorridor()
    {
        var grid = MakeOpenGrid(60);

        // Trois murs formant un U ouvert vers +Z, autour du depart (0,-10).
        for (int i = -60; i <= 60; i++)
        {
            SetBlockedAtWorld(grid, i * 0.5f, -14f);   // fond du U
        }
        for (int i = -28; i <= 0; i++)
        {
            SetBlockedAtWorld(grid, -14f, i * 0.5f);   // branche gauche
            SetBlockedAtWorld(grid, 14f, i * 0.5f);    // branche droite
        }

        var path = Pathfinding.FindPath(grid, new Vector2(0f, -10f), new Vector2(0f, 10f));
        if (path.Count < 2) return false;

        // Aucun point du chemin ne doit se trouver sur une cellule bloquee.
        foreach (var p in path)
        {
            if (!grid.TryWorldToCell(p, out int cx, out int cz)) return false;
            bool isStart = Mathf.Abs(p.x - path[0].x) < 0.01f && Mathf.Abs(p.y - path[0].y) < 0.01f;
            if (!isStart && !grid.IsWalkable(cx, cz)) return false;
        }
        return true;
    }

    private static void SetBlockedAtWorld(TacticalGrid grid, float x, float z)
    {
        if (grid.TryWorldToCell(new Vector2(x, z), out int cx, out int cz)) grid.SetWalkable(cx, cz, false);
    }
}
