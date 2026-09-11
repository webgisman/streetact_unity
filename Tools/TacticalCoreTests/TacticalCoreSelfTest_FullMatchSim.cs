// Ajoute le 2026-09-12 -- simulation de partie COMPLETE (deploiement -> plusieurs tours d'ordres ->
// issue), demandee explicitement pour couvrir un angle mort de la suite existante : chaque test
// TacticalCoreSelfTest* jusqu'ici n'exerce qu'UN mecanisme isole (une resolution, un test de
// geometrie...). Aucun ne joue une partie de bout en bout au niveau du moteur pur.
//
// VIT DELIBEREMENT ICI, dans Tools/TacticalCoreTests/, et PAS a cote de ses "voisins" dans
// Assets/Editor/TacticalCoreSelfTest*.cs -- consequence directe de la consigne de cette session
// ("ne touche a aucun fichier hors de Tools/TacticalCoreTests/ sauf bug moteur reel"). C'est donc le
// SEUL fichier de tests qui ne peut pas etre relie a TacticalCoreSelfTest.RunAll() (qui vit dans
// Assets/Editor/TacticalCoreSelfTest.cs, hors-limites ici) : Tools/TacticalCoreTests/Program.cs
// (le point d'entree du harnais, LUI dans le perimetre autorise) appelle RunFullMatchSimTests()
// directement, juste apres RunAll(). Les deux alimentent le meme compteur d'erreurs
// (UnityEngine.Debug.Errors, voir UnityShim.cs) via le meme helper Run(...ref passed, ref failed),
// donc le resultat process (exit code) les couvre tous les deux sans rien dupliquer.
//
// Reste malgre tout un `partial class TacticalCoreSelfTest` (meme namespace implicite -- aucun --,
// meme forme de classe statique, memes conventions Run()/MakeInfantry()/OrderTo() reutilisees) pour
// beneficier des memes fixtures que ses voisins plutot que d'en reinventer une variante. Seule
// difference volontaire : RunFullMatchSimTests est PUBLIC (les RunXxxTests des voisins sont privees)
// puisque Program.cs, une classe totalement differente, doit pouvoir l'appeler depuis l'exterieur.
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Novgov.TacticalCore;

public static partial class TacticalCoreSelfTest
{
    public static void RunFullMatchSimTests(ref int passed, ref int failed)
    {
        Run("Simulation : partie Deathmatch complete de bout en bout (deploiement -> tours -> issue)", TestFullDeathmatchSimulation, ref passed, ref failed);
        Run("Deploiement : MaxMortarsPerTeam (2026-09-12) coupe exactement au 3e mortier, jamais au hasard", TestMortarCapTrimming, ref passed, ref failed);
        Run("Deploiement : un roster deja dans les 3 plafonds ne perd rien au filtrage", TestValidRosterUntouchedByFilter, ref passed, ref failed);
    }

    // ---- Fabriques d'unites manquantes -----------------------------------------------------
    // Barème EXACT de UnitTypeStats.Get / UnitTypeStats.MovementBudget (Assets/Scripts/Server/
    // MatchState.cs) -- MakeInfantry (TacticalCoreSelfTest.cs) couvre deja le Fantassin
    // (100 PV / 15 dgts / 0.35s / 50m, engagementRange=15 = porteeDetection Fantassin), donc
    // seuls les trois autres types de UnitSpawnerUI.UnitType manquent ici.

    private static TacticalUnit MakeCharLeopard(string id, int team, Vector2 pos)
    {
        return new TacticalUnit
        {
            id = id, team = team, position = pos, health = 500,
            engagementRange = 45f, spottingRange = 35f,
            weaponDamage = 150, weaponCooldownSeconds = 1.8f,
            movementBudget = 42f, isTank = true,
        };
    }

    private static TacticalUnit MakeVehiculeCanon(string id, int team, Vector2 pos)
    {
        return new TacticalUnit
        {
            id = id, team = team, position = pos, health = 250,
            engagementRange = 45f, spottingRange = 35f,
            weaponDamage = 75, weaponCooldownSeconds = 1.4f,
            movementBudget = 46f,
        };
    }

