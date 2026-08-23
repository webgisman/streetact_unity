using UnityEngine;
using System.Collections.Generic;

namespace Novgov.Generation
{
    public static class BuildingSubdivider
    {
        private const float MAX_HOUSE_WIDTH = 12.0f; // Les façades de maisons de ville font rarement plus de 12m
        private const float MIN_HOUSE_AREA = 30.0f;  // Surface minimum d'une maison

        /// <summary>
        /// Découpe récursivement un grand bloc (polygone OSM) en parcelles plus petites.
        /// </summary>
        /// <param name="cutEdges">
        /// Segments internes créés par la découpe (murs mitoyens entre maisons attenantes).
        /// Contrairement aux arêtes du polygone d'origine (façades réelles issues d'OSM), ces murs
        /// ne doivent jamais recevoir de porte ou de fenêtre.
        /// </param>
        public static List<List<Vector2>> Subdivide(List<Vector2> polygon, out List<(Vector2, Vector2)> cutEdges)
        {
            List<List<Vector2>> result = new List<List<Vector2>>();
            cutEdges = new List<(Vector2, Vector2)>();
            SubdivideRecursive(polygon, result, cutEdges);
            return result;
        }

        // Sécurité anti-boucle infinie sur une géométrie OSM pathologique (auto-intersections, doublons...).
        private const int MAX_RECURSION_DEPTH = 40;

        private static void SubdivideRecursive(List<Vector2> poly, List<List<Vector2>> result, List<(Vector2, Vector2)> cutEdges, int depth = 0)
        {
            if (poly.Count < 3) return;

            // 1. Trouver le segment le plus long
            float maxDist = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                int next = (i + 1) % poly.Count;
                float d = Vector2.Distance(poly[i], poly[next]);
                if (d > maxDist) maxDist = d;
            }

            // 2. Condition d'arrêt
            float area = CalculateArea(poly);
            if (maxDist <= MAX_HOUSE_WIDTH || area < MIN_HOUSE_AREA * 2 || depth >= MAX_RECURSION_DEPTH)
            {
                result.Add(poly);
                return;
            }

            // 3. Essaie une coupe perpendiculaire au milieu de chaque arête, de la plus longue à la plus
            // courte, jusqu'à en trouver une valide. La plupart des îlots OSM réels (terrasses de maisons,
            // pâtés de maisons irréguliers) ne sont PAS convexes : se limiter aux polygones convexes (comme
            // avant) empêchait de les découper du tout, laissant un unique bâtiment géant recouvrir tout
            // l'îlot. Une coupe reste valide tant qu'elle ne traverse le contour que deux fois (sinon, sur
            // une forme concave, elle produirait une géométrie auto-intersectante).
            List<int> edgeOrder = new List<int>(poly.Count);
            for (int i = 0; i < poly.Count; i++) edgeOrder.Add(i);
            edgeOrder.Sort((a, b) =>
                Vector2.Distance(poly[b], poly[(b + 1) % poly.Count]).CompareTo(Vector2.Distance(poly[a], poly[(a + 1) % poly.Count])));

            foreach (int edgeIdx in edgeOrder)
            {
                if (Vector2.Distance(poly[edgeIdx], poly[(edgeIdx + 1) % poly.Count]) <= MAX_HOUSE_WIDTH) break; // Arêtes déjà assez courtes

                if (TryCutAtEdge(poly, edgeIdx, out List<Vector2> leftPoly, out List<Vector2> rightPoly, out Vector2 intersectA, out Vector2 intersectB))
                {
                    cutEdges.Add((intersectA, intersectB));
                    SubdivideRecursive(leftPoly, result, cutEdges, depth + 1);
                    SubdivideRecursive(rightPoly, result, cutEdges, depth + 1);
                    return;
                }
            }

            // Aucune coupe simple (2 intersections) trouvée : on garde l'îlot entier plutôt que de risquer
            // une géométrie auto-intersectante.
            result.Add(poly);
        }

        /// <summary>
        /// Tente de couper le polygone avec la perpendiculaire au milieu de l'arête <paramref name="edgeIdx"/>.
        /// Ne réussit que si cette ligne traverse le contour exactement deux fois (coupe géométriquement saine,
        /// valable aussi bien pour un polygone convexe que pour la plupart des polygones concaves réels).
        /// </summary>
        private static bool TryCutAtEdge(List<Vector2> poly, int edgeIdx, out List<Vector2> leftPoly, out List<Vector2> rightPoly, out Vector2 intersectA, out Vector2 intersectB)
        {
            leftPoly = new List<Vector2>();
            rightPoly = new List<Vector2>();
            intersectA = Vector2.zero;
            intersectB = Vector2.zero;

            Vector2 p1 = poly[edgeIdx];
            Vector2 p2 = poly[(edgeIdx + 1) % poly.Count];
            Vector2 midPoint = (p1 + p2) * 0.5f;
            Vector2 edgeDir = (p2 - p1).normalized;
            Vector2 cutNormal = new Vector2(-edgeDir.y, edgeDir.x); // Perpendiculaire

            int crossings = 0;

            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 curr = poly[i];
                Vector2 next = poly[(i + 1) % poly.Count];

                bool currIsLeft = IsLeft(midPoint, cutNormal, curr);
                bool nextIsLeft = IsLeft(midPoint, cutNormal, next);

                if (currIsLeft) leftPoly.Add(curr);
                else rightPoly.Add(curr);

                if (currIsLeft != nextIsLeft)
                {
                    Vector2 intersect = GetIntersection(curr, next, midPoint, cutNormal);
                    leftPoly.Add(intersect);
                    rightPoly.Add(intersect);

                    crossings++;
                    if (crossings == 1) intersectA = intersect;
                    else if (crossings == 2) intersectB = intersect;
                }
            }

            // Un polygone n'est proprement bissecté par une ligne que si elle en traverse le contour
            // exactement deux fois. Plus de deux traversées (forme concave complexe) produirait des
            // sous-polygones auto-intersectants : on rejette cette coupe et on essaiera une autre arête.
            if (crossings != 2) return false;
            if (leftPoly.Count < 3 || rightPoly.Count < 3) return false;
            if (leftPoly.Count == poly.Count || rightPoly.Count == poly.Count) return false;

            return true;
        }

        private static bool IsLeft(Vector2 linePoint, Vector2 lineDir, Vector2 pt)
        {
            // Vector cross product to determine side
            return (lineDir.x * (pt.y - linePoint.y) - lineDir.y * (pt.x - linePoint.x)) > 0;
        }

        private static Vector2 GetIntersection(Vector2 p1, Vector2 p2, Vector2 linePoint, Vector2 lineDir)
        {
            Vector2 v1 = p1 - linePoint;
            Vector2 v2 = p2 - p1;
            Vector2 n = new Vector2(-lineDir.y, lineDir.x);

            float dot = Vector2.Dot(v2, n);
            if (Mathf.Abs(dot) < 0.0001f) return p1;

            float t = -Vector2.Dot(v1, n) / dot;
            return p1 + t * v2;
        }

        public static float CalculateArea(List<Vector2> p)
        {
            float area = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            {
                area += (p[j].x + p[i].x) * (p[j].y - p[i].y);
            }
            return Mathf.Abs(area * 0.5f);
        }
    }
}
