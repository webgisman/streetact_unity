using UnityEngine;

namespace Novgov.Core
{
    /// <summary>
    /// Lecture correcte d'une position « sentinelle » (Vector3.positiveInfinity = aucune valeur).
    /// L'opérateur == de Unity compare (a-b).sqrMagnitude à un epsilon ; or inf - inf = NaN, donc
    /// `Vector3.positiveInfinity == Vector3.positiveInfinity` est FAUX et `!=` toujours VRAI.
    /// Classe pure, couverte par les tests automatiques.
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
