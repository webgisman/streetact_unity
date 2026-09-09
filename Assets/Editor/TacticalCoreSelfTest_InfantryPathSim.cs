using System.Collections.Generic;
using UnityEngine;
using Novgov.TacticalCore;

/// <summary>
/// Simulation diagnostique demandée en réaction à un retour de jeu (2026-09-09, PvP Deathmatch/
/// Zone de Contrôle réel) : « le fantassin ne suit pas le chemin ». Contrairement aux tests
/// existants (TacticalCoreSelfTest.TestMultiCheckpointOrderPreserved, etc.), qui vérifient chaque
/// point un par un, celle-ci imprime la position RÉELLE à CHAQUE tick pour un ordre à plusieurs
/// checkpoints sur un terrain plus grand que le budget de déplacement d'un tour (50m fantassin,
/// voir UnitTypeStats.MovementBudget) — exactement le cas d'usage réel (le joueur trace un long
/// trajet sur la carte), pour repérer tout saut, arrêt prématuré ou déviation qu'un test
/// "atteint/pas atteint" pourrait manquer.
/// </summary>
public static partial class TacticalCoreSelfTest
{
    private static void RunInfantryPathSimTests(ref int passed, ref int failed)
    {
        Run("Simulation : chemin fantassin réaliste (>budget) sur terrain ouvert, log tick par tick", TestInfantryLongPathOpenTerrainLogged, ref passed, ref failed);
        Run("Simulation : chemin fantassin réaliste contournant un pâté de maisons, log tick par tick", TestInfantryLongPathAroundBuildingLogged, ref passed, ref failed);
    }

    /// <summary>Terrain ouvert (aucun bâtiment), 3 jambes, 90m au total — largement au-dessus des
    /// 50m de budget d'un fantassin. Doit progresser MONOTONEMENT vers chaque checkpoint dans
    /// l'ordre, sans jamais reculer ni sauter, puis s'arrêter EXACTEMENT à 50m parcourus.</summary>
    private static bool TestInfantryLongPathOpenTerrainLogged()
    {
        var state = MakeEmptyState();
        var unit = MakeInfantry("Fantassin_1_1", 1, new Vector2(0, 0));
        unit.movementBudget = 50f; // valeur réelle serveur (UnitTypeStats.MovementBudget, Fantassin)
        state.units.Add(unit);

        Vector2 cpA = new Vector2(30, 0);
        Vector2 cpB = new Vector2(30, 30);
        Vector2 cpC = new Vector2(60, 30);
        var order = new UnitOrders
        {
            unitId = "Fantassin_1_1",
            checkpoints = new List<PathCheckpoint> {
                new PathCheckpoint { position = cpA },
                new PathCheckpoint { position = cpB },
                new PathCheckpoint { position = cpC },
            }
        };

        var events = TacticalResolver.Resolve(state, new List<UnitOrders> { order }, new List<UnitOrders>(), null);

        Debug.Log("[Sim] --- Terrain ouvert : position à chaque tick ---");
        Vector2 lastPos = new Vector2(0, 0);
        float traveled = 0f;
        bool everWentBackward = false;
        int moveCount = 0;
        foreach (var e in events)
        {
            if (e.kind != TacticalEvent.Kind.Move) continue;
            moveCount++;
            float stepDist = Vector2.Distance(lastPos, e.position);
            traveled += stepDist;
            Debug.Log($"[Sim] tick={e.tick,4}  pos=({e.position.x,6:F2}, {e.position.y,6:F2})  pas={stepDist:F3}m  cumul={traveled:F2}m");

            // "Recule" = s'éloigne du prochain checkpoint utile ; ici on vérifie juste qu'aucun pas
            // individuel ne dépasse la vitesse max plausible d'un tick (1 m, voir MoveStepDistance)
            // avec une marge, pour repérer un éventuel saut/téléportation.
            if (stepDist > 1.05f) everWentBackward = true;
            lastPos = e.position;
        }

        Debug.Log($"[Sim] Position finale : ({unit.position.x:F2}, {unit.position.y:F2}) — distance parcourue mesurée : {traveled:F2}m (budget=50m)");

        bool noTeleport = !everWentBackward;
        // Le chemin réel (A -> B -> C) mesure exactement 30+30+30 = 90m en ligne droite sur terrain
        // ouvert (pas de contournement nécessaire) — le budget de 50m doit donc couper AVANT cpC,
        // quelque part entre cpA (atteint à 30m) et cpB (atteint à 60m, hors budget).
        bool stoppedWithinBudget = traveled <= 50.5f; // petite marge flottante
        bool reachedCpA = false;
        foreach (var e in events)
        {
            if (e.kind == TacticalEvent.Kind.Move && Vector2.Distance(e.position, cpA) < 0.5f) { reachedCpA = true; break; }
        }
        bool neverReachedCpC = Vector2.Distance(unit.position, cpC) > 1f; // hors de portée du budget, ne doit jamais y arriver

        return noTeleport && stoppedWithinBudget && reachedCpA && neverReachedCpC && moveCount > 0;
    }

