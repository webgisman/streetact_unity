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
