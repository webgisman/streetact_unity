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
        /// <summary>Vrai si les segments [a1,a2] et [b1,b2] se croisent (intersection stricte,
        /// pas juste un contact aux extrémités) — méthode de l'orientation par produit en croix,
        /// sans division ni racine carrée, donc sans risque d'instabilité numérique près des cas
        /// dégénérés qu'aurait une résolution paramétrique classique.</summary>
        public static bool SegmentsIntersect(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
        {
            float d1 = Cross(b2 - b1, a1 - b1);
            float d2 = Cross(b2 - b1, a2 - b1);
            float d3 = Cross(a2 - a1, b1 - a1);
            float d4 = Cross(a2 - a1, b2 - a1);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            {
                return true;
            }
            return false;
        }

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

        /// <summary>Distance au carré d'un point au CONTOUR d'un polygone (0 s'il est pile dessus).
        /// Ne dit RIEN de l'intérieur/extérieur — combiner avec PointInPolygon si besoin.</summary>
        public static float SqrDistanceToPolygonEdge(System.Collections.Generic.List<Vector2> polygon, Vector2 pt)
        {
            if (polygon == null || polygon.Count < 2) return float.MaxValue;
            float best = float.MaxValue;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                float d = SqrDistancePointToSegment(pt, polygon[j], polygon[i]);
                if (d < best) best = d;
            }
            return best;
        }

        /// <summary>Bâtiment contenant <paramref name="pt"/>, ou à défaut le plus proche dont le
        /// contour passe à moins de <paramref name="maxDistance"/> — sinon -1. Renvoie l'identifiant
        /// (<c>TacticalBuilding.id</c>), pas l'index.
        ///
        /// POURQUOI LA TOLÉRANCE (2026-09-07). Les actions de porte (ENTRER DANS LE BÂTIMENT,
        /// GUETTER PAR LA PORTE) désignent forcément un point qui n'est PAS dans l'empreinte : une
        /// porte est générée 5 cm en DEHORS de sa façade (CityGenerator), et le « seuil extérieur »
        /// 1.25 m dehors. Un test d'appartenance strict renvoyait donc « aucun bâtiment » pour
        /// 100 % des points de porte — l'unité n'entrait jamais, et la garnison de porte accordait
        /// -75 % de dégâts subis sans rattacher l'unité à quoi que ce soit.
        ///
        /// Déterministe : balayage par index croissant, comparaison stricte, donc à égalité exacte
        /// de distance c'est toujours le plus petit index qui gagne.</summary>
        public static int FindBuildingAtOrNear(System.Collections.Generic.List<TacticalBuilding> buildings, Vector2 pt, float maxDistance)
        {
            if (buildings == null) return -1;

            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                if (b.footprint != null && b.footprint.Count >= 3 && PointInPolygon(b.footprint, pt)) return b.id;
            }

            float bestSqr = maxDistance * maxDistance;
            int best = -1;
            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                if (b.footprint == null || b.footprint.Count < 3) continue;
                float d = SqrDistanceToPolygonEdge(b.footprint, pt);
                if (d < bestSqr) { bestSqr = d; best = b.id; }
            }
            return best;
        }

        /// <summary>Test de cône SANS trigonométrie : un point est "dans le cône" si le produit
        /// scalaire entre la direction normalisée vers ce point et la direction de visée dépasse
        /// cosHalfAngle (= cos(angle/2), précalculé une fois côté données, jamais recalculé ici).</summary>
        public static bool InsideCone(Vector2 origin, Vector2 facing, float cosHalfAngle, float range, Vector2 point)
        {
            Vector2 toPoint = point - origin;
            float sqrDist = toPoint.x * toPoint.x + toPoint.y * toPoint.y;
            if (sqrDist > range * range) return false;
            if (sqrDist < 0.0001f) return true; // origin == point

            float dist = Mathf.Sqrt(sqrDist); // sqrt : opération de base IEEE 754, sûre
            Vector2 dir = new Vector2(toPoint.x / dist, toPoint.y / dist);
            float dot = dir.x * facing.x + dir.y * facing.y;
            return dot >= cosHalfAngle;
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
