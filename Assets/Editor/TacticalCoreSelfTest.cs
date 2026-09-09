using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Novgov.TacticalCore;

/// <summary>
/// Auto-test du moteur logique déterministe (Assets/Scripts/TacticalCore/) — ne touche à AUCUNE
/// scène ni composant Unity vivant, purement des structures de données synthétiques. Couvre les
/// règles de l'audit exhaustif du 2026-08-30 (UnitAI_Combat.cs, UnitAI.cs, RoadBarrier.cs,
/// MortarShell.cs, DestructibleEnvironment.cs) — voir TacticalTypes.cs pour les sources exactes.
/// </summary>
public static partial class TacticalCoreSelfTest
{
    [MenuItem("Novgov/Tests/TacticalCore Self-Test")]
    public static void RunAll()
    {
        int passed = 0, failed = 0;

        Run("Ligne de vue bloquée par un mur", TestLineOfSightBlockedByWall, ref passed, ref failed);
        Run("Destruction de bâtiment ouvre la ligne de vue dans la même résolution", TestBuildingDestructionOpensLineOfSight, ref passed, ref failed);
        Run("Pathfinding contourne un bâtiment", TestPathfindingAvoidsBuilding, ref passed, ref failed);
        Run("Overwatch interrompt un déplacement ennemi", TestOverwatchInterrupt, ref passed, ref failed);
        Run("Barricade bloque la ligne de vue tant qu'elle tient", TestBarricadeBlocksSight, ref passed, ref failed);
        Run("Priorité de couverture garnison > barricade > guet (jamais cumulée)", TestCoverPriority, ref passed, ref failed);
        Run("Camouflage : bonus d'embuscade x1.75 puis rupture au tir", TestCamouflageAmbushBonus, ref passed, ref failed);
        Run("Camouflage de la cible rompt dès qu'elle encaisse un dégât", TestCamouflageBreaksOnHit, ref passed, ref failed);
        Run("Cadence de tir : plusieurs coups dans une même résolution", TestWeaponCooldownMultiShot, ref passed, ref failed);
        Run("Cône de fenêtre (garnison) refuse un tir hors du champ de vision", TestWindowConeRestriction, ref passed, ref failed);
        Run("Mortier n'engage jamais directement (dégâts nuls hors zone)", TestMortarNeverEngagesDirectly, ref passed, ref failed);
        Run("Résolution : le déplacement contourne un bâtiment au lieu de couper au travers (\"raccourci\")", TestResolveMovementAvoidsBuilding, ref passed, ref failed);
        Run("Résolution : un checkpoint intermédiaire exécute sa commande immédiatement, pas en fin de chemin", TestCheckpointCommandFiresImmediately, ref passed, ref failed);
        Run("Résolution : un chemin multi-checkpoints visite chaque point dans l'ordre exact", TestMultiCheckpointOrderPreserved, ref passed, ref failed);

        // Tests de traversée de toit et de fenêtre de combat (2026-09-03) — voir
        // TacticalCoreSelfTest_Roof.cs.
        RunExtraTests(ref passed, ref failed);

        // Logique CLIENT pure (sentinelles, etc.) — voir TacticalCoreSelfTest_Client.cs.
        RunClientLogicTests(ref passed, ref failed);

        // Simulation diagnostique avec log tick par tick (retour joueur 2026-09-09, "le fantassin
        // ne suit pas le chemin") — voir TacticalCoreSelfTest_InfantryPathSim.cs.
        RunInfantryPathSimTests(ref passed, ref failed);

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

    private static TacticalWorldState MakeEmptyState()
    {
        var state = new TacticalWorldState();
        state.grid = new TacticalGrid(-50f, -50f, 100, 100);
        return state;
    }

    private static TacticalUnit MakeInfantry(string id, int team, Vector2 pos, int health = 100)
    {
        return new TacticalUnit { id = id, team = team, position = pos, health = health, engagementRange = 15f, spottingRange = 35f, weaponDamage = 15, weaponCooldownSeconds = 0.35f };
    }

    private static bool TestLineOfSightBlockedByWall()
    {
        var state = MakeEmptyState();
        state.wallSegments.Add(new WallSegment { p1 = new Vector2(0, -10), p2 = new Vector2(0, 10), buildingId = 0 });
        state.buildings.Add(new TacticalBuilding { id = 0 });

        bool blocked = !LineOfSight.HasLineOfSight(state, new Vector2(-5, 0), new Vector2(5, 0), VisionType.Normale);
        bool clearBeforeWall = LineOfSight.HasLineOfSight(state, new Vector2(-5, 0), new Vector2(-1, 0), VisionType.Normale);
        return blocked && clearBeforeWall;
    }

    private static bool TestBuildingDestructionOpensLineOfSight()
    {
        var state = MakeEmptyState();
        state.wallSegments.Add(new WallSegment { p1 = new Vector2(0, -10), p2 = new Vector2(0, 10), buildingId = 0 });
        state.buildings.Add(new TacticalBuilding { id = 0, health = 300f, footprint = new List<Vector2> { new Vector2(-1, -10), new Vector2(1, -10), new Vector2(1, 10), new Vector2(-1, 10) } });

        bool blockedBefore = !LineOfSight.HasLineOfSight(state, new Vector2(-5, 0), new Vector2(5, 0), VisionType.Normale);

        var shooter = MakeInfantry("A1", 1, new Vector2(-5, 0));
        var target = MakeInfantry("B1", 2, new Vector2(5, 0));
        state.units.Add(shooter);
        state.units.Add(target);

        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { new UnitOrders { unitId = "A1" } },
            new List<UnitOrders> { new UnitOrders { unitId = "B1" } },
            new List<Vector2> { Vector2.zero }); // frappe de mortier pile sur le mur : Lerp(100,375,~1) > 300 PV, détruit en un coup

        bool buildingDestroyed = state.buildings[0].destroyed;
        bool sightNowOpen = LineOfSight.HasLineOfSight(state, new Vector2(-5, 0), new Vector2(5, 0), VisionType.Normale);
        bool hasDestroyedEvent = events.Exists(e => e.kind == TacticalEvent.Kind.WallDestroyed && e.buildingId == 0);

        return blockedBefore && buildingDestroyed && sightNowOpen && hasDestroyedEvent;
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

    private static bool TestOverwatchInterrupt()
    {
        var state = MakeEmptyState();
        var watcher = MakeInfantry("Watcher", 1, new Vector2(0, 0));
        watcher.watchTrigger = new OverwatchTrigger { origin = new Vector2(0, 0), facing = Vector2.right, cosHalfAngle = 0.5f, range = 30f };
        var mover = MakeInfantry("Mover", 2, new Vector2(20, 0));
        state.units.Add(watcher);
        state.units.Add(mover);

        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders>(),
            new List<UnitOrders> { new UnitOrders { unitId = "Mover", checkpoints = new List<PathCheckpoint> { new PathCheckpoint { position = new Vector2(5, 0) } } } },
            null);

        bool triggered = events.Exists(e => e.kind == TacticalEvent.Kind.OverwatchTriggered && e.unitId == "Watcher");
        bool tookDamage = mover.health < 100;
        return triggered && tookDamage;
    }

