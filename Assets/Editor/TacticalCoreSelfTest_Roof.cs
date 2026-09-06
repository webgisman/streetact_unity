// Tests ajoutes le 2026-09-03 — traversee des toits, fenetre de combat, garnison, budget de
// deplacement. Partie du meme partial class que TacticalCoreSelfTest.cs : lances par RunAll() via
// RunExtraTests(). Ils n'utilisent QUE Vector2/Mathf et les types de Novgov.TacticalCore, jamais un
// composant de scene, ce qui permet de les executer aussi hors de l'editeur Unity.
using System.Collections.Generic;
using UnityEngine;
using Novgov.TacticalCore;

/// <summary>
/// Tests de traversee de TOIT (2026-09-03) — la plainte explicite du joueur : "souci de
/// deplacement des unites, notamment l'infanterie quand elle monte sur le toit".
///
/// Semantique VOULUE, verifiee ici :
///   1. Tant qu'une unite est sur un toit (zStrata == Toit), son deplacement reste CONFINE a
///      l'empreinte du batiment sur lequel elle se tient — elle ne traverse jamais le vide.
///   2. Un ordre vers un point qui n'est pas sur ce toit fait descendre l'unite par le bord le
///      plus proche de la cible, puis elle continue au sol par un vrai chemin.
///   3. Un ordre d'Escalade fait marcher l'unite AU SOL jusqu'au pied du batiment vise (en
///      contournant ce qui gene), puis elle monte — jamais une ligne droite a travers le decor.
/// </summary>
public static partial class TacticalCoreSelfTest
{
    private static void RunExtraTests(ref int passed, ref int failed)
    {
        Run("Toit : une unite sur un toit concave (L) reste sur son empreinte", TestRooftopUnitStaysOnItsOwnRoof, ref passed, ref failed);
        Run("Toit : atteint bien l'autre extremite de son propre toit", TestRooftopUnitReachesFarSideOfItsRoof, ref passed, ref failed);
        Run("Toit : un ordre vers la rue ne traverse pas un autre batiment", TestRooftopUnitToStreetDoesNotCrossOtherBuildings, ref passed, ref failed);
        Run("Toit : apres un ordre vers la rue, l'unite finit au Sol", TestRooftopUnitEndsOnGroundWhenOrderedToStreet, ref passed, ref failed);
        Run("Escalade : le trajet au sol contourne un batiment genant", TestClimbApproachWalksAroundObstacle, ref passed, ref failed);
        Run("Escalade : l'unite finit sur le Toit a la hauteur du batiment", TestClimbEndsOnRoofStrata, ref passed, ref failed);

        Run("Toit : un tireur perche voit et engage par-dessus SON parapet", TestRooftopShooterFiresOverOwnParapet, ref passed, ref failed);
        Run("Toit : un tireur perche est reperable depuis la rue", TestRooftopUnitCanBeSpottedFromStreet, ref passed, ref failed);
        Run("Toit : un tireur perche est vulnerable depuis la rue (pas invincible)", TestRooftopUnitCanBeShotFromStreet, ref passed, ref failed);
        Run("Toit : un AUTRE batiment bloque toujours la ligne de vue", TestRooftopShooterStillBlockedByOtherBuilding, ref passed, ref failed);
        Run("Escalade : le checkpoint rattache l'unite a son batiment", TestClimbAssignsBuildingId, ref passed, ref failed);

        Run("Combat : un duel ne se resout PAS a mort en un seul tour", TestDuelDoesNotResolveToDeathInOneTurn, ref passed, ref failed);
        Run("Combat : la fenetre de tir est bornee apres la fin des mouvements", TestCombatWindowIsBounded, ref passed, ref failed);
        Run("Combat : la cadence de tir respecte le cooldown nominal", TestFireRateMatchesNominalCooldown, ref passed, ref failed);

        Run("Garnison : une unite en garnison peut etre touchee en retour", TestGarrisonedUnitIsNotInvulnerable, ref passed, ref failed);
        Run("Garnison : elle beneficie bien de la couverture -75%", TestGarrisonedUnitStillGetsCover, ref passed, ref failed);

        Run("Budget : un ordre trop long s'arrete au budget de deplacement", TestMovementBudgetIsEnforced, ref passed, ref failed);
        Run("Budget : un ordre court n'est pas tronque", TestShortOrderIsNotTruncated, ref passed, ref failed);
        Run("Budget : une posture hors budget n'est pas appliquee d'avance", TestPostureBeyondBudgetIsNotApplied, ref passed, ref failed);
    }

