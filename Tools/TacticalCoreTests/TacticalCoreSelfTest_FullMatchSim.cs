// 2026-09-13 : TestFullDeathmatchSimulation (jouait plusieurs tours de TacticalResolver.Resolve()
// en chaine) a ete retiree avec TacticalResolver.cs/LineOfSight.cs -- plus aucun appelant en
// production depuis le passage de tous les modes au vrai moteur Unity (voir
// Assets/_ServerDocs/multiplayer/00-current-architecture-2026-09-13.md). Les deux tests restants
// ci-dessous ne touchent jamais TacticalResolver : ils verifient une copie fidele de la regle de
// plafonnement de roster de MatchSessionManager_Deployment.cs (FilterRosterToBudget), toujours
// vivante en production -- voir le commentaire de MirrorFilterRosterToBudget plus bas pour pourquoi
// cette regle est dupliquee ici plutot qu'appelee directement.
using System.Collections.Generic;
using System.Linq;

public static partial class TacticalCoreSelfTest
{
    public static void RunFullMatchSimTests(ref int passed, ref int failed)
    {
        Run("Deploiement : MaxMortarsPerTeam (2026-09-12) coupe exactement au 3e mortier, jamais au hasard", TestMortarCapTrimming, ref passed, ref failed);
        Run("Deploiement : un roster deja dans les 3 plafonds ne perd rien au filtrage", TestValidRosterUntouchedByFilter, ref passed, ref failed);
    }

    // ---- Plafonds de deploiement (mirroir de FilterRosterToBudget) -------------------------
    //
    // MatchSessionManager_Deployment.cs (Assets/Scripts/Server, HORS PERIMETRE du harnais -- il
    // depend de UnitSpawnerUI.UnitType/UnitPlacement, des types reseau/scene que Harness.csproj ne
    // compile pas) n'est donc pas appelable directement ici. Ce qui suit est une copie fidele, terme
    // a terme, de sa regle (memes 3 plafonds, meme ordre de garde `fitsCount/fitsMortarCap/
    // fitsBudget`, meme conservation dans l'ORDRE DE SOUMISSION) -- toute divergence future entre
    // les deux devra se voir a l'execution de CE test, pas seulement a la relecture du code serveur.
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

    /// <summary>Cas complementaire : un roster deja dans les 3 plafonds ne doit RIEN perdre -- le
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
