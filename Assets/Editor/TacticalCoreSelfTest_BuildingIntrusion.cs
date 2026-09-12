using System.Collections.Generic;
using UnityEngine;
using Novgov.TacticalCore;

/// <summary>
/// Retour de jeu 2026-09-12 (PvP Deathmatch multijoueur réel) : « les unités rentrent dans les
/// polygones ». Root cause trouvée en lisant TacticalResolver.ExpandOrder (branche "trajet
/// ordinaire", ~ligne 480) : le correctif du 2026-09-06 ("un char peut foncer dans un bâtiment",
/// voir TacticalCoreSelfTest_InfantryPathSim et TestResolveMovementAvoidsBuilding — aucun des deux
/// ne teste en fait CE cas précis, voir plus bas) ne redirigeait la destination hors du bâtiment
/// QUE pour <c>unit.isTank</c>. Toute AUTRE unité (fantassin, véhicule, mortier) dont la destination
/// géométrique tombe dans l'empreinte d'un bâtiment qu'elle n'a JAMAIS explicitement demandé à
/// visiter (aucun enterBuildingId, pas déjà à l'intérieur) retombait dans AppendLeg avec
/// confinement=null : Pathfinding.FindPath échoue toujours sur une cellule d'arrivée impraticable
/// (creusée par TacticalGrid.CarveBuildingInteriors), et son repli en ligne droite traverse le mur
/// sans la moindre vérification — exactement le symptôme décrit.
///
/// Différence avec les tests déjà existants qui manipulent un bâtiment
/// (TestResolveMovementAvoidsBuilding, TestInfantryLongPathAroundBuildingLogged) : leur destination
/// est toujours DERRIÈRE le bâtiment (donc hors de son empreinte), ce qui exerce le contournement
/// A* — jamais le cas où la destination elle-même tombe DANS l'empreinte sans ordre d'entrée
/// explicite. C'est précisément l'angle mort qui laissait passer ce bug.
/// </summary>
public static partial class TacticalCoreSelfTest
{
    private static void RunBuildingIntrusionTests(ref int passed, ref int failed)
    {
        Run("Résolution : un fantassin dont la destination tombe dans un bâtiment (sans ordre d'entrée) est redirigé dehors, pas téléporté à travers le mur",
            TestInfantryDestinationInsideBuildingRedirectedOutside, ref passed, ref failed);
        Run("Résolution : un véhicule (non-char) dont la destination tombe dans un bâtiment est lui aussi redirigé dehors",
            TestNonTankVehicleDestinationInsideBuildingRedirectedOutside, ref passed, ref failed);
        Run("Résolution : un char dont la destination tombe dans un bâtiment reste redirigé dehors (non-régression du correctif 2026-09-06)",
            TestTankDestinationInsideBuildingStillRedirectedOutside, ref passed, ref failed);
        Run("Résolution : une entrée EXPLICITE (enterBuildingId) place toujours bien l'unité à l'intérieur — la redirection ne doit pas la bloquer dehors",
            TestExplicitBuildingEntryStillEntersInside, ref passed, ref failed);
    }

    private static List<Vector2> MakeIntrusionFootprint() => new List<Vector2> {
        new Vector2(-5, -5), new Vector2(5, -5), new Vector2(5, 5), new Vector2(-5, 5)
    };

    private static TacticalWorldState MakeIntrusionState(out List<Vector2> footprint)
    {
        footprint = MakeIntrusionFootprint();
        var state = new TacticalWorldState { grid = new TacticalGrid(-30f, -30f, 60, 60) };
        state.grid.CarveBuildingInteriors(new List<List<Vector2>> { footprint });
        state.buildings.Add(new TacticalBuilding { id = 0, footprint = footprint, health = 300f });
        return state;
    }

