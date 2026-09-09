// Tests ajoutes le 2026-09-07 — ENTREE PAR UNE VRAIE POSITION DE PORTE.
//
// Pourquoi ce fichier existe alors que TacticalCoreSelfTest_BuildingEntry.cs (2026-09-06) couvre
// deja l'entree/sortie de batiment : ces tests-la placaient tous leur "doorPoint" 1 m A L'INTERIEUR
// de l'empreinte (ex. Vector2(31, 6) pour un batiment allant de x=30 a x=42). Or une VRAIE porte
// n'est jamais a l'interieur : CityGenerator la genere a `outwardNormal * 0.05f` de sa facade,
// donc 5 cm DEHORS, et DoorInteraction.GetOutsidePosition ajoute encore 1.2 m. Le moteur etait donc
// correct et teste, mais sur une entree qui ne se produisait jamais telle quelle en jeu.
//
// Consequences reelles du trou de couverture, corrigees le 2026-09-07 :
//   1. cote serveur, MatchGeometry.FindBuildingAt / BuildingStructure.FindBuildingAt utilisent un
//      PointInPolygon STRICT : pour un point de porte ils renvoyaient toujours "aucun batiment", donc
//      checkpoint.enterBuildingId restait a -1 et l'unite n'entrait JAMAIS (et GUETTER PAR LA PORTE
//      accordait -75% de degats subis sans rattacher l'unite a un batiment, la rendant intouchable) ;
//   2. cote moteur, franchir le seuil posait l'unite PILE sur le point de porte — donc dehors — tout
//      en la marquant "a l'interieur", un etat incoherent.
// Le point 1 se corrige hors de TacticalCore (FindBuildingAtOrNear) ; les tests ci-dessous couvrent
// la primitive geometrique qui le rend possible, et le point 2 de bout en bout.
using System.Collections.Generic;
using UnityEngine;
using Novgov.TacticalCore;

public static partial class TacticalCoreSelfTest
{
    private static void RunDoorEntryTests(ref int passed, ref int failed)
    {
        Run("Porte : distance point-segment bornee AU segment (pas a la droite)", TestSqrDistancePointToSegmentClamps, ref passed, ref failed);
        Run("Porte : distance au contour d'un polygone, vue de dehors ET de dedans", TestSqrDistanceToPolygonEdge, ref passed, ref failed);
        Run("Porte : une vraie position de porte (5 cm dehors) est bien a portee de rattachement", TestRealDoorPositionIsWithinAttachTolerance, ref passed, ref failed);
        Run("Porte : le seuil exterieur (1.25 m dehors) l'est aussi, mais pas un point lointain", TestOutsideThresholdWithinToleranceButNotFarPoint, ref passed, ref failed);
        Run("Porte : rattachement deterministe a egalite exacte de distance", TestAttachIsDeterministicOnExactTie, ref passed, ref failed);
        Run("Porte : entrer par un point de porte DEHORS pose l'unite DEDANS", TestEnterFromRealDoorPositionLandsInside, ref passed, ref failed);
        Run("Porte : entrer par la porte puis continuer atteint le point interieur choisi", TestEnterFromDoorThenContinueReachesInterior, ref passed, ref failed);
    }

    /// <summary>Tolerance de rattachement d'un point de porte a son batiment. Miroir de
    /// Novgov.Server.MatchGeometry.DoorAttachToleranceMeters — duplique ici parce que le harnais
    /// hors-editeur ne compile pas Assets/Scripts/Server (trop dependant de la scene vivante).</summary>
    private const float DoorAttachTolerance = 2f;

    private static bool TestSqrDistancePointToSegmentClamps()
    {
        Vector2 a = new Vector2(0, 0), b = new Vector2(10, 0);

        // Perpendiculaire, projection A L'INTERIEUR du segment.
        if (!Approx(GeometryMath.SqrDistancePointToSegment(new Vector2(4, 3), a, b), 9f)) return false;

        // Au-dela de l'extremite : doit mesurer la distance A L'EXTREMITE, pas a la droite infinie
        // (une droite infinie donnerait 9, pas 25 — c'est tout l'interet du bornage).
        if (!Approx(GeometryMath.SqrDistancePointToSegment(new Vector2(14, 3), a, b), 16f + 9f)) return false;
        if (!Approx(GeometryMath.SqrDistancePointToSegment(new Vector2(-3, 4), a, b), 9f + 16f)) return false;

        // Segment degenere : ne doit pas diviser par zero.
        if (!Approx(GeometryMath.SqrDistancePointToSegment(new Vector2(3, 4), a, a), 25f)) return false;

        return true;
    }

    private static bool TestSqrDistanceToPolygonEdge()
    {
        var square = Rect(0, 0, 10, 10);

        // Dehors, a 0.05 m du mur ouest : exactement la position d'une porte generee.
        if (!Approx(GeometryMath.SqrDistanceToPolygonEdge(square, new Vector2(-0.05f, 5f)), 0.0025f)) return false;

        // Pile sur le contour.
        if (!Approx(GeometryMath.SqrDistanceToPolygonEdge(square, new Vector2(0f, 5f)), 0f)) return false;

        // Au centre : la distance au CONTOUR vaut 5 (le test ne dit rien de l'interieur/exterieur,
        // c'est le role de PointInPolygon — les deux se combinent dans FindBuildingAtOrNear).
        if (!Approx(GeometryMath.SqrDistanceToPolygonEdge(square, new Vector2(5f, 5f)), 25f)) return false;

        return true;
    }

