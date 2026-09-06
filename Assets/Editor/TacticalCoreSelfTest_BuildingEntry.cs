// Tests ajoutes le 2026-09-06 — entree/sortie de batiment AU SOL (pas le toit, deja couvert par
// TacticalCoreSelfTest_Roof.cs). Plainte explicite du joueur : "le fantassin n'arrive pas a bien
// monter dans l'immeuble et ne rentre pas". Meme partial class que TacticalCoreSelfTest.cs, lances
// par RunAll() via RunClientLogicTests() -> RunBuildingEntryTests().
//
// Bug reproduit : TacticalGrid.CarveBuildingInteriors rend TOUT l'interieur d'un batiment
// impraticable dans la grille au sol (c'est ce qui empeche une unite de couper au travers). Un
// ordre "ENTRER DANS LE BATIMENT" (ou le noeud "Continuer" ajoute juste apres pour rejoindre un
// point choisi a l'interieur, voir TacticalPathManager_ContextMenu.ConfirmerBuildingAction choix 1)
// vise pourtant PRECISEMENT un point dans cette zone impraticable. Avant le correctif,
// ExpandOrder appelait AppendLeg directement dessus : Pathfinding.FindPath echouait
// systematiquement (cellule d'arrivee impraticable), et le repli en ligne droite partait de la
// position ACTUELLE de l'unite, a travers n'importe quel obstacle entre les deux — exactement
// comme le bug de "raccourci a travers un batiment" deja corrige pour le deplacement normal, mais
// jamais traite pour l'entree/la sortie d'un batiment.
using System.Collections.Generic;
using UnityEngine;
using Novgov.TacticalCore;

public static partial class TacticalCoreSelfTest
{
    private static void RunBuildingEntryTests(ref int passed, ref int failed)
    {
        Run("Entree : rejoint la porte en contournant un batiment genant", TestEnterBuildingApproachWalksAroundObstacle, ref passed, ref failed);
        Run("Entree : le \"Continuer\" qui suit atteint bien le point choisi a l'interieur", TestEnterThenContinueReachesInteriorPoint, ref passed, ref failed);
        Run("Entree : rattache l'unite au batiment (currentBuildingId)", TestEnterBuildingAssignsBuildingId, ref passed, ref failed);
        Run("Sortie : depuis l'interieur, contourne un batiment genant pour rejoindre la rue", TestExitBuildingApproachWalksAroundObstacle, ref passed, ref failed);
        Run("Sortie : detache l'unite du batiment (currentBuildingId remis a -1)", TestExitBuildingClearsBuildingId, ref passed, ref failed);
    }

    private static bool TestEnterBuildingApproachWalksAroundObstacle()
    {
        var target = Rect(30, 0, 42, 12);     // batiment a rejoindre
        var blocker = Rect(12, -8, 22, 20);   // pile entre l'unite et lui
        var state = StateWithBuildings(target, blocker);

        var unit = MakeInfantry("Fantassin", 1, new Vector2(0, 6));
        unit.movementBudget = 250f; // itineraire teste, pas la limite de distance
        state.units.Add(unit);

        Vector2 doorPoint = new Vector2(31, 6); // 1m a l'interieur du mur ouest : creuse non-franchissable
        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Fantassin", new PathCheckpoint { position = doorPoint, enterBuildingId = 0 }) },
            new List<UnitOrders>(), null);

        foreach (var e in Moves(events, "Fantassin"))
        {
            if (GeometryMath.PointInPolygon(blocker, e.position)) return false; // aurait traverse le batiment genant
        }
        return Vector2.Distance(unit.position, doorPoint) < 1.5f;
    }

    private static bool TestEnterThenContinueReachesInteriorPoint()
    {
        var target = Rect(30, 0, 42, 12);
        var blocker = Rect(12, -8, 22, 20);
        var state = StateWithBuildings(target, blocker);

        var unit = MakeInfantry("Fantassin", 1, new Vector2(0, 6));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        // Reproduit exactement la sequence posee par ConfirmerBuildingAction choix 1 : un noeud
        // EntrerBatiment (rattachement explicite) suivi d'un simple "Continuer" vers le point
        // choisi par le joueur a l'interieur — ce second checkpoint ne porte AUCUN drapeau, seule
        // sa position (bien a l'interieur de l'empreinte) doit le trahir.
        Vector2 doorPoint = new Vector2(31, 6);
        Vector2 interiorPoint = new Vector2(38, 6);
        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Fantassin",
                new PathCheckpoint { position = doorPoint, enterBuildingId = 0 },
                new PathCheckpoint { position = interiorPoint }) },
            new List<UnitOrders>(), null);

        foreach (var e in Moves(events, "Fantassin"))
        {
            if (GeometryMath.PointInPolygon(blocker, e.position)) return false;
        }
        return Vector2.Distance(unit.position, interiorPoint) < 1.5f && unit.currentBuildingId == 0;
    }

    private static bool TestEnterBuildingAssignsBuildingId()
    {
        var target = Rect(30, 0, 42, 12);
        var state = StateWithBuildings(target);

        var unit = MakeInfantry("Fantassin", 1, new Vector2(0, 6));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Fantassin", new PathCheckpoint { position = new Vector2(31, 6), enterBuildingId = 0 }) },
            new List<UnitOrders>(), null);

        // Sans ce rattachement, un ordre suivant ne sait plus que l'unite est deja a l'interieur
        // (voir TestEnterThenContinueReachesInteriorPoint) et la garnison reste vulnerable au tir
        // direct (meme classe de bug que TestGarrisonedUnitIsNotInvulnerable, cote toit).
        return unit.currentBuildingId == 0 && unit.zStrata == ZStrata.Sol;
    }

    private static bool TestExitBuildingApproachWalksAroundObstacle()
    {
        var target = Rect(30, 0, 42, 12);
        var blocker = Rect(12, -8, 22, 20);
        var state = StateWithBuildings(target, blocker);

        // L'unite est DEJA a l'interieur (etat herite d'un tour precedent, comme currentRoof pour
        // un sniper sur son toit dans les tests de TacticalCoreSelfTest_Roof.cs).
        var unit = MakeInfantry("Fantassin", 1, new Vector2(38, 6));
        unit.currentBuildingId = 0;
        unit.movementBudget = 250f;
        state.units.Add(unit);

        Vector2 street = new Vector2(0, 6); // de l'autre cote du batiment genant
        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Fantassin", new PathCheckpoint { position = street, exitBuilding = true }) },
            new List<UnitOrders>(), null);

        foreach (var e in Moves(events, "Fantassin"))
        {
            if (GeometryMath.PointInPolygon(blocker, e.position)) return false;
        }
        return Vector2.Distance(unit.position, street) < 1.5f;
    }

    private static bool TestExitBuildingClearsBuildingId()
    {
        var target = Rect(30, 0, 42, 12);
        var state = StateWithBuildings(target);

        var unit = MakeInfantry("Fantassin", 1, new Vector2(38, 6));
        unit.currentBuildingId = 0;
        unit.movementBudget = 250f;
        state.units.Add(unit);

        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Fantassin", new PathCheckpoint { position = new Vector2(20, 6), exitBuilding = true }) },
            new List<UnitOrders>(), null);

        return unit.currentBuildingId == -1;
    }
}
