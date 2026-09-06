// Tests de non-regression (2026-09-05) — un franchissement vertical (montee/descente de toit)
// tronque par le budget de deplacement du tour NE DOIT JAMAIS laisser une unite avec une position 2D
// d'un cote de la frontiere du toit et un zStrata/outputY de l'autre. Bug trouve et reproduit
// experimentalement par un audit adversarial, confirme en lisant TacticalResolver.ExpandOrder /
// TruncateToMovementBudget : le pas qui porte la VerticalTransition pouvait etre coupe EN SON MILIEU
// par l'interpolation lineaire de TruncateToMovementBudget, alors que la transition (qui met a jour
// zStrata/outputY) est retiree des que son index depasse le point de coupe.
//
// Meme partial class que TacticalCoreSelfTest.cs, lances par RunClientLogicTests().
using System.Collections.Generic;
using UnityEngine;
using Novgov.TacticalCore;

public static partial class TacticalCoreSelfTest
{
    private static void RunVerticalBudgetTests(ref int passed, ref int failed)
    {
        Run("Budget vs toit : une descente tronquee reste toujours coherente (position/strate accordees)", TestTruncatedDescentStaysConsistent, ref passed, ref failed);
        Run("Budget vs toit : une montee tronquee reste toujours coherente (position/strate accordees)", TestTruncatedClimbStaysConsistent, ref passed, ref failed);
    }

    /// <summary>Vrai si la strate annoncee de l'unite correspond a la geometrie reelle de sa position
    /// 2D vis-a-vis de <paramref name="roof"/> — l'invariant que le bug cassait.</summary>
    private static bool StrataMatchesGeometry(TacticalUnit unit, List<Vector2> roof)
    {
        bool insideRoof = GeometryMath.PointInPolygon(roof, unit.position);
        if (unit.zStrata == ZStrata.Toit) return insideRoof;
        return !insideRoof;
    }

    private static bool TestTruncatedDescentStaysConsistent()
    {
        var roofA = Rect(0, 0, 12, 12);
        var state0 = StateWithBuildings(roofA); // sert a fabriquer un budget "complet" une seule fois

        // Budget largement suffisant pour tout parcourir, sert de reference de longueur totale.
        var probe = MakeInfantry("Sonde", 1, new Vector2(6, 6));
        probe.zStrata = ZStrata.Toit;
        probe.outputY = 9f;
        probe.movementBudget = 250f;
        state0.units.Add(probe);
        Vector2 street = new Vector2(30, 6);
        TacticalResolver.Resolve(state0,
            new List<UnitOrders> { OrderTo("Sonde", new PathCheckpoint { position = street }) },
            new List<UnitOrders>(), null);
        float fullDistance = 250f; // borne large ; la boucle ci-dessous couvre largement l'intervalle utile

        // Balaye tout l'intervalle de budgets possibles par pas fins : AUCUNE valeur ne doit produire
        // un etat incoherent, pas seulement la valeur qui a fait echouer l'audit.
        for (float budget = 0.5f; budget < fullDistance; budget += 0.5f)
        {
            var state = StateWithBuildings(roofA);
            var unit = MakeInfantry("Sniper", 1, new Vector2(6, 6));
            unit.zStrata = ZStrata.Toit;
            unit.outputY = 9f;
            unit.movementBudget = budget;
            state.units.Add(unit);

            TacticalResolver.Resolve(state,
                new List<UnitOrders> { OrderTo("Sniper", new PathCheckpoint { position = street }) },
                new List<UnitOrders>(), null);

            if (!StrataMatchesGeometry(unit, roofA))
            {
                Debug.LogError($"[VerticalBudget] Descente incoherente a budget={budget}: position={unit.position} zStrata={unit.zStrata} outputY={unit.outputY}");
                return false;
            }

            // Une fois l'unite redescendue au sol, un budget plus grand ne peut plus revenir en
            // arriere vers le toit : dès que la descente est actée, inutile de continuer le balayage
            // au-delà (gagne du temps de test sans perdre de couverture).
            if (unit.zStrata == ZStrata.Sol && Vector2.Distance(unit.position, street) < 0.5f) break;
        }
        return true;
    }

    private static bool TestTruncatedClimbStaysConsistent()
    {
        var target = Rect(30, 0, 42, 12);

        for (float budget = 0.5f; budget < 60f; budget += 0.5f)
        {
            var state = StateWithBuildings(target);
            var unit = MakeInfantry("Grimpeur", 1, new Vector2(0, 6));
            unit.movementBudget = budget;
            state.units.Add(unit);

            Vector2 roofPoint = new Vector2(36, 6);
            TacticalResolver.Resolve(state,
                new List<UnitOrders> { OrderTo("Grimpeur", new PathCheckpoint { position = roofPoint, setPositionY = 9f }) },
                new List<UnitOrders>(), null);

            if (!StrataMatchesGeometry(unit, target))
            {
                Debug.LogError($"[VerticalBudget] Montee incoherente a budget={budget}: position={unit.position} zStrata={unit.zStrata} outputY={unit.outputY}");
                return false;
            }

            if (unit.zStrata == ZStrata.Toit && Vector2.Distance(unit.position, roofPoint) < 0.5f) break;
        }
        return true;
    }
}
