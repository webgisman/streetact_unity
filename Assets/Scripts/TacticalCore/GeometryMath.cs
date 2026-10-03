using UnityEngine;

namespace Novgov.TacticalCore
{
    /// <summary>
    /// Primitives géométriques 2D déterministes — uniquement +, -, *, /, comparaisons (aucun
    /// sin/cos/atan2/Mathf.Angle). Base de tout ce qui doit produire le même résultat sur tous
    /// les appareils : ligne de vue, cônes d'Overwatch, intersections de segments.
    /// </summary>
    public static class GeometryMath
    {

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        /// <summary>Distance au carré — préférer à Vector2.Distance pour les comparaisons (évite
        /// une racine carrée inutile ; sqrt est garanti déterministe par IEEE 754 mais reste plus
        /// coûteuse, à réserver aux cas où la vraie distance est nécessaire, ex: portée d'arme).</summary>
        public static float SqrDistance(Vector2 a, Vector2 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y;
            return dx * dx + dy * dy;
        }

        /// <summary>Distance au carré d'un point au SEGMENT [a,b] (pas à la droite infinie).
        /// N'utilise que +,-,*,/ — aucune racine, aucune trigonométrie : déterministe partout,
        /// conformément à la règle de ce fichier.</summary>
        public static float SqrDistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = new Vector2(b.x - a.x, b.y - a.y);
            float abLenSq = ab.x * ab.x + ab.y * ab.y;
            if (abLenSq < 1e-12f) return SqrDistance(p, a); // segment dégénéré

            float t = ((p.x - a.x) * ab.x + (p.y - a.y) * ab.y) / abLenSq;
            t = t < 0f ? 0f : (t > 1f ? 1f : t); // projection bornée AU segment
            float cx = a.x + ab.x * t, cy = a.y + ab.y * t;
            float dx = p.x - cx, dy = p.y - cy;
            return dx * dx + dy * dy;
        }

        /// <summary>Point-dans-polygone 2D (ray casting), même algorithme que
        /// BuildingStructure.ContainsPoint2D — dupliqué ici pour que TacticalCore n'ait aucune
        /// dépendance vers les composants de scène Unity (voir en-tête de TacticalTypes.cs).</summary>
        public static bool PointInPolygon(System.Collections.Generic.List<Vector2> polygon, Vector2 pt)
        {
            if (polygon == null || polygon.Count < 3) return false;
            bool inside = false;
            int count = polygon.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                Vector2 pi = polygon[i];
                Vector2 pj = polygon[j];
                if (pi.y != pj.y && ((pi.y > pt.y) != (pj.y > pt.y)) &&
                    (pt.x < (pj.x - pi.x) * (pt.y - pi.y) / (pj.y - pi.y) + pi.x))
                {
                    inside = !inside;
                }
            }
            return inside;
        }
    }
}
