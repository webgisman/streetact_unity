// Test ajoute le 2026-09-07 — EPINGLE LE PIEGE QUI A CASSE LE REJEU DES DEUX MODES PvP.
//
// Le bug : MatchSessionManager_CombatPure capturait `unitsBeforeResolution` AVANT d'appeler
// TacticalResolver.Resolve, puis s'en servait APRES pour amorcer la reconstruction du rejeu
// (BuildSnapshotsFromEventsPure). Comme TacticalUnit est une CLASSE que Resolve mute EN PLACE, cette
// liste ne contenait que des references : apres la resolution elle decrivait la FIN du tour, pas son
// debut. Chaque tour de Deathmatch/Zone de Controle etait donc rejoue a partir de son propre
// resultat — snapshot t=0 deja a la position finale, PV retranches une seconde fois par chaque
// evenement Shot, unite tuee affichee morte des la premiere image.
//
// Le correctif est une copie PAR VALEUR (MatchSessionManager.UnitTurnStart) prise avant Resolve.
// Ce fichier ne peut pas tester ce type-la (le harnais hors-editeur ne compile pas Assets/Scripts/
// Server, trop dependant de la scene vivante) — il epingle donc la PROPRIETE DU MOTEUR qui rend le
// piege possible, pour que personne ne la suppose fausse a nouveau : Resolve MUTE SES ENTREES.
using System.Collections.Generic;
using UnityEngine;
using Novgov.TacticalCore;

public static partial class TacticalCoreSelfTest
{
    private static void RunResolveMutationTests(ref int passed, ref int failed)
    {
        Run("Mutation : Resolve deplace l'unite EN PLACE (toute reference gardee voit la fin du tour)", TestResolveMutatesPositionInPlace, ref passed, ref failed);
        Run("Mutation : Resolve retranche les PV EN PLACE (amorcer un rejeu dessus les compterait deux fois)", TestResolveMutatesHealthInPlace, ref passed, ref failed);
        Run("Mutation : un rejeu amorce sur les references s'ouvre sur la FIN du tour, pas son debut", TestReplaySeededFromReferencesShowsTheFuture, ref passed, ref failed);
    }

    private static bool TestResolveMutatesPositionInPlace()
    {
        var state = MakeEmptyState();
        var unit = MakeInfantry("Fantassin", 1, new Vector2(0, 0));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        Vector2 before = unit.position;
        Vector2 destination = new Vector2(20, 0);

        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Fantassin", new PathCheckpoint { position = destination }) },
            new List<UnitOrders>(), null);

        // `unit` est la MEME reference que celle passee dans state.units : elle porte desormais la
        // position de FIN de tour. C'est exactement ce qui invalidait `unitsBeforeResolution`.
        if (Vector2.Distance(unit.position, before) < 1f) return false;
        return Vector2.Distance(unit.position, destination) < 1.5f;
    }

    private static bool TestResolveMutatesHealthInPlace()
    {
        var state = MakeEmptyState();
        // Deux ennemis a portee l'un de l'autre, sans obstacle : le combat continu doit s'engager.
        var a = MakeInfantry("A", 1, new Vector2(0, 0));
        var b = MakeInfantry("B", 2, new Vector2(6, 0));
        state.units.Add(a);
        state.units.Add(b);

        int healthBefore = b.health;

        // Un ordre trivial pour que la boucle de resolution tourne (elle a besoin d'au moins un
        // mouvement pour demarrer, puis la fenetre de combat prend le relais).
        TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("A", new PathCheckpoint { position = new Vector2(1, 0) }) },
            new List<UnitOrders>(), null);

        // Les PV de B ont baisse SUR L'OBJET LUI-MEME. Un rejeu amorce sur cette valeur puis
        // retranchant a nouveau chaque evenement Shot afficherait des PV faux (voire negatifs).
        return b.health < healthBefore;
    }

    /// <summary>Rejoue le tour a partir des deux amorcages possibles et montre qu'ils DIVERGENT :
    /// c'est la difference exacte entre le bug et son correctif. Reproduit la boucle de
    /// MatchSessionManager.BuildSnapshotsFromEventsPure : un dictionnaire de positions amorce l'etat
    /// de depart, puis chaque evenement Move l'avance tick par tick.</summary>
    private static bool TestReplaySeededFromReferencesShowsTheFuture()
    {
        var state = MakeEmptyState();
        var unit = MakeInfantry("Fantassin", 1, new Vector2(0, 0));
        unit.movementBudget = 250f;
        state.units.Add(unit);

        // Ce que fait le correctif : copie PAR VALEUR avant la resolution (cf. UnitTurnStart).
        var seedFromValueCopy = new Dictionary<string, Vector2> { { unit.id, unit.position } };

        // Ce que faisait le code bugue : garder la LISTE d'origine et la relire apres coup.
        var referencesKeptAcrossResolve = new List<TacticalUnit> { unit };

        var events = TacticalResolver.Resolve(state,
            new List<UnitOrders> { OrderTo("Fantassin", new PathCheckpoint { position = new Vector2(20, 0) }) },
            new List<UnitOrders>(), null);

        var seedFromReferences = new Dictionary<string, Vector2>();
        foreach (var u in referencesKeptAcrossResolve) seedFromReferences[u.id] = u.position;

        // La premiere image du rejeu doit montrer le DEPART du tour...
        if (Vector2.Distance(seedFromValueCopy[unit.id], Vector2.zero) > 0.01f) return false;

        // ...alors que l'amorcage par references montre deja l'ARRIVEE : le rejeu s'ouvrait sur son
        // propre resultat, puis le premier evenement Move ramenait l'unite en arriere.
        if (Vector2.Distance(seedFromReferences[unit.id], Vector2.zero) < 5f) return false;

        // Et les deux amorcages different bel et bien — si un jour Resolve cessait de muter en place,
        // ce test le signalerait au lieu de passer silencieusement.
        if (Vector2.Distance(seedFromValueCopy[unit.id], seedFromReferences[unit.id]) < 5f) return false;

        // Enfin : rejoue depuis le bon amorcage, les evenements Move menent bien a la position finale.
        Vector2 replayed = seedFromValueCopy[unit.id];
        foreach (var e in Moves(events, unit.id)) replayed = e.position;
        return Vector2.Distance(replayed, unit.position) < 0.01f;
    }
}