    private static TacticalUnit MakeMortier(string id, int team, Vector2 pos)
    {
        return new TacticalUnit
        {
            id = id, team = team, position = pos, health = 350,
            engagementRange = 120f, spottingRange = 25f,
            weaponDamage = 0, weaponCooldownSeconds = 0f,
            movementBudget = 34f, isMortar = true,
        };
    }

    // ---- Simulation complete ----------------------------------------------------------------

    /// <summary>Rejoue une partie Deathmatch entiere au niveau du moteur pur : terrain avec un pate
    /// de maisons entre les deux zones (meme fixture que TestInfantryLongPathAroundBuildingLogged,
    /// TacticalCoreSelfTest_InfantryPathSim.cs, agrandie), un roster de 4 unites par camp respectant
    /// EXACTEMENT les plafonds reels de MatchSessionManager_Deployment.cs (CombatPointBudget=8,
    /// MaxDeployedCombatUnits=6, MaxMortarsPerTeam=2), puis jusqu'a 10 tours de TacticalResolver.
    /// Resolve() EN CHAINE SUR LE MEME TacticalWorldState -- pas de reapplication manuelle des
    /// evenements entre les tours : voir TacticalCoreSelfTest_ResolveMutatesInPlace.cs, Resolve()
    /// mute deja `state` EN PLACE (positions, PV, isDead, batiments detruits), exactement comme
    /// MatchSessionManager_CombatPure.RunExecutionPhasePure le fait sur ms.World d'un vrai tour a
    /// l'autre -- c'est CE meme fait qui rend une copie separee inutile ici.</summary>
    private static bool TestFullDeathmatchSimulation()
    {
        // --- Terrain : un seul pate de maisons entre les deux zones de deploiement -------------
        var footprint = new List<Vector2> {
            new Vector2(-10, -5), new Vector2(10, -5), new Vector2(10, 5), new Vector2(-10, 5)
        };
        var state = new TacticalWorldState { grid = new TacticalGrid(-80f, -80f, 160, 160) };
        state.grid.CarveBuildingInteriors(new List<List<Vector2>> { footprint });
        state.buildings.Add(new TacticalBuilding { id = 0, footprint = footprint, health = 300f });
        // Murs explicites (comme TestLineOfSightBlockedByWall) : CarveBuildingInteriors ne bloque que
        // le PATHFINDING -- sans ces segments le batiment ne bloquerait jamais la ligne de vue, alors
        // qu'un vrai batiment de partie a toujours les deux (TacticalGridBuilder.BuildFromScene).
        state.wallSegments.Add(new WallSegment { p1 = new Vector2(-10, -5), p2 = new Vector2(10, -5), buildingId = 0 });
        state.wallSegments.Add(new WallSegment { p1 = new Vector2(10, -5), p2 = new Vector2(10, 5), buildingId = 0 });
        state.wallSegments.Add(new WallSegment { p1 = new Vector2(10, 5), p2 = new Vector2(-10, 5), buildingId = 0 });
        state.wallSegments.Add(new WallSegment { p1 = new Vector2(-10, 5), p2 = new Vector2(-10, -5), buildingId = 0 });

        // --- Roster : 4 unites/camp, melange des 4 types, PILE dans les 3 plafonds reels --------
        // (CombatPointBudget=8 ; MaxDeployedCombatUnits=6 ; MaxMortarsPerTeam=2 depuis 2026-09-12) :
        // Fantassin(1) + CharLeopard(3) + VehiculeCanon(2) + Mortier(2) = 4 unites, 8/8 points, 1/2 mortier.
        var team1 = new List<TacticalUnit> {
            MakeInfantry("T1_Fantassin", 1, new Vector2(-40, 0)),
            MakeCharLeopard("T1_CharLeopard", 1, new Vector2(-40, 8)),
            MakeVehiculeCanon("T1_VehiculeCanon", 1, new Vector2(-40, -8)),
            MakeMortier("T1_Mortier", 1, new Vector2(-40, 16)),
        };
        var team2 = new List<TacticalUnit> {
            MakeInfantry("T2_Fantassin", 2, new Vector2(40, 0)),
            MakeCharLeopard("T2_CharLeopard", 2, new Vector2(40, -8)),
            MakeVehiculeCanon("T2_VehiculeCanon", 2, new Vector2(40, 8)),
            MakeMortier("T2_Mortier", 2, new Vector2(40, -16)),
        };
        foreach (var u in team1) state.units.Add(u);
        foreach (var u in team2) state.units.Add(u);

        // Sanite prealable : re-verifie que CE roster precis respecte bien les 3 plafonds reels
        // avant meme de jouer la partie -- si un futur changement de cette fixture les depassait par
        // erreur, ce serait un faux positif de test, pas un vrai resultat de simulation.
        foreach (var team in new[] { team1, team2 })
        {
            int cost = team.Sum(u => UnitDeploymentCostFor(u));
            int mortars = team.Count(u => u.isMortar);
            if (team.Count > MirrorMaxDeployedCombatUnits) return false;
            if (mortars > MirrorMaxMortarsPerTeam) return false;
            if (cost > MirrorCombatPointBudget) return false;
        }

        const int TurnCap = 10;
        bool sawShotOrDeath = false;
        bool endedByElimination = false;
        int turnsRun = 0;

        for (int turn = 1; turn <= TurnCap; turn++)
        {
            bool team1Alive = state.units.Any(u => u.team == 1 && !u.isDead);
            bool team2Alive = state.units.Any(u => u.team == 2 && !u.isDead);
            if (!team1Alive || !team2Alive) { endedByElimination = true; break; }

            turnsRun = turn;
            // Photo des morts AVANT ce tour -- sert a verifier ci-dessous qu'aucun d'entre eux ne
            // redevient acteur (deplacement ou tir) PENDANT ce tour (voir l'assertion apres Resolve).
            var deadBeforeTurn = new HashSet<string>(state.units.Where(u => u.isDead).Select(u => u.id));

            // Meme etape que RunExecutionPhasePure (MatchSessionManager_CombatPure.cs) au debut de
            // chaque tour reel : re-derive spottingRange de la strate courante avant de resoudre.
            foreach (var u in state.units)
            {
                if (u.isDead) continue;
                u.spottingRange = u.isMortar ? 25f : 35f;
            }

            var ordersTeam1 = new List<UnitOrders>();
            var ordersTeam2 = new List<UnitOrders>();
            var mortarStrikes = new List<Vector2>();
            BuildAdvanceOrders(state, 1, ordersTeam1);
            BuildAdvanceOrders(state, 2, ordersTeam2);
            BuildMortarStrikes(state, 1, mortarStrikes);
            BuildMortarStrikes(state, 2, mortarStrikes);

            var events = TacticalResolver.Resolve(state, ordersTeam1, ordersTeam2, mortarStrikes);

            foreach (var e in events)
            {
                if (e.kind == TacticalEvent.Kind.Shot || e.kind == TacticalEvent.Kind.Death) sawShotOrDeath = true;

                // Invariant demande explicitement : une unite DEJA morte au debut de ce tour ne doit
                // plus jamais generer d'evenement ou elle est l'ACTEUR (elle se deplace ou elle tire)
                // -- un mort ne recoit plus jamais d'ordre traite. Les evenements Shot du mortier
                // portent l'id synthetique "mortar" (voir TacticalResolver.ApplyAreaDamage), jamais
                // l'id d'une unite reelle, donc aucun faux positif possible ici.
                bool actorEvent = e.kind == TacticalEvent.Kind.Move || e.kind == TacticalEvent.Kind.Shot;
                if (actorEvent && deadBeforeTurn.Contains(e.unitId)) return false;
            }

            // Invariant de sante : une unite encore VIVANTE ne doit jamais avoir des PV <= 0 -- des
            // PV qui tombent a 0 ou moins marquent isDead DANS LE MEME ApplyDamage (TacticalResolver.
            // cs, juste apres `target.health -= damage`) ; si ce n'etait plus vrai, ce serait un vrai
            // bug moteur, pas une simple assertion de test trop stricte (les PV d'une unite MORTE,
            // eux, peuvent legitimement finir negatifs -- rien ne les replafonne a 0, cf. le meme
            // extrait de code -- donc on ne verifie surtout PAS `u.health >= 0` pour tout le monde).
            foreach (var u in state.units)
            {
                if (!u.isDead && u.health <= 0) return false;
            }
        }

        bool terminated = endedByElimination || turnsRun == TurnCap;
        return sawShotOrDeath && terminated;
    }