    /// <summary>Aucune Move ne doit jamais retomber dans l'empreinte, ET la position finale doit être
    /// dehors — les deux sont nécessaires : une unité pourrait "sauter" par-dessus l'intérieur sans
    /// qu'aucun Move loggé ne soit dedans si le pas est trop grossier, d'où la double vérification.</summary>
    private static bool CheckNeverEntersAndEndsOutside(List<TacticalEvent> events, TacticalUnit unit, List<Vector2> footprint)
    {
        foreach (var e in events)
        {
            if (e.kind != TacticalEvent.Kind.Move) continue;
            if (GeometryMath.PointInPolygon(footprint, e.position))
            {
                Debug.LogError($"[BuildingIntrusion] Move dans l'empreinte : ({e.position.x:F2}, {e.position.y:F2})");
                return false;
            }
        }
        if (GeometryMath.PointInPolygon(footprint, unit.position))
        {
            Debug.LogError($"[BuildingIntrusion] Position finale dans l'empreinte : ({unit.position.x:F2}, {unit.position.y:F2})");
            return false;
        }
        return true;
    }

    private static bool TestInfantryDestinationInsideBuildingRedirectedOutside()
    {
        var state = MakeIntrusionState(out var footprint);
        var unit = MakeInfantry("Fantassin_Intrus", 1, new Vector2(-15, 0));
        state.units.Add(unit);

        // Destination choisie EN PLEIN CENTRE de l'empreinte, comme le ferait un tap/checkpoint
        // qui recouvre géométriquement le bâtiment sans jamais passer par le menu "Entrer".
        var order = new UnitOrders { unitId = "Fantassin_Intrus", checkpoints = new List<PathCheckpoint> { new PathCheckpoint { position = Vector2.zero } } };
        var events = TacticalResolver.Resolve(state, new List<UnitOrders> { order }, new List<UnitOrders>(), null);

        return CheckNeverEntersAndEndsOutside(events, unit, footprint);
    }

    private static bool TestNonTankVehicleDestinationInsideBuildingRedirectedOutside()
    {
        var state = MakeIntrusionState(out var footprint);
        var unit = new TacticalUnit
        {
            id = "VehiculeCanon_Intrus", team = 1, position = new Vector2(-15, 0), health = 250,
            engagementRange = 45f, spottingRange = 35f, weaponDamage = 75, weaponCooldownSeconds = 1.4f,
            movementBudget = 46f, isTank = false,
        };
        state.units.Add(unit);

        var order = new UnitOrders { unitId = "VehiculeCanon_Intrus", checkpoints = new List<PathCheckpoint> { new PathCheckpoint { position = Vector2.zero } } };
        var events = TacticalResolver.Resolve(state, new List<UnitOrders> { order }, new List<UnitOrders>(), null);

        return CheckNeverEntersAndEndsOutside(events, unit, footprint);
    }

    private static bool TestTankDestinationInsideBuildingStillRedirectedOutside()
    {
        var state = MakeIntrusionState(out var footprint);
        var unit = new TacticalUnit
        {
            id = "CharLeopard_Intrus", team = 1, position = new Vector2(-15, 0), health = 500,
            engagementRange = 45f, spottingRange = 35f, weaponDamage = 150, weaponCooldownSeconds = 1.8f,
            movementBudget = 42f, isTank = true,
        };
        state.units.Add(unit);

        var order = new UnitOrders { unitId = "CharLeopard_Intrus", checkpoints = new List<PathCheckpoint> { new PathCheckpoint { position = Vector2.zero } } };
        var events = TacticalResolver.Resolve(state, new List<UnitOrders> { order }, new List<UnitOrders>(), null);

        return CheckNeverEntersAndEndsOutside(events, unit, footprint);
    }

    /// <summary>Garde-fou symétrique : la généralisation de la redirection à toutes les unités ne
    /// doit surtout pas empêcher une entrée VOULUE (enterBuildingId posé par EntrerBatiment) —
    /// seule la branche "trajet ordinaire" (targetInterior == currentInterior == -1) doit rediriger,
    /// jamais la branche d'entrée explicite.</summary>
    private static bool TestExplicitBuildingEntryStillEntersInside()
    {
        var state = MakeIntrusionState(out var footprint);
        var unit = MakeInfantry("Fantassin_Entree", 1, new Vector2(-15, 0));
        state.units.Add(unit);

        var order = new UnitOrders
        {
            unitId = "Fantassin_Entree",
            checkpoints = new List<PathCheckpoint> { new PathCheckpoint { position = Vector2.zero, enterBuildingId = 0 } }
        };
        TacticalResolver.Resolve(state, new List<UnitOrders> { order }, new List<UnitOrders>(), null);

        return GeometryMath.PointInPolygon(footprint, unit.position);
    }
}