    /// <summary>Liste de batiments minimale pour interroger GeometryMath.FindBuildingAtOrNear —
    /// l'id DOIT valoir l'index (invariant garanti par TacticalGridBuilder).</summary>
    private static List<TacticalBuilding> BuildingsFrom(params List<Vector2>[] footprints)
    {
        var list = new List<TacticalBuilding>();
        for (int i = 0; i < footprints.Length; i++)
            list.Add(new TacticalBuilding { id = i, footprint = footprints[i], height = 6f });
        return list;
    }

    private static bool TestRealDoorPositionIsWithinAttachTolerance()
    {
        var footprint = Rect(30, 0, 42, 12);
        var buildings = BuildingsFrom(footprint);

        // Reproduction exacte de CityGenerator : position sur la facade + outwardNormal * 0.05f.
        Vector2 doorPos = new Vector2(30f - 0.05f, 6f);

        // Le coeur du bug : le test d'appartenance STRICT echoue pour une vraie porte...
        if (GeometryMath.PointInPolygon(footprint, doorPos)) return false;

        // ...alors que la recherche TOLERANTE — celle que le serveur utilise desormais pour
        // EntrerBatiment/GuetterPorte — retrouve bien le batiment. CE test echoue si l'on revient
        // a la recherche stricte.
        return GeometryMath.FindBuildingAtOrNear(buildings, doorPos, DoorAttachTolerance) == 0;
    }

    private static bool TestOutsideThresholdWithinToleranceButNotFarPoint()
    {
        var footprint = Rect(30, 0, 42, 12);
        var buildings = BuildingsFrom(footprint);

        // DoorInteraction.GetOutsidePosition = position de porte + entryDirection * 1.2f.
        Vector2 outsideThreshold = new Vector2(30f - 0.05f - 1.2f, 6f);
        if (GeometryMath.FindBuildingAtOrNear(buildings, outsideThreshold, DoorAttachTolerance) != 0) return false;

        // En revanche un point franchement dans la rue ne doit JAMAIS etre rattache : sinon
        // n'importe quel deplacement au sol pres d'une facade vaudrait "entrer dans le batiment".
        Vector2 street = new Vector2(20f, 6f);
        if (GeometryMath.FindBuildingAtOrNear(buildings, street, DoorAttachTolerance) != -1) return false;

        // Un point BIEN a l'interieur reste evidemment rattache (chemin rapide, sans tolerance).
        return GeometryMath.FindBuildingAtOrNear(buildings, new Vector2(36f, 6f), DoorAttachTolerance) == 0;
    }

    /// <summary>A egalite exacte de distance entre deux batiments, c'est TOUJOURS le plus petit
    /// index qui gagne — le serveur resout la geometrie de la meme partie sur des machines
    /// differentes, un depart d'ordre de balayage suffirait a les faire diverger.</summary>
    private static bool TestAttachIsDeterministicOnExactTie()
    {
        // Deux batiments strictement symetriques autour de x=10 : le point (10,6) est a 1 m de
        // chacun, exactement.
        var left = Rect(0, 0, 9, 12);
        var right = Rect(11, 0, 20, 12);
        var buildings = BuildingsFrom(left, right);

        Vector2 tie = new Vector2(10f, 6f);
        int first = GeometryMath.FindBuildingAtOrNear(buildings, tie, DoorAttachTolerance);
        for (int i = 0; i < 50; i++)
        {
            if (GeometryMath.FindBuildingAtOrNear(buildings, tie, DoorAttachTolerance) != first) return false;
        }
        return first == 0; // le plus petit index, jamais l'autre
    }

    private static bool TestEnterFromRealDoorPositionLandsInside()
    {
        var footprint = Rect(30, 0, 42, 12);
        var state = StateWithBuildings(footprint);

        var unit = MakeInfantry("Fantassin", 1, new Vector2(0, 6));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        // Le checkpoint que le serveur produit une fois le rattachement repare : position de la
        // VRAIE porte (dehors) + enterBuildingId resolu par FindBuildingAtOrNear.
        Vector2 doorPos = new Vector2(30f - 0.05f, 6f);
        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Fantassin", new PathCheckpoint { position = doorPos, enterBuildingId = 0 }) },
            new List<UnitOrders>(), null);

        // Avant le correctif, l'unite s'arretait PILE sur doorPos, donc dehors, tout en etant
        // marquee a l'interieur : incoherence qui faussait ensuite le confinement du deplacement
        // interieur et l'exemption de mur de LineOfSight.
        if (!GeometryMath.PointInPolygon(footprint, unit.position)) return false;
        return unit.currentBuildingId == 0 && unit.zStrata == ZStrata.Sol;
    }

    private static bool TestEnterFromDoorThenContinueReachesInterior()
    {
        var footprint = Rect(30, 0, 42, 12);
        var blocker = Rect(12, -8, 22, 20); // pile sur le chemin : l'approche doit le contourner
        var state = StateWithBuildings(footprint, blocker);

        var unit = MakeInfantry("Fantassin", 1, new Vector2(0, 6));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        Vector2 doorPos = new Vector2(30f - 0.05f, 6f);
        Vector2 interiorPoint = new Vector2(38, 6);
        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Fantassin",
                new PathCheckpoint { position = doorPos, enterBuildingId = 0 },
                new PathCheckpoint { position = interiorPoint }) },
            new List<UnitOrders>(), null);

        foreach (var e in Moves(events, "Fantassin"))
        {
            if (GeometryMath.PointInPolygon(blocker, e.position)) return false; // aurait traverse
        }
        return Vector2.Distance(unit.position, interiorPoint) < 1.5f && unit.currentBuildingId == 0;
    }

    private static bool Approx(float a, float b) => Mathf.Abs(a - b) < 1e-4f;
}