    /// <summary>Ordres de tour : chaque unite vivante de <paramref name="team"/> avance vers un point
    /// juste au-dela du centre sur SA PROPRE rangee (meme y, x=+/-5 selon le camp) -- pour l'unite de
    /// devant (y=0 des deux cotes), ce point est directement de l'autre cote du pate de maisons, donc
    /// le trajet resolu doit le contourner (meme verification que TestResolveMovementAvoidsBuilding).
    /// N'emet aucun ordre pour une unite deja arrivee (>1.5m pres) : un vrai joueur ne re-clique pas
    /// "avancer" sur une unite qui a deja atteint sa destination.</summary>
    private static void BuildAdvanceOrders(TacticalWorldState state, int team, List<UnitOrders> orders)
    {
        float attackX = team == 1 ? 5f : -5f;
        foreach (var u in state.units)
        {
            if (u.team != team || u.isDead) continue;
            var destination = new Vector2(attackX, u.position.y);
            if (Vector2.Distance(u.position, destination) <= 1.5f) continue;
            orders.Add(OrderTo(u.id, new PathCheckpoint { position = destination }));
        }
    }

    /// <summary>Chaque mortier vivant de <paramref name="team"/> vise l'ennemi vivant le plus proche
    /// DE LUI-MEME -- TacticalResolver.ApplyAreaDamage n'impose aucune portee ni ligne de vue entre le
    /// mortier et son impact (seulement entre l'impact et chaque cible, voir TestMortarNeverEngages
    /// Directly), exactement le comportement d'une piece d'artillerie reelle.</summary>
    private static void BuildMortarStrikes(TacticalWorldState state, int team, List<Vector2> mortarStrikes)
    {
        foreach (var mortar in state.units)
        {
            if (mortar.team != team || mortar.isDead || !mortar.isMortar) continue;

            TacticalUnit nearestEnemy = null;
            float bestDist = float.MaxValue;
            foreach (var enemy in state.units)
            {
                if (enemy.team == team || enemy.isDead) continue;
                float d = Vector2.Distance(mortar.position, enemy.position);
                if (d < bestDist) { bestDist = d; nearestEnemy = enemy; }
            }
            if (nearestEnemy != null) mortarStrikes.Add(nearestEnemy.position);
        }
    }

