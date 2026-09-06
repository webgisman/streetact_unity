// Tests de la halte tactique "ATTENDRE 30 SECONDES" cote moteur pur (2026-09-05).
//
// Avant ce correctif, PathCheckpoint n'avait AUCUN champ pour representer une duree d'attente : le
// switch qui construit les checkpoints (MatchSessionManager.BuildUnitOrders/BuildUnitOrdersPure)
// tombait dans son cas par defaut pour NodeAction.Attendre30s, produisant un PathCheckpoint
// strictement identique a un simple deplacement — l'action n'avait donc RIEN d'autre effet qu'un
// mouvement en multijoueur, alors que le menu promet une pause de 30 secondes.
//
// Meme partial class que TacticalCoreSelfTest.cs, lances par RunClientLogicTests().
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Novgov.TacticalCore;

public static partial class TacticalCoreSelfTest
{
    private static void RunWaitTests(ref int passed, ref int failed)
    {
        Run("Attente : une halte de 30s retarde bien l'arrivee au point suivant", TestWaitDelaysArrivalAtNextCheckpoint, ref passed, ref failed);
        Run("Attente : aucune halte -> le chemin est parcouru sans retard artificiel", TestNoWaitMeansNoDelay, ref passed, ref failed);
        Run("Attente : l'unite en halte reste IMMOBILE (aucun evenement Move pendant l'attente)", TestUnitDoesNotMoveWhileWaiting, ref passed, ref failed);
        Run("Attente : le combat continue de tourner pendant la halte (l'unite peut riposter)", TestCombatStillResolvesWhileWaiting, ref passed, ref failed);
        Run("Attente : le chemin reprend et atteint le point final APRES la halte", TestPathResumesAfterWait, ref passed, ref failed);
    }

    private static int MaxTick(List<TacticalEvent> events, string unitId)
    {
        int max = 0;
        foreach (var e in events) if (e.unitId == unitId && e.tick > max) max = e.tick;
        return max;
    }

    private static bool TestWaitDelaysArrivalAtNextCheckpoint()
    {
        var state = MakeEmptyState();
        var unit = MakeInfantry("Attendeur", 1, new Vector2(0, 0));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        // 5m, halte de 30s, puis encore 5m.
        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Attendeur",
                new PathCheckpoint { position = new Vector2(5, 0), waitSeconds = 30f },
                new PathCheckpoint { position = new Vector2(10, 0) }) },
            new List<UnitOrders>(), null);

        // Sans la halte, ~10 ticks (10m a 1m/tick) + une courte fenetre de combat suffiraient.
        // Avec 30s de halte a TickSeconds=0.25s, il faut au moins 120 ticks SUPPLEMENTAIRES.
        int maxTick = MaxTick(events, "Attendeur");
        bool arrived = Vector2.Distance(unit.position, new Vector2(10, 0)) < 0.5f;
        return arrived && maxTick >= 120;
    }

    private static bool TestNoWaitMeansNoDelay()
    {
        var state = MakeEmptyState();
        var unit = MakeInfantry("Marcheur", 1, new Vector2(0, 0));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        // Meme trajet, sans halte : doit rester tres en dessous de 120 ticks.
        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Marcheur",
                new PathCheckpoint { position = new Vector2(5, 0) },
                new PathCheckpoint { position = new Vector2(10, 0) }) },
            new List<UnitOrders>(), null);

        int maxTick = MaxTick(events, "Marcheur");
        bool arrived = Vector2.Distance(unit.position, new Vector2(10, 0)) < 0.5f;
        return arrived && maxTick < 30;
    }

    private static bool TestUnitDoesNotMoveWhileWaiting()
    {
        var state = MakeEmptyState();
        var unit = MakeInfantry("Statique", 1, new Vector2(0, 0));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Statique",
                new PathCheckpoint { position = new Vector2(5, 0), waitSeconds = 30f },
                new PathCheckpoint { position = new Vector2(10, 0) }) },
            new List<UnitOrders>(), null);

        var moves = events.Where(e => e.unitId == "Statique" && e.kind == TacticalEvent.Kind.Move).OrderBy(e => e.tick).ToList();
        if (moves.Count < 2) return false;

        // Repere le tick d'arrivee au point d'attente (5,0), puis le tick du PROCHAIN mouvement
        // enregistre : l'ecart doit correspondre a la halte (~120 ticks), pas a un mouvement continu
        // (ce qui prouverait qu'aucun evenement Move n'a ete emis PENDANT l'attente elle-meme).
        int arrivalTick = -1;
        foreach (var m in moves)
        {
            if (Vector2.Distance(m.position, new Vector2(5, 0)) < 0.05f) { arrivalTick = m.tick; break; }
        }
        if (arrivalTick < 0) return false;

        var nextMove = moves.FirstOrDefault(m => m.tick > arrivalTick && Vector2.Distance(m.position, new Vector2(5, 0)) > 0.05f);
        if (nextMove == null) return false;

        int gap = nextMove.tick - arrivalTick;
        return gap >= 118 && gap <= 122; // ~120 ticks attendus, petite marge sur l'arrondi
    }

    private static bool TestCombatStillResolvesWhileWaiting()
    {
        var state = MakeEmptyState();

        // Un tireur ennemi immobile, a portee, ligne de vue degagee, qui attend juste que l'unite
        // en halte reste assez longtemps dans sa portee pour l'engager (cooldown 0.35s << 30s
        // d'attente : plusieurs tirs doivent avoir lieu).
        var waiter = MakeInfantry("EnAttente", 1, new Vector2(0, 0));
        waiter.movementBudget = 250f;
        var shooter = MakeInfantry("Tireur", 2, new Vector2(5, 3));
        shooter.engagementRange = 20f;
        shooter.spottingRange = 20f;
        state.units.Add(waiter);
        state.units.Add(shooter);

        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("EnAttente", new PathCheckpoint { position = new Vector2(5, 0), waitSeconds = 30f }) },
            new List<UnitOrders>(), null);

        bool anyShotDuringWait = events.Any(e => e.kind == TacticalEvent.Kind.Shot && e.unitId == "Tireur");
        return anyShotDuringWait;
    }

    private static bool TestPathResumesAfterWait()
    {
        var state = MakeEmptyState();
        var unit = MakeInfantry("Complet", 1, new Vector2(0, 0));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Complet",
                new PathCheckpoint { position = new Vector2(5, 0), waitSeconds = 5f },
                new PathCheckpoint { position = new Vector2(5, 8) },
                new PathCheckpoint { position = new Vector2(-3, 8) }) },
            new List<UnitOrders>(), null);

        return Vector2.Distance(unit.position, new Vector2(-3, 8)) < 0.5f;
    }
}