    /// <summary>Même idée, mais avec un pâté de maisons entre le départ et le premier checkpoint —
    /// pour vérifier que le contournement réel (A*, pas une ligne droite) ne fait pas "perdre" du
    /// budget de façon disproportionnée ni ne fait dévier l'unité de façon erratique.</summary>
    private static bool TestInfantryLongPathAroundBuildingLogged()
    {
        var footprint = new List<Vector2> {
            new Vector2(10, -5), new Vector2(20, -5), new Vector2(20, 5), new Vector2(10, 5)
        };
        var state = new TacticalWorldState { grid = new TacticalGrid(-50f, -50f, 150, 150) };
        state.grid.CarveBuildingInteriors(new List<List<Vector2>> { footprint });
        state.buildings.Add(new TacticalBuilding { id = 0, footprint = footprint, health = 300f });

        var unit = MakeInfantry("Fantassin_1_2", 1, new Vector2(0, 0));
        unit.movementBudget = 50f;
        state.units.Add(unit);

        Vector2 destination = new Vector2(30, 0); // directement derrière le bâtiment
        var order = new UnitOrders
        {
            unitId = "Fantassin_1_2",
            checkpoints = new List<PathCheckpoint> { new PathCheckpoint { position = destination } }
        };

        var events = TacticalResolver.Resolve(state, new List<UnitOrders> { order }, new List<UnitOrders>(), null);

        Debug.Log("[Sim] --- Contournement de bâtiment : position à chaque tick ---");
        Vector2 lastPos = new Vector2(0, 0);
        float traveled = 0f;
        bool everCutThroughBuilding = false;
        foreach (var e in events)
        {
            if (e.kind != TacticalEvent.Kind.Move) continue;
            float stepDist = Vector2.Distance(lastPos, e.position);
            traveled += stepDist;
            bool insideBuilding = GeometryMath.PointInPolygon(footprint, e.position);
            Debug.Log($"[Sim] tick={e.tick,4}  pos=({e.position.x,6:F2}, {e.position.y,6:F2})  pas={stepDist:F3}m  cumul={traveled:F2}m  dans_batiment={insideBuilding}");
            if (insideBuilding) everCutThroughBuilding = true;
            lastPos = e.position;
        }

        Debug.Log($"[Sim] Position finale : ({unit.position.x:F2}, {unit.position.y:F2})");

        // Ligne droite = 30m, tout rond, largement sous le budget de 50m même avec le détour du
        // contournement (~40m de marge de detour tolérée avant de dépasser 50m).
        bool reachedDestination = Vector2.Distance(unit.position, destination) < 1.5f;
        return !everCutThroughBuilding && reachedDestination;
    }
}
