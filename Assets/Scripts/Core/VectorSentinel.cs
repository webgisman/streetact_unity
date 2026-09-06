using UnityEngine;

namespace Novgov.Core
{
    /// <summary>
    /// Lecture correcte d'une position "sentinelle" (Vector3.positiveInfinity = « aucune valeur »).
    ///
    /// POURQUOI CE FICHIER EXISTE. L'opérateur == de Unity sur Vector2/Vector3 ne compare PAS les
    /// composantes : il calcule (a-b).sqrMagnitude et le compare à un epsilon (~1e-10). Or
    /// inf - inf = NaN, et toute comparaison impliquant NaN est fausse. Donc :
    ///
    ///     Vector3.positiveInfinity == Vector3.positiveInfinity   ->  FALSE
    ///     Vector3.positiveInfinity != Vector3.positiveInfinity   ->  TRUE
    ///
    /// Tout code écrit « if (maPosition != Vector3.positiveInfinity) » croit donc tester « une
    /// valeur a été renseignée » alors qu'il teste une expression TOUJOURS VRAIE. C'est exactement
    /// ce qui s'est produit dans TacticalPathManager : le tracé se croyait en permanence en attente
    /// d'un aperçu vers un point à l'infini, redessinait tous les chemins de toutes les unités à
    /// chaque frame (A*/NavMesh en continu sur mobile) et poussait un point infini dans le
    /// LineRenderer de l'unité sélectionnée.
    ///
    /// Classe volontairement PURE (aucun MonoBehaviour, aucun accès de scène) : elle est compilée
    /// telle quelle par le harnais de test hors-éditeur, et couverte par des tests automatiques.
    /// </summary>
    public static class VectorSentinel
    {
        /// <summary>Cette position porte-t-elle une valeur réelle (par opposition à la sentinelle
        /// « aucune valeur ») ? Rejette aussi les NaN, qui se propagent silencieusement dans les
        /// LineRenderer et les bornes de rendu.</summary>
        public static bool IsSet(Vector3 p)
        {
            return !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z)
                   && !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z);
        }

        /// <summary>Deux positions désignent-elles le même point ? Traite correctement le cas
        /// « les deux sont la sentinelle », que l'opérateur == rapporte à tort comme différent.
        /// Deux positions renseignées sont comparées avec la tolérance habituelle de Unity.</summary>
        public static bool Same(Vector3 a, Vector3 b)
        {
            bool aSet = IsSet(a), bSet = IsSet(b);
            if (!aSet || !bSet) return aSet == bSet;
            return a == b;
        }
    }
}
