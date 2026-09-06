namespace Novgov.Core
{
    /// <summary>
    /// Hash déterministe d'une position 2D vers un flottant dans [0, 1) — ENTIÈREMENT en
    /// arithmétique ENTIÈRE (aucune fonction transcendante : pas de sin/cos/tan/log/exp).
    ///
    /// POURQUOI CE FICHIER EXISTE (2026-09-05). CityGenerator.DeterministicLotHeight/
    /// JitterBuildingColor utilisaient `Mathf.Sin(x * a + y * b) * grandFacteur` puis `Mathf.Floor` —
    /// un "hash trigonométrique" volontairement introduit pour remplacer un ancien
    /// `UnityEngine.Random.Range` non seedé (qui faisait flotter une unité perchée jusqu'à ~3m
    /// au-dessus du toit d'un appareil à l'autre). Ce remplacement élimine la dépendance à un flux
    /// aléatoire global, MAIS `sin()`/`cos()`/`tan()`/`log()`/`exp()` ne sont PAS spécifiées
    /// bit-à-bit par IEEE754 : leur implémentation dépend de la bibliothèque C native du runtime —
    /// Bionic sur Android/ARM, glibc sur le serveur Linux dédié, CRT sur l'Editor Windows. Un écart
    /// d'un seul bit de précision (ULP) sur `Sin(x)`, une fois multiplié par le grand facteur
    /// (24634.6345) du code d'origine, peut faire basculer `Floor()` d'une unité entière si la
    /// vraie valeur tombe près d'une frontière — un cas rare mais pas exclu, qui ferait diverger la
    /// hauteur d'un toit (donc la position Y d'un tireur perché) entre le client et le serveur pour
    /// le MÊME bâtiment. Voir aussi Novgov.TacticalCore : ce projet a déjà choisi, pour le moteur de
    /// résolution de combat, de n'utiliser QUE +,-,*,/,sqrt — jamais de trigonométrie — pour cette
    /// même raison (voir TacticalCore.GeometryMath, cône de vue par produit scalaire plutôt que par
    /// angle). Ce fichier applique le même principe à la génération de ville.
    ///
    /// L'algorithme est un hash entier classique (mélange type MurmurHash/FNV, opérations XOR/shift/
    /// multiplication sur des `int`/`uint` de 32 bits) : toutes ces opérations SONT garanties
    /// bit-identiques sur toute plateforme .NET/Mono/IL2CPP respectant IEEE754/l'arithmétique entière
    /// standard — contrairement aux fonctions transcendantes.
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
