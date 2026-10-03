namespace Novgov.Core
{
    /// <summary>
    /// Hash déterministe d'une position 2D vers un flottant dans [0, 1), entièrement en arithmétique
    /// entière (XOR, décalages, multiplications sur 32 bits) : bit-identique sur Android, sur le serveur
    /// Linux et dans l'Éditeur. Sin/Cos/Log ne le sont pas (bibliothèque C native différente) : un écart
    /// d'un bit pouvait changer la hauteur d'un toit entre le client et le serveur.
    /// </summary>
    public static class DeterministicHash
    {
        /// <summary>Hash entier 32 bits d'une paire de coordonnées, arrondies au millimètre avant
        /// hachage (évite qu'un bruit flottant de dernier bit sur x/y — hérité de la projection GPS,
        /// elle-même basée sur des fonctions transcendantes — change le résultat : au millimètre
        /// près, deux plateformes qui calculent "la même" position GPS→monde tombent toujours sur le
        /// même entier arrondi, même si leurs derniers bits flottants diffèrent).</summary>
        private static uint HashInt(int x, int y)
        {
            unchecked
            {
                uint h = 2166136261u; // FNV-1a offset basis
                h = (h ^ (uint)x) * 16777619u;
                h = (h ^ (uint)y) * 16777619u;
                // Diffusion supplémentaire (finalisateur type MurmurHash3) : sans elle, un FNV-1a à
                // seulement 2 mots d'entrée a une distribution encore assez grossière sur les bits
                // de poids faible, ce qui suffit pour ce qu'on en fait (un flottant [0,1) sur un
                // bâtiment) mais autant avoir une meilleure diffusion pour un coût négligeable.
                h ^= h >> 16;
                h *= 0x85ebca6bu;
                h ^= h >> 13;
                h *= 0xc2b2ae35u;
                h ^= h >> 16;
                return h;
            }
        }

        /// <summary>Flottant déterministe dans [0, 1) pour une position 2D — remplace un hash
        /// trigonométrique par un hash entier pur, bit-identique sur toute plateforme.</summary>
        public static float Unit01(float x, float y)
        {
            // Millimètre : largement suffisant pour distinguer deux bâtiments réels (jamais à moins
            // de quelques centimètres l'un de l'autre) tout en absorbant le bruit flottant résiduel.
            int ix = (int)System.Math.Round(x * 1000f);
            int iy = (int)System.Math.Round(y * 1000f);
            uint h = HashInt(ix, iy);
            // 24 bits de mantisse (comme un float) suffisent, division exacte par une puissance de 2.
            return (h >> 8) / 16777216f; // (h >> 8) tient sur 24 bits ; 2^24 = 16777216
        }
    }
}
