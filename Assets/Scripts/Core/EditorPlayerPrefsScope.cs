namespace Novgov.Core
{
    /// <summary>
    /// Suffixe de clé PlayerPrefs propre à chaque instance de l'Éditeur : sur Windows, PlayerPrefs est une
    /// case du Registre partagée entre l'Éditeur principal et ses Joueurs Virtuels (Multiplayer Play
    /// Mode), qui s'écrasaient leur session et leur quartier. Vide pour l'Éditeur principal et sur un vrai
    /// appareil ; dérivé du dossier du clone pour un Joueur Virtuel.
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
