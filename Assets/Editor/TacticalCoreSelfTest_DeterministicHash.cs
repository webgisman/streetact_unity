// Tests du hash entier pur remplaçant le hash trigonométrique de CityGenerator (2026-09-05).
// Voir Novgov.Core.DeterministicHash pour le pourquoi (portabilité cross-plateforme des
// bâtiments/hauteurs de toit en multijoueur).
using Novgov.Core;

public static partial class TacticalCoreSelfTest
{
    private static void RunDeterministicHashTests(ref int passed, ref int failed)
    {
        Run("Hash déterministe : même position -> toujours la même valeur", TestHashIsDeterministic, ref passed, ref failed);
        Run("Hash déterministe : toujours dans [0, 1)", TestHashStaysInUnitRange, ref passed, ref failed);
        Run("Hash déterministe : positions différentes -> distribution raisonnable (pas de collision massive)", TestHashHasReasonableSpread, ref passed, ref failed);
        Run("Hash déterministe : insensible à un bruit flottant sous le millimètre", TestHashAbsorbsSubMillimeterNoise, ref passed, ref failed);
    }

    private static bool TestHashIsDeterministic()
    {
        for (int i = 0; i < 200; i++)
        {
            float x = i * 3.7f - 100f, y = i * -2.3f + 50f;
            float a = DeterministicHash.Unit01(x, y);
            float b = DeterministicHash.Unit01(x, y);
            if (a != b) return false;
        }
        return true;
    }

    private static bool TestHashStaysInUnitRange()
    {
        for (int i = -500; i < 500; i += 7)
        {
            float v = DeterministicHash.Unit01(i * 1.234f, -i * 5.678f);
            if (v < 0f || v >= 1f) return false;
        }
        return true;
    }

    private static bool TestHashHasReasonableSpread()
    {
        // 1000 positions distinctes réparties sur une grille -> au moins ~900 valeurs de hash
        // distinctes attendues (une bonne diffusion ne devrait quasiment jamais faire collision
        // sur un échantillon de cette taille).
        var seen = new System.Collections.Generic.HashSet<float>();
        for (int i = 0; i < 1000; i++)
        {
            float x = (i % 32) * 4.1f;
            float y = (i / 32) * 4.1f;
            seen.Add(DeterministicHash.Unit01(x, y));
        }
        return seen.Count > 900;
    }

    private static bool TestHashAbsorbsSubMillimeterNoise()
    {
        // Un bruit flottant de dernier bit (hérité d'une projection GPS calculée légèrement
        // différemment selon la plateforme) reste TOUJOURS sous 1e-4 m — bien en dessous du
        // millimètre d'arrondi. Deux positions "presque identiques" doivent donc produire EXACTEMENT
        // le même hash, contrairement à Mathf.Sin(x) * grand_facteur, qui amplifie ce bruit.
        float baseX = 123.456789f, baseY = -987.654321f;
        float noisyX = baseX + 0.00003f; // 0.03mm, très en dessous du seuil d'arrondi (1mm)
        float noisyY = baseY - 0.00004f;
        return DeterministicHash.Unit01(baseX, baseY) == DeterministicHash.Unit01(noisyX, noisyY);
    }
}