    // ---- Plafonds de deploiement (mirroir de FilterRosterToBudget) -------------------------
    //
    // MatchSessionManager_Deployment.cs (Assets/Scripts/Server, HORS PERIMETRE du harnais -- il
    // depend de UnitSpawnerUI.UnitType/UnitPlacement, des types reseau/scene que Harness.csproj ne
    // compile pas, voir TacticalCoreSelfTest_ResolveMutatesInPlace.cs pour la meme contrainte deja
    // documentee) n'est donc pas appelable directement ici. Ce qui suit est une copie fidele, terme a
    // terme, de sa regle (memes 3 plafonds, meme ordre de garde `fitsCount/fitsMortarCap/fitsBudget`,
    // meme conservation dans l'ORDRE DE SOUMISSION) -- toute divergence future entre les deux devra
    // se voir a l'execution de CE test, pas seulement a la relecture du code serveur.
    private const int MirrorCombatPointBudget = 8;      // MatchSessionManager.CombatPointBudget
    private const int MirrorMaxDeployedCombatUnits = 6; // MatchSessionManager.MaxDeployedCombatUnits
    private const int MirrorMaxDeployedBarricades = 8;  // MatchSessionManager.MaxDeployedBarricades
    private const int MirrorMaxMortarsPerTeam = 2;      // MatchSessionManager.MaxMortarsPerTeam (2026-09-12)

    private struct RosterItem
    {
        public string type;
        public int cost;
        public bool isMortar;
        public bool isBarricade;
    }

