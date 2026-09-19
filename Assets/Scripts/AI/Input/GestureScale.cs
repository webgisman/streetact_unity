using UnityEngine;

namespace Novgov.Gestures
{
    /// <summary>Facteur d'échelle DPI partagé par tout geste tactile du jeu — tap/sélection
    /// (TacticalPathManager_Input, TapGestureDetector) ET pincer/pivoter caméra (TacticalCamera).
    ///
    /// Extrait le 2026-09-19 : la même formule (référence ~160 DPI, densité "mdpi" Android
    /// historique contre laquelle ces seuils ont été réglés à l'œil) vivait dupliquée à l'identique
    /// dans les deux fichiers. Une duplication de formule comme celle-ci peut diverger en silence :
    /// un des deux endroits a été mis à l'échelle DPI le 2026-09-13, l'autre l'était déjà — mais nature
    /// même de la duplication, rien ne garantissait que ça reste vrai indéfiniment. Un seul point de
    /// vérité pour toute échelle de geste tactile du jeu.
    ///
    /// Repli sur 1 si Screen.dpi n'est pas rapporté (0, certains émulateurs/environnements) plutôt
    /// que d'inventer une valeur.</summary>
    public static class GestureScale
    {
        public static float TouchDpiScale => (Screen.dpi > 0f) ? (Screen.dpi / 160f) : 1f;
    }
}
