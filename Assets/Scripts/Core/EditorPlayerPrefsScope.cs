namespace Novgov.Core
{
    /// <summary>
    /// PlayerPrefs, sur Windows, vit dans le Registre — une seule case par nom de clé, PARTAGÉE entre
    /// l'Éditeur principal et TOUS ses clones Multiplayer Play Mode. Deux bugs distincts en sont
    /// venus (2026-09-06) : ZoneManager (tuile domicile toujours identique pour tout le monde) et
    /// SupabaseAuthClient (l'Éditeur principal héritait du compte de test que le Joueur Virtuel venait
    /// d'utiliser — pas juste une lecture ratée, une VRAIE pollution en écriture). Un reset ponctuel
    /// pour le Joueur Virtuel réglait la lecture, jamais l'écriture.
    ///
    /// Solution complète : donner à chaque IDENTITÉ (Éditeur principal vs Joueur Virtuel) sa PROPRE
    /// clé de Registre, en ajoutant ce suffixe au nom de clé partout où PlayerPrefs sert à mémoriser
    /// quelque chose de propre à CETTE instance (session de connexion, tuile domicile). Vide pour
    /// l'Éditeur principal et pour un vrai appareil (comportement historique inchangé, aucune
    /// migration de données existantes nécessaire) ; dérivé du dossier du clone pour un Joueur
    /// Virtuel, pour qu'en plus chaque clone garde sa PROPRE case (utile dès qu'on active plusieurs
    /// Joueurs Virtuels à la fois, pas seulement Player 2).
    /// </summary>
    public static class EditorPlayerPrefsScope
    {
        public static string Suffix
        {
            get
            {
#if UNITY_EDITOR
                if (Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor) return "";
                return "_vp" + UnityEngine.Application.dataPath.GetHashCode();
#else
                return "";
#endif
            }
        }
    }
}
