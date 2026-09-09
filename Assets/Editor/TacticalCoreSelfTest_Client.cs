// Tests de la logique CLIENT pure (2026-09-03, seconde passe) — tout ce qui, cote client, peut etre
// extrait en fonctions sans MonoBehaviour ni scene, et donc verifie automatiquement. Meme partial
// class que TacticalCoreSelfTest.cs, lance par RunAll() via RunClientLogicTests().
//
// Regle de conception : si un bug client est reproductible par du calcul pur, sa logique est
// extraite dans une classe pure (ici Novgov.Core.VectorSentinel) et un test vient l'y epingler.
// Sans cela, ces bugs ne sont detectables qu'en jouant sur un vrai telephone.
using UnityEngine;
using Novgov.Core;

public static partial class TacticalCoreSelfTest
{
    private static void RunClientLogicTests(ref int passed, ref int failed)
    {
        Run("Sentinelle : positiveInfinity n'est PAS une cible renseignee", TestSentinelInfinityIsNotSet, ref passed, ref failed);
        Run("Sentinelle : une vraie position est renseignee, y compris (0,0,0)", TestSentinelRealPositionIsSet, ref passed, ref failed);
        Run("Sentinelle : NaN n'est jamais une cible valide", TestSentinelNaNIsNotSet, ref passed, ref failed);
        Run("Sentinelle : deux sentinelles sont EGALES (le piege de l'operateur ==)", TestSentinelTwoSentinelsAreSame, ref passed, ref failed);
        Run("Sentinelle : sentinelle et position reelle sont differentes", TestSentinelSetVsUnsetDiffer, ref passed, ref failed);
        Run("Sentinelle : deux positions reelles distinctes sont differentes", TestSentinelTwoRealPositionsDiffer, ref passed, ref failed);
        Run("Sentinelle : l'operateur == de Unity ment bien sur l'infini (garde-fou du shim)", TestUnityEqualityIsBrokenOnInfinity, ref passed, ref failed);

        // Non-regression de l'A* — voir TacticalCoreSelfTest_Pathfinding.cs.
        RunPathfindingTests(ref passed, ref failed);

        // Non-regression budget de deplacement vs franchissement vertical — voir
        // TacticalCoreSelfTest_VerticalBudget.cs.
        RunVerticalBudgetTests(ref passed, ref failed);

        // Halte tactique "ATTENDRE 30 SECONDES" cote moteur pur — voir TacticalCoreSelfTest_Wait.cs.
        RunWaitTests(ref passed, ref failed);

        // Hash entier pur (remplace le hash trigonometrique de CityGenerator) — voir
        // TacticalCoreSelfTest_DeterministicHash.cs.
        RunDeterministicHashTests(ref passed, ref failed);

        // Entree/sortie de batiment au sol (2026-09-06, "l'infanterie ne rentre pas dans les
        // batiments") — voir TacticalCoreSelfTest_BuildingEntry.cs.
        RunBuildingEntryTests(ref passed, ref failed);

        // Entree par une VRAIE position de porte, qui est toujours hors de l'empreinte (2026-09-07)
        // — voir TacticalCoreSelfTest_DoorEntry.cs. Les tests d'entree ci-dessus visaient tous un
        // point deja interieur, ce qui laissait passer le fait que l'entree ne se declenchait
        // jamais en jeu.
        RunDoorEntryTests(ref passed, ref failed);

        // Resolve mute ses entrees EN PLACE (2026-09-07) — la propriete qui rendait possible le
        // rejeu amorce sur son propre resultat. Voir TacticalCoreSelfTest_ResolveMutatesInPlace.cs.
        RunResolveMutationTests(ref passed, ref failed);
    }

    /// <summary>LE test de non-regression du bug "jouabilite cassee" : tant que ceci echoue, le
    /// tracé se croit en attente d'un apercu vers l'infini a chaque frame.</summary>
    private static bool TestSentinelInfinityIsNotSet()
    {
        return !VectorSentinel.IsSet(Vector3.positiveInfinity)
               && !VectorSentinel.IsSet(Vector3.negativeInfinity)
               && !VectorSentinel.IsSet(new Vector3(12f, float.PositiveInfinity, -4f));
    }

    private static bool TestSentinelRealPositionIsSet()
    {
        // (0,0,0) DOIT rester une cible valide : le plan "Sol" est centre sur l'origine du monde,
        // un tap au centre exact de la carte y resout litteralement.
        return VectorSentinel.IsSet(Vector3.zero)
               && VectorSentinel.IsSet(new Vector3(-18.5f, 6.2f, 41f));
    }

    private static bool TestSentinelNaNIsNotSet()
    {
        return !VectorSentinel.IsSet(new Vector3(float.NaN, 0f, 0f))
               && !VectorSentinel.IsSet(new Vector3(0f, 0f, float.NaN));
    }

    private static bool TestSentinelTwoSentinelsAreSame()
    {
        return VectorSentinel.Same(Vector3.positiveInfinity, Vector3.positiveInfinity);
    }

    private static bool TestSentinelSetVsUnsetDiffer()
    {
        return !VectorSentinel.Same(Vector3.positiveInfinity, new Vector3(3f, 0f, 7f))
               && !VectorSentinel.Same(new Vector3(3f, 0f, 7f), Vector3.positiveInfinity);
    }

    private static bool TestSentinelTwoRealPositionsDiffer()
    {
        return !VectorSentinel.Same(new Vector3(3f, 0f, 7f), new Vector3(3f, 0f, 9f))
               && VectorSentinel.Same(new Vector3(3f, 0f, 7f), new Vector3(3f, 0f, 7f));
    }

    /// <summary>Garde-fou meta : ce test echouerait si l'operateur == cessait d'avoir la semantique
    /// approximative de Unity — auquel cas les tests ci-dessus ne prouveraient plus rien. Il
    /// documente aussi noir sur blanc le piege a l'origine du bug.</summary>
    private static bool TestUnityEqualityIsBrokenOnInfinity()
    {
        bool equalityLies = (Vector3.positiveInfinity != Vector3.positiveInfinity);
        bool normalEqualityWorks = (new Vector3(1f, 2f, 3f) == new Vector3(1f, 2f, 3f));
        return equalityLies && normalEqualityWorks;
    }
}