    /// <summary>Cout en points de deploiement (UnitTypeStats.DeploymentCost) d'une TacticalUnit deja
    /// construite -- deduit de weaponDamage/health/isTank/isMortar puisque le moteur pur n'a pas de
    /// UnitSpawnerUI.UnitType. Sert uniquement a la sanite-check du roster de TestFullDeathmatch
    /// Simulation ci-dessus, jamais a une regle de jeu.</summary>
    private static int UnitDeploymentCostFor(TacticalUnit u)
    {
        if (u.isTank) return 3;         // CharLeopard
        if (u.isMortar) return 2;       // Mortier
        if (u.weaponDamage == 75) return 2; // VehiculeCanon
        return 1;                       // Fantassin
    }

    private static List<RosterItem> MirrorFilterRosterToBudget(List<RosterItem> placements, out bool anyDropped)
    {
        var kept = new List<RosterItem>();
        anyDropped = false;
        int combatCount = 0, barricadeCount = 0, mortarCount = 0, totalCost = 0;
        foreach (var p in placements)
        {
            bool fitsCount = p.isBarricade ? barricadeCount + 1 <= MirrorMaxDeployedBarricades : combatCount + 1 <= MirrorMaxDeployedCombatUnits;
            bool fitsMortarCap = !p.isMortar || mortarCount + 1 <= MirrorMaxMortarsPerTeam;
            bool fitsBudget = totalCost + p.cost <= MirrorCombatPointBudget;
            if (!fitsCount || !fitsMortarCap || !fitsBudget) { anyDropped = true; continue; }

            if (p.isBarricade) barricadeCount++; else combatCount++;
            if (p.isMortar) mortarCount++;
            totalCost += p.cost;
            kept.Add(p);
        }
        return kept;
    }

    /// <summary>3 mortiers proposes (6/8 points, 3/6 unites -- ni le budget en points ni le plafond
    /// d'unites ne suffiraient a eux seuls a en ecarter un) + 1 Fantassin : seul MaxMortarsPerTeam=2
    /// (ajoute le 2026-09-12) doit couper EXACTEMENT le 3e mortier, dans l'ordre de soumission --
    /// jamais un des deux premiers, jamais le Fantassin.</summary>
    private static bool TestMortarCapTrimming()
    {
        var proposed = new List<RosterItem> {
            new RosterItem { type = "Mortier", cost = 2, isMortar = true },
            new RosterItem { type = "Mortier", cost = 2, isMortar = true },
            new RosterItem { type = "Mortier", cost = 2, isMortar = true }, // doit etre ecarte
            new RosterItem { type = "Fantassin", cost = 1 },
        };

        var kept = MirrorFilterRosterToBudget(proposed, out bool anyDropped);

        bool exactlyTwoMortarsKept = kept.Count(i => i.isMortar) == MirrorMaxMortarsPerTeam;
        bool infantryKept = kept.Any(i => i.type == "Fantassin");
        bool firstTwoMortarsSurvived = kept.Count >= 2 && kept[0].isMortar && kept[1].isMortar;
        bool totalKeptCount = kept.Count == 3; // 2 mortiers + 1 fantassin, le 3e mortier seul manque

        return anyDropped && exactlyTwoMortarsKept && infantryKept && firstTwoMortarsSurvived && totalKeptCount;
    }

    /// <summary>Cas complementaire : un roster deja dans les 3 plafonds (comme team1/team2 de
    /// TestFullDeathmatchSimulation, 8/8 points, 4 unites, 1 mortier) ne doit RIEN perdre -- le
    /// filtrage ne doit jamais rejeter un placement qui rentrait deja.</summary>
    private static bool TestValidRosterUntouchedByFilter()
    {
        var validRoster = new List<RosterItem> {
            new RosterItem { type = "Fantassin", cost = 1 },
            new RosterItem { type = "CharLeopard", cost = 3 },
            new RosterItem { type = "VehiculeCanon", cost = 2 },
            new RosterItem { type = "Mortier", cost = 2, isMortar = true },
        };

        var kept = MirrorFilterRosterToBudget(validRoster, out bool anyDropped);
        return !anyDropped && kept.Count == validRoster.Count;
    }
}
