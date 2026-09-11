using System;

namespace NovgovHarness
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== Novgov TacticalCore — auto-tests hors Editeur ===");
            try
            {
                TacticalCoreSelfTest.RunAll();

                // 2026-09-12 : simulation de partie Deathmatch complete (voir
                // TacticalCoreSelfTest_FullMatchSim.cs, dans ce meme dossier). Ce fichier de test ne
                // peut pas etre relie a RunAll() ci-dessus (qui vit dans Assets/Editor/
                // TacticalCoreSelfTest.cs, hors du perimetre autorise pour cette session) : il est
                // donc invoque directement depuis ce point d'entree. Les deux alimentent le meme
                // compteur d'erreurs (UnityEngine.Debug.Errors, voir UnityShim.cs) via le meme
                // helper Run(...ref passed, ref failed), donc le code de sortie ci-dessous couvre
                // deja les deux sans rien dupliquer.
                int fullMatchPassed = 0, fullMatchFailed = 0;
                TacticalCoreSelfTest.RunFullMatchSimTests(ref fullMatchPassed, ref fullMatchFailed);
                Console.WriteLine(fullMatchFailed == 0
                    ? $"[TacticalCoreSelfTest.FullMatchSim] {fullMatchPassed} reussi(s), {fullMatchFailed} echoue(s)."
                    : $"[TacticalCoreSelfTest.FullMatchSim] {fullMatchPassed} reussi(s), {fullMatchFailed} ECHOUE(S).");
            }
            catch (Exception e)
            {
                Console.WriteLine("EXCEPTION NON RATTRAPEE DANS RunAll : " + e);
                return 2;
            }

            int errors = UnityEngine.Debug.Errors.Count;
            Console.WriteLine(errors == 0
                ? "RESULTAT : tous les tests passent."
                : $"RESULTAT : {errors} ligne(s) d'erreur — ECHEC.");
            return errors == 0 ? 0 : 1;
        }
    }
}