    private static bool TestBarricadeBlocksSight()
    {
        var state = MakeEmptyState();
        state.barricades.Add(new Barricade { p1 = new Vector2(0, -3), p2 = new Vector2(0, 3), hp = 250 });

        bool blockedWhileStanding = !LineOfSight.HasLineOfSight(state, new Vector2(-5, 0), new Vector2(5, 0), VisionType.Normale);

        state.barricades[0].hp = 0; // détruite
        bool clearOnceDestroyed = LineOfSight.HasLineOfSight(state, new Vector2(-5, 0), new Vector2(5, 0), VisionType.Normale);

        return blockedWhileStanding && clearOnceDestroyed;
    }

    /// <summary>UnitAI.TakeDamage (§8.3) : garnison (-75%) > barricade à 2.2m (-60%) > guet (-50%),
    /// JAMAIS cumulée — une cible à la fois garnison ET guet doit prendre exactement -75%, pas
    /// -75% puis -50% de plus.</summary>
    private static bool TestCoverPriority()
    {
        var state = MakeEmptyState();
        var shooter = MakeInfantry("Shooter", 1, new Vector2(0, 0));
        shooter.weaponDamage = 100;

        var garrisonedAndGuarding = MakeInfantry("Target", 2, new Vector2(5, 0), health: 1000);
        garrisonedAndGuarding.isGarrisoned = true;
        garrisonedAndGuarding.isGuarding = true;
        state.units.Add(shooter);
        state.units.Add(garrisonedAndGuarding);

        var coverEvents = TacticalResolver.Resolve(state, new List<UnitOrders>(), new List<UnitOrders>(), null); // pas d'ordres : juste le combat continu
        int damageTaken = FirstShotDamage(coverEvents, "Shooter");

        // 100 * 0.25 (garnison) = 25 exactement — si -50% s'appliquait EN PLUS, ce serait 12 ou 13.
        // Mesuré sur le PREMIER tir : la fenêtre de combat de 2.0s en contient plusieurs (voir
        // TacticalResolver, CombatWindowTicks), alors que la règle testée s'applique par tir.
        return damageTaken == 25;
    }

