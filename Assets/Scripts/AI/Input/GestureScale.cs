using UnityEngine;

namespace Novgov.Gestures
{
    /// <summary>Facteur d'échelle DPI partagé par tous les gestes tactiles (tap, sélection, pincer/pivoter
    /// la caméra) : référence ~160 DPI (« mdpi »), contre laquelle les seuils ont été réglés. Repli sur 1
    /// si Screen.dpi est inconnu.</summary>
    public static class GestureScale
    {
        public static float TouchDpiScale => (Screen.dpi > 0f) ? (Screen.dpi / 160f) : 1f;
    }
}
