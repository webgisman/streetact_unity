using UnityEngine;

namespace Novgov.Gestures
{
    /// <summary>Distingue un TAP (appui puis relâchement au même endroit) d'un GLISSEMENT, à partir
    /// d'un flux de <see cref="PointerFrame"/> lus frame par frame.
    ///
    /// Extrait de TacticalPathManager_Input.HandlePointerInput (2026-09-19) pour isoler cette
    /// mécanique dans sa propre petite classe à état, au lieu de la laisser noyée au milieu du
    /// routage tap→action (sélection, porte, fenêtre, bâtiment...) dans une méthode de 500 lignes.</summary>
    public class TapGestureDetector
    {
        private readonly float tapMaxDistancePx;
        private bool isDown;
        private Vector2 downPos;

        /// <param name="tapMaxDistancePx">Distance maximale (en pixels à ~160 DPI — mise à l'échelle
        /// DPI en interne via <see cref="GestureScale"/>) entre l'appui et le relâchement pour que ce
        /// soit encore un tap plutôt qu'un glissement.</param>
        public TapGestureDetector(float tapMaxDistancePx)
        {
            this.tapMaxDistancePx = tapMaxDistancePx;
        }

        /// <summary>Traite une frame de pointeur ; renvoie true UNE SEULE fois, à la frame où un tap
        /// complet vient d'être détecté (avec la position du relâchement dans
        /// <paramref name="tapPosition"/>).
        ///
        /// IMPORTANT : les deux tests ci-dessous (appui, relâchement) sont volontairement
        /// INDÉPENDANTS — deux `if`, jamais `else if`. Un appui et un relâchement d'un tap bref
        /// peuvent arriver dans la MÊME frame dès que la cadence baisse, ou quand le joueur tape vite.
        /// Avec un `else if`, seule la branche d'appui s'exécuterait : le tap serait PUREMENT PERDU,
        /// symptôme vécu et rapporté "la sélection ne répond plus" (régression 2026-09-04).</summary>
        public bool TryDetectTap(in PointerFrame frame, out Vector2 tapPosition)
        {
            tapPosition = default;
            if (!frame.isActive) return false;

            if (frame.wasPressed)
            {
                isDown = true;
                downPos = frame.position;
            }

            if (frame.wasReleased && isDown)
            {
                isDown = false;
                // Seuil mis à l'échelle DPI (2026-09-13) : un tap qui tremble de quelques pixels BRUTS
                // est un tap parfaitement immobile sur un écran de référence (~160 DPI), mais un vrai
                // petit glissement sur un téléphone moderne (souvent 400+ DPI) — sans cette échelle, ce
                // même tap dépassait le seuil et se perdait comme un glisser-déposer au lieu d'un tap.
                if (Vector2.Distance(downPos, frame.position) < tapMaxDistancePx * GestureScale.TouchDpiScale)
                {
                    tapPosition = frame.position;
                    return true;
                }
            }

            return false;
        }
    }
}