    /// <summary>UnitAI_Combat.cs:411-416 : un tireur camouflé inflige x1.75 dégâts sur son premier
    /// tir puis perd son camouflage (un seul bonus par activation).</summary>
    private static bool TestCamouflageAmbushBonus()
    {
        var state = MakeEmptyState();
        var shooter = MakeInfantry("Ambusher", 1, new Vector2(0, 0));
        shooter.weaponDamage = 20;
        shooter.isCamouflaged = true;
        var target = MakeInfantry("Target", 2, new Vector2(5, 0), health: 1000);
        state.units.Add(shooter);
        state.units.Add(target);

        var camoEvents = TacticalResolver.Resolve(state, new List<UnitOrders>(), new List<UnitOrders>(), null);
        int damageTaken = FirstShotDamage(camoEvents, "Ambusher");

        // 20 * 1.75 = 35 exactement sur le PREMIER tir (le bonus d'embuscade ne vaut qu'une fois),
        // et le camouflage doit être tombé après.
        return damageTaken == 35 && !shooter.isCamouflaged;
    }

    /// <summary>UnitAI.cs:685 : isCamouflaged de la CIBLE tombe dès qu'elle encaisse un dégât,
    /// même si ce n'est pas elle qui vient de tirer.</summary>
    private static bool TestCamouflageBreaksOnHit()
    {
        var state = MakeEmptyState();
        var shooter = MakeInfantry("Shooter", 1, new Vector2(0, 0));
        // La cible est temporairement rendue visible pour ce test unitaire direct (ApplyDamage ne
        // filtre pas le camouflage lui-même, seul FindClosestEngageableTarget le fait) :
        var target = MakeInfantry("Target", 2, new Vector2(5, 0), health: 1000);
        target.isCamouflaged = true;
        state.units.Add(shooter);
        state.units.Add(target);

        // Appel direct pour isoler la règle sans dépendre du ciblage automatique (qui ignore les
        // cibles camouflées, donc ne les toucherait jamais dans Resolve()).
        var events = new List<TacticalEvent>();
        typeof(TacticalResolver).GetMethod("ApplyDamage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Invoke(null, new object[] { state, shooter, target, events, 0 });

        return !target.isCamouflaged;
    }

    /// <summary>UnitAI_Combat.cs:87-91 : une unité tire plusieurs fois si le combat dure assez
    /// longtemps par rapport à son cooldown (0.35s infanterie) — pas un seul coup par résolution.</summary>
    private static bool TestWeaponCooldownMultiShot()
    {
        var state = MakeEmptyState();
        var shooter = MakeInfantry("Shooter", 1, new Vector2(0, 0));
        shooter.weaponDamage = 1;
        var target = MakeInfantry("Target", 2, new Vector2(5, 0), health: 1000);
        state.units.Add(shooter);
        state.units.Add(target);

        var events = TacticalResolver.Resolve(state, new List<UnitOrders>(), new List<UnitOrders>(), null);
        int shotCount = events.FindAll(e => e.kind == TacticalEvent.Kind.Shot).Count;

        return shotCount >= 2; // au moins deux tirs sur la durée de résolution (cooldown 0.35s largement écoulé plusieurs fois)
    }

    /// <summary>UnitAI_Combat.cs:278-281 : une unité en garnison de fenêtre ne peut pas tirer sur
    /// une cible derrière elle (hors du cône de ~140° centré sur la normale de la fenêtre).</summary>
    private static bool TestWindowConeRestriction()
    {
        var state = MakeEmptyState();
        var shooter = MakeInfantry("Sniper", 1, new Vector2(0, 0));
        shooter.isGarrisoned = true;
        shooter.windowNormal = Vector2.right; // regarde vers +X

        var behindTarget = MakeInfantry("Behind", 2, new Vector2(-5, 0)); // dans le dos, hors champ
        bool canEngageBehind = LineOfSight.CanEngageTarget(state, shooter, behindTarget);

        var frontTarget = MakeInfantry("Front", 2, new Vector2(5, 0)); // devant, dans le champ
        bool canEngageFront = LineOfSight.CanEngageTarget(state, shooter, frontTarget);

        return !canEngageBehind && canEngageFront;
    }

    /// <summary>UnitAI_Combat.cs:42-54/248-264 : le mortier n'a pas de tir direct (weaponDamage=0
    /// et weaponCooldownSeconds=0 dans BuildTacticalUnit) — seules les frappes de zone
    /// (mortarStrikes) infligent des dégâts. Vérifié ici en s'assurant qu'un "mortier" placé à
    /// portée d'un ennemi, sans ordre de tir de zone, ne lui inflige jamais de dégâts.</summary>
    private static bool TestMortarNeverEngagesDirectly()
    {
        var state = MakeEmptyState();
        var mortar = MakeInfantry("Mortar", 1, new Vector2(0, 0));
        mortar.isMortar = true;
        mortar.weaponDamage = 0;
        mortar.weaponCooldownSeconds = 0f;
        var target = MakeInfantry("Target", 2, new Vector2(5, 0), health: 100);
        state.units.Add(mortar);
        state.units.Add(target);

        TacticalResolver.Resolve(state, new List<UnitOrders>(), new List<UnitOrders>(), null);
        return target.health == 100;
    }

    /// <summary>2026-09-02, correctif "prend le raccourci" : le mouvement RÉSOLU (pas seulement
    /// Pathfinding.FindPath en isolation, déjà couvert par TestPathfindingAvoidsBuilding) doit
    /// contourner un bâtiment placé directement entre le point de départ et le checkpoint visé,
    /// jamais couper tout droit à travers son empreinte comme une simple ligne droite le ferait.</summary>
    private static bool TestResolveMovementAvoidsBuilding()
    {
        var footprint = new List<Vector2> {
            new Vector2(-5, -5), new Vector2(5, -5), new Vector2(5, 5), new Vector2(-5, 5)
        };
        var state = new TacticalWorldState { grid = new TacticalGrid(-30f, -30f, 60, 60) };
        state.grid.CarveBuildingInteriors(new List<List<Vector2>> { footprint });

        var mover = MakeInfantry("Runner", 1, new Vector2(-10, 0));
        state.units.Add(mover);

        var order = new UnitOrders { unitId = "Runner", checkpoints = new List<PathCheckpoint> { new PathCheckpoint { position = new Vector2(10, 0) } } };
        var events = TacticalResolver.Resolve(state, new List<UnitOrders> { order }, new List<UnitOrders>(), null);

        foreach (var e in events)
        {
            if (e.kind != TacticalEvent.Kind.Move) continue;
            if (GeometryMath.PointInPolygon(footprint, e.position)) return false; // aurait coupé à travers le bâtiment
        }

        bool reachedGoal = Vector2.Distance(mover.position, new Vector2(10, 0)) < 1.5f;
        return reachedGoal;
    }

    /// <summary>2026-09-02, correctif "commandes à chaque checkpoint" : un Guetter posé sur un
    /// checkpoint INTERMÉDIAIRE (pas le dernier de l'ordre) doit armer son Overwatch DÈS L'ARRIVÉE à
    /// ce point, pas seulement une fois le chemin entier terminé. Le checkpoint suivant est
    /// délibérément TRÈS loin : si la commande était encore reportée en fin de chemin (ancien bug),
    /// le tir d'interruption ne pourrait jamais se produire aussi tôt que vérifié ici.</summary>
    private static bool TestCheckpointCommandFiresImmediately()
    {
        var state = MakeEmptyState();
        var watcher = MakeInfantry("Watcher", 1, new Vector2(0, 0));
        var trespasser = MakeInfantry("Trespasser", 2, new Vector2(2, -10));
        state.units.Add(watcher);
        state.units.Add(trespasser);

        var nearCheckpoint = new PathCheckpoint
        {
            position = new Vector2(0, 2),
            setGuarding = true,
            enterOverwatchAtEnd = true,
            overwatchToSet = new OverwatchTrigger { origin = new Vector2(0, 2), facing = new Vector2(1, 0), cosHalfAngle = 0.3f, range = 15f },
        };
        var farCheckpoint = new PathCheckpoint { position = new Vector2(0, 100) };
        var watcherOrders = new UnitOrders { unitId = "Watcher", checkpoints = new List<PathCheckpoint> { nearCheckpoint, farCheckpoint } };
        var trespasserOrders = new UnitOrders { unitId = "Trespasser", checkpoints = new List<PathCheckpoint> { new PathCheckpoint { position = new Vector2(2, 5) } } };

        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { watcherOrders },
            new List<UnitOrders> { trespasserOrders },
            null);

        var triggerEvent = events.Find(e => e.kind == TacticalEvent.Kind.OverwatchTriggered && e.unitId == "Watcher");
        if (triggerEvent == null) return false;

        // Watcher est encore à des dizaines de mètres de son checkpoint lointain (0,100) à cet
        // instant — preuve que la commande du premier checkpoint a été appliquée SUR PLACE, jamais
        // reportée à la fin de l'ordre entier.
        return triggerEvent.tick < 30;
    }