    // ---- 6. budget de deplacement ---------------------------------------------------------

    private static bool TestMovementBudgetIsEnforced()
    {
        var state = MakeEmptyState();
        var unit = MakeInfantry("Marcheur", 1, new Vector2(0, 0));
        unit.movementBudget = 20f;
        state.units.Add(unit);

        // 200m demandes, 20m autorises.
        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Marcheur", new PathCheckpoint { position = new Vector2(200, 0) }) },
            new List<UnitOrders>(), null);

        float travelled = Vector2.Distance(unit.position, new Vector2(0, 0));
        return travelled > 15f && travelled < 25f;
    }

    private static bool TestShortOrderIsNotTruncated()
    {
        var state = MakeEmptyState();
        var unit = MakeInfantry("Marcheur", 1, new Vector2(0, 0));
        unit.movementBudget = 50f;
        state.units.Add(unit);

        Vector2 goal = new Vector2(12, 0);
        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Marcheur", new PathCheckpoint { position = goal }) },
            new List<UnitOrders>(), null);

        return Vector2.Distance(unit.position, goal) < 1.5f;
    }

    private static bool TestPostureBeyondBudgetIsNotApplied()
    {
        var state = MakeEmptyState();
        var unit = MakeInfantry("Marcheur", 1, new Vector2(0, 0));
        unit.movementBudget = 10f;
        state.units.Add(unit);

        // Le "Guetter" est place a 100m : hors budget, il ne doit PAS s'appliquer ce tour-ci.
        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Marcheur", new PathCheckpoint { position = new Vector2(100, 0), setGuarding = true }) },
            new List<UnitOrders>(), null);

        return !unit.isGuarding;
    }

    // ---- 5. garnison ----------------------------------------------------------------------

    /// <summary>La garnison tirait par son embrasure (exemption "impact a moins de 2.5m du tireur")
    /// mais aucune riposte ne l'atteignait : l'exemption symetrique cote cible compare le mur au
    /// currentBuildingId de la cible, qui restait a -1 faute de rattachement. L'unite etait donc
    /// litteralement increvable au tir direct.</summary>
    private static bool TestGarrisonedUnitIsNotInvulnerable()
    {
        var house = Rect(0, 0, 10, 10);
        var state = StateWithBuildings(house);
        AddWalls(state, 0, house);

        var defender = MakeInfantry("Defenseur", 2, new Vector2(5, 9.6f)); // juste derriere le mur nord
        defender.isGarrisoned = true;
        defender.windowNormal = new Vector2(0, 1);
        defender.currentBuildingId = 0;

        var attacker = MakeInfantry("Assaillant", 1, new Vector2(5, 14));
        attacker.engagementRange = 20f;
        state.units.Add(defender);
        state.units.Add(attacker);

        return LineOfSight.CanEngageTarget(state, attacker, defender);
    }

    private static bool TestGarrisonedUnitStillGetsCover()
    {
        var state = MakeEmptyState();
        var shooter = MakeInfantry("Shooter", 1, new Vector2(0, 0));
        shooter.weaponDamage = 100;

        var garrisoned = MakeInfantry("Cible", 2, new Vector2(5, 0), health: 100000);
        garrisoned.isGarrisoned = true;
        state.units.Add(shooter);
        state.units.Add(garrisoned);

        var events = TacticalResolver.Resolve(state, new List<UnitOrders>(), new List<UnitOrders>(), null);
        return FirstShotDamage(events, "Shooter") == 25; // 100 * 0.25
    }

    // ---- 4. fenetre de combat -------------------------------------------------------------

    /// <summary>Fidelite au solo : TacticalPathManager_Execution ferme le combat apres 2.0s
    /// (`combatResolutionTimer < 2.0f`), soit 8 pas de 0.25s. Deux fantassins a 100 PV, 15 degats et
    /// 0.35s de cadence ne peuvent donc PAS s'entretuer dans le meme tour — le joueur garde la main
    /// pour se replier ou renforcer. Sans borne, la boucle ne s'arretait que quand un camp etait
    /// aneanti.</summary>
    private static bool TestDuelDoesNotResolveToDeathInOneTurn()
    {
        var state = MakeEmptyState();
        var a = MakeInfantry("A", 1, new Vector2(0, 0));
        var b = MakeInfantry("B", 2, new Vector2(6, 0));
        state.units.Add(a);
        state.units.Add(b);

        TacticalResolver.Resolve(state, new List<UnitOrders>(), new List<UnitOrders>(), null);

        // Les deux doivent survivre au tour, en ayant tout de meme echange des tirs.
        return !a.isDead && !b.isDead && a.health < 100 && b.health < 100;
    }

    private static bool TestCombatWindowIsBounded()
    {
        var state = MakeEmptyState();
        var a = MakeInfantry("A", 1, new Vector2(0, 0), health: 100000);
        var b = MakeInfantry("B", 2, new Vector2(6, 0), health: 100000);
        state.units.Add(a);
        state.units.Add(b);

        var events = TacticalResolver.Resolve(state, new List<UnitOrders>(), new List<UnitOrders>(), null);

        int lastTick = 0;
        foreach (var e in events) if (e.tick > lastTick) lastTick = e.tick;

        // Sans borne, ces deux unites increvables tiraient jusqu'au plafond de securite de 3200 pas
        // (13 minutes de rejeu pour UN tour).
        return lastTick <= 12;
    }

    private static bool TestFireRateMatchesNominalCooldown()
    {
        var state = MakeEmptyState();
        var shooter = MakeInfantry("Shooter", 1, new Vector2(0, 0), health: 100000);
        shooter.weaponDamage = 1;
        var target = MakeInfantry("Target", 2, new Vector2(5, 0), health: 100000);
        state.units.Add(shooter);
        state.units.Add(target);

        var events = TacticalResolver.Resolve(state, new List<UnitOrders>(), new List<UnitOrders>(), null);
        int shots = 0;
        foreach (var e in events) if (e.kind == TacticalEvent.Kind.Shot && e.unitId == "Shooter") shots++;

        // Fenetre de 2.0s a 0.35s de cadence => ~6 tirs. L'arrondi du cooldown au pas de simulation
        // (0.35 -> 0.5) ne donnait que 4 tirs, soit 30 DPS au lieu des 43 que suppose le cout de
        // deploiement des unites.
        return shots >= 5 && shots <= 7;
    }

    /// <summary>Un WallSegment bloquant par arête du polygone — exactement ce que
    /// TacticalGridBuilder produit pour un vrai bâtiment.</summary>
    private static void AddWalls(TacticalWorldState state, int buildingId, List<Vector2> footprint)
    {
        int n = footprint.Count;
        for (int i = 0; i < n; i++)
        {
            state.wallSegments.Add(new WallSegment
            {
                p1 = footprint[i],
                p2 = footprint[(i + 1) % n],
                buildingId = buildingId,
            });
        }
    }

    private static bool TestRooftopShooterFiresOverOwnParapet()
    {
        // Bâtiment de 20m de côté : depuis le centre du toit, la ligne vers la rue croise sa propre
        // façade à ~10m — bien au-delà de l'ancienne tolérance de 2.5m, donc le tireur ne pouvait
        // jamais faire feu.
        var roof = Rect(0, 0, 20, 20);
        var state = StateWithBuildings(roof);
        AddWalls(state, 0, roof);

        var sniper = MakeInfantry("Sniper", 1, new Vector2(10, 10));
        sniper.zStrata = ZStrata.Toit;
        sniper.outputY = 9f;
        sniper.currentBuildingId = 0;
        sniper.engagementRange = 15f; // +20 sur un toit => 35m

        var streetEnemy = MakeInfantry("Rue", 2, new Vector2(10, 32));
        state.units.Add(sniper);
        state.units.Add(streetEnemy);

        return LineOfSight.CanEngageTarget(state, sniper, streetEnemy);
    }

    private static bool TestRooftopUnitCanBeSpottedFromStreet()
    {
        var roof = Rect(0, 0, 20, 20);
        var state = StateWithBuildings(roof);
        AddWalls(state, 0, roof);

        var sniper = MakeInfantry("Sniper", 2, new Vector2(10, 10));
        sniper.zStrata = ZStrata.Toit;
        sniper.outputY = 9f;
        sniper.currentBuildingId = 0;

        var observer = MakeInfantry("Guetteur", 1, new Vector2(10, 32));
        state.units.Add(sniper);
        state.units.Add(observer);

        return LineOfSight.CanBeSpotted(state, observer, sniper);
    }

    private static bool TestRooftopUnitCanBeShotFromStreet()
    {
        var roof = Rect(0, 0, 20, 20);
        var state = StateWithBuildings(roof);
        AddWalls(state, 0, roof);

        var sniper = MakeInfantry("Sniper", 2, new Vector2(10, 10));
        sniper.zStrata = ZStrata.Toit;
        sniper.outputY = 9f;
        sniper.currentBuildingId = 0;

        var streetShooter = MakeInfantry("Tireur", 1, new Vector2(10, 32));
        streetShooter.engagementRange = 40f;
        state.units.Add(sniper);
        state.units.Add(streetShooter);

        return LineOfSight.CanEngageTarget(state, streetShooter, sniper);
    }

    private static bool TestRooftopShooterStillBlockedByOtherBuilding()
    {
        var roof = Rect(0, 0, 20, 20);
        var blocker = Rect(0, 26, 20, 34); // immeuble voisin, entre le toit et la cible
        var state = StateWithBuildings(roof, blocker);
        AddWalls(state, 0, roof);
        AddWalls(state, 1, blocker);

        var sniper = MakeInfantry("Sniper", 1, new Vector2(10, 10));
        sniper.zStrata = ZStrata.Toit;
        sniper.outputY = 9f;
        sniper.currentBuildingId = 0;
        sniper.engagementRange = 30f;

        var hidden = MakeInfantry("Abrite", 2, new Vector2(10, 40));
        state.units.Add(sniper);
        state.units.Add(hidden);

        // Le parapet de SON toit ne doit pas gêner, mais l'immeuble d'en face, si.
        return !LineOfSight.CanEngageTarget(state, sniper, hidden);
    }

    private static bool TestClimbAssignsBuildingId()
    {
        var target = Rect(30, 0, 42, 12);
        var state = StateWithBuildings(target);

        var unit = MakeInfantry("Grimpeur", 1, new Vector2(0, 6));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        Vector2 roofPoint = new Vector2(36, 6);
        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Grimpeur", new PathCheckpoint { position = roofPoint, setPositionY = 9f, enterBuildingId = 0 }) },
            new List<UnitOrders>(), null);

        // Sans ce rattachement, la ligne de vue ne peut pas savoir quel parapet est "le sien".
        return unit.currentBuildingId == 0 && unit.zStrata == ZStrata.Toit;
    }

    // ---- geometrie de test ----------------------------------------------------------------

    /// <summary>Empreinte en L : la ligne droite entre les deux extremites SORT de l'empreinte
    /// (elle coupe l'encoche), donc un deplacement qui reste dedans PROUVE qu'un vrai chemin a ete
    /// suivi et pas un repli en ligne droite.</summary>
    private static List<Vector2> LShapedFootprint()
    {
        return new List<Vector2> {
            new Vector2(0, 0), new Vector2(24, 0), new Vector2(24, 8),
            new Vector2(8, 8), new Vector2(8, 24), new Vector2(0, 24),
        };
    }

    private static Vector2 LTipA() { return new Vector2(21f, 4f); }  // branche horizontale
    private static Vector2 LTipB() { return new Vector2(4f, 21f); }  // branche verticale

    private static List<Vector2> Rect(float minX, float minZ, float maxX, float maxZ)
    {
        return new List<Vector2> {
            new Vector2(minX, minZ), new Vector2(maxX, minZ), new Vector2(maxX, maxZ), new Vector2(minX, maxZ),
        };
    }

    private static TacticalWorldState StateWithBuildings(params List<Vector2>[] footprints)
    {
        var state = new TacticalWorldState();
        var walkable = new List<List<Vector2>>();
        for (int i = 0; i < footprints.Length; i++)
        {
            state.buildings.Add(new TacticalBuilding { id = i, footprint = footprints[i], height = 9f });
            walkable.Add(footprints[i]);
        }
        state.grid = new TacticalGrid(-60f, -60f, 160, 160);
        state.grid.CarveBuildingInteriors(walkable);
        return state;
    }

    private static List<TacticalEvent> Moves(List<TacticalEvent> events, string unitId)
    {
        var res = new List<TacticalEvent>();
        foreach (var e in events) if (e.kind == TacticalEvent.Kind.Move && e.unitId == unitId) res.Add(e);
        return res;
    }

    private static UnitOrders OrderTo(string unitId, params PathCheckpoint[] cps)
    {
        var o = new UnitOrders { unitId = unitId };
        foreach (var c in cps) o.checkpoints.Add(c);
        return o;
    }

    // ---- 1. confinement au toit -----------------------------------------------------------

    private static bool TestRooftopUnitStaysOnItsOwnRoof()
    {
        var roof = LShapedFootprint();
        var state = StateWithBuildings(roof);

        var unit = MakeInfantry("Sniper", 1, LTipA());
        unit.zStrata = ZStrata.Toit;
        unit.outputY = 9f;
        unit.movementBudget = 250f;
        state.units.Add(unit);

        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Sniper", new PathCheckpoint { position = LTipB() }) },
            new List<UnitOrders>(), null);

        // Chaque position intermediaire doit rester SUR le toit — jamais dans le vide au-dessus
        // de la rue (l'encoche du L).
        foreach (var e in Moves(events, "Sniper"))
        {
            if (!GeometryMath.PointInPolygon(roof, e.position)) return false;
        }
        return true;
    }

    private static bool TestRooftopUnitReachesFarSideOfItsRoof()
    {
        var roof = LShapedFootprint();
        var state = StateWithBuildings(roof);

        var unit = MakeInfantry("Sniper", 1, LTipA());
        unit.zStrata = ZStrata.Toit;
        unit.outputY = 9f;
        unit.movementBudget = 250f;
        state.units.Add(unit);

        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Sniper", new PathCheckpoint { position = LTipB() }) },
            new List<UnitOrders>(), null);

        return Vector2.Distance(unit.position, LTipB()) < 1.5f && unit.zStrata == ZStrata.Toit;
    }

    // ---- 2. descente vers la rue ----------------------------------------------------------

    private static bool TestRooftopUnitToStreetDoesNotCrossOtherBuildings()
    {
        var roofA = Rect(0, 0, 12, 12);       // le batiment occupe
        var blocker = Rect(20, -6, 32, 18);   // pile entre A et la cible
        var state = StateWithBuildings(roofA, blocker);

        var unit = MakeInfantry("Sniper", 1, new Vector2(6, 6));
        unit.zStrata = ZStrata.Toit;
        unit.outputY = 9f;
        unit.movementBudget = 250f; // itineraire teste, pas la limite de distance
        state.units.Add(unit);

        Vector2 street = new Vector2(42, 6);
        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Sniper", new PathCheckpoint { position = street }) },
            new List<UnitOrders>(), null);

        foreach (var e in Moves(events, "Sniper"))
        {
            // Jamais a l'interieur du batiment qui bloque : ni au sol (mur), ni "en vol" par-dessus.
            if (GeometryMath.PointInPolygon(blocker, e.position)) return false;
        }
        return Vector2.Distance(unit.position, street) < 1.5f;
    }

    private static bool TestRooftopUnitEndsOnGroundWhenOrderedToStreet()
    {
        var roofA = Rect(0, 0, 12, 12);
        var state = StateWithBuildings(roofA);

        var unit = MakeInfantry("Sniper", 1, new Vector2(6, 6));
        unit.zStrata = ZStrata.Toit;
        unit.outputY = 9f;
        state.units.Add(unit);

        Vector2 street = new Vector2(30, 6);
        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Sniper", new PathCheckpoint { position = street }) },
            new List<UnitOrders>(), null);

        bool arrived = Vector2.Distance(unit.position, street) < 1.5f;
        bool onGround = unit.zStrata == ZStrata.Sol && (!unit.outputY.HasValue || unit.outputY.Value <= 2.2f);
        return arrived && onGround;
    }

    // ---- 3. approche d'escalade -----------------------------------------------------------

    private static bool TestClimbApproachWalksAroundObstacle()
    {
        var target = Rect(30, 0, 42, 12);     // batiment a escalader
        var blocker = Rect(12, -8, 22, 20);   // pile entre l'unite et lui
        var state = StateWithBuildings(target, blocker);

        var unit = MakeInfantry("Grimpeur", 1, new Vector2(0, 6));
        unit.movementBudget = 250f; // itineraire teste, pas la limite de distance
        state.units.Add(unit);

        Vector2 roofPoint = new Vector2(36, 6); // au milieu du toit vise
        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Grimpeur", new PathCheckpoint { position = roofPoint, setPositionY = 9f }) },
            new List<UnitOrders>(), null);

        foreach (var e in Moves(events, "Grimpeur"))
        {
            if (GeometryMath.PointInPolygon(blocker, e.position)) return false; // aurait traverse le batiment genant
        }
        return true;
    }

    private static bool TestClimbEndsOnRoofStrata()
    {
        var target = Rect(30, 0, 42, 12);
        var state = StateWithBuildings(target);

        var unit = MakeInfantry("Grimpeur", 1, new Vector2(0, 6));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        Vector2 roofPoint = new Vector2(36, 6);
        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Grimpeur", new PathCheckpoint { position = roofPoint, setPositionY = 9f }) },
            new List<UnitOrders>(), null);

        bool onRoof = unit.zStrata == ZStrata.Toit && unit.outputY.HasValue && unit.outputY.Value > 2.2f;
        bool arrived = Vector2.Distance(unit.position, roofPoint) < 1.5f;
        return onRoof && arrived;
    }
}