    /// <summary>2026-09-02 : un ordre à plusieurs checkpoints doit tous les visiter, DANS L'ORDRE
    /// EXACT où ils ont été posés — jamais sauter directement au dernier.</summary>
    private static bool TestMultiCheckpointOrderPreserved()
    {
        var state = MakeEmptyState();
        var unit = MakeInfantry("Patrol", 1, new Vector2(0, 0));
        state.units.Add(unit);

        Vector2 cpA = new Vector2(10, 0), cpB = new Vector2(10, 10), cpC = new Vector2(0, 10);
        var order = new UnitOrders
        {
            unitId = "Patrol",
            checkpoints = new List<PathCheckpoint> {
                new PathCheckpoint { position = cpA },
                new PathCheckpoint { position = cpB },
                new PathCheckpoint { position = cpC },
            }
        };

        var events = TacticalResolver.Resolve(state, new List<UnitOrders> { order }, new List<UnitOrders>(), null);

        int tickA = FirstTickWithin(events, "Patrol", cpA, 0.5f);
        int tickB = FirstTickWithin(events, "Patrol", cpB, 0.5f);
        int tickC = FirstTickWithin(events, "Patrol", cpC, 0.5f);

        bool allFound = tickA > 0 && tickB > 0 && tickC > 0;
        bool inOrder = allFound && tickA < tickB && tickB < tickC;
        bool endedAtLast = Vector2.Distance(unit.position, cpC) < 0.5f;

        return allFound && inOrder && endedAtLast;
    }

    /// <summary>Dégâts du PREMIER tir de ce tireur dans la résolution. Les règles de couverture et
    /// d'embuscade se vérifient PAR TIR, alors qu'une résolution contient toute une fenêtre de combat
    /// de 2.0s (plusieurs tirs).</summary>
    private static int FirstShotDamage(List<TacticalEvent> events, string shooterId)
    {
        foreach (var e in events)
        {
            if (e.kind == TacticalEvent.Kind.Shot && e.unitId == shooterId) return e.damage;
        }
        return -1;
    }

    private static int FirstTickWithin(List<TacticalEvent> events, string unitId, Vector2 point, float threshold)
    {
        foreach (var e in events)
        {
            if (e.kind != TacticalEvent.Kind.Move || e.unitId != unitId) continue;
            if (Vector2.Distance(e.position, point) <= threshold) return e.tick;
        }
        return -1;
    }
}
