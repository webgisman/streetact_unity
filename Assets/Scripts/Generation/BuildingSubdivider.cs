using UnityEngine;
using System.Collections.Generic;

namespace StreetAct.Generation
{
    public static class BuildingSubdivider
    {
        private const float MAX_HOUSE_WIDTH = 12.0f; // Les façades de maisons de ville font rarement plus de 12m
        private const float MIN_HOUSE_AREA = 30.0f;  // Surface minimum d'une maison

        /// <summary>
        /// Découpe récursivement un grand bloc (polygone OSM) en parcelles plus petites.
        /// </summary>
        public static List<List<Vector2>> Subdivide(List<Vector2> polygon)
        {
            List<List<Vector2>> result = new List<List<Vector2>>();
            SubdivideRecursive(polygon, result);
            return result;
        }

        private static void SubdivideRecursive(List<Vector2> poly, List<List<Vector2>> result)
        {
            if (poly.Count < 3) return;

            // 0. Only subdivide if polygon is convex to prevent self-intersecting garbage geometry
            bool isConvex = true;
            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 cv1 = poly[i];
                Vector2 cv2 = poly[(i + 1) % poly.Count];
                Vector2 cv3 = poly[(i + 2) % poly.Count];
                Vector2 dir1 = cv2 - cv1;
                Vector2 dir2 = cv3 - cv2;
                if ((dir1.x * dir2.y - dir1.y * dir2.x) > 0) // Assumes CW orientation, cross product should be <= 0 for convex
                {
                    isConvex = false;
                    break;
                }
            }

            // 1. Trouver le segment le plus long
            float maxDist = 0;
            int maxIdx = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                int next = (i + 1) % poly.Count;
                float d = Vector2.Distance(poly[i], poly[next]);
                if (d > maxDist)
                {
                    maxDist = d;
                    maxIdx = i;
                }
            }

            // 2. Condition d'arrêt
            float area = CalculateArea(poly);
            if (maxDist <= MAX_HOUSE_WIDTH || area < MIN_HOUSE_AREA * 2 || !isConvex)
            {
                result.Add(poly);
                return;
            }

            // 3. Calcul de la ligne de coupe (au milieu du plus long segment, perpendiculaire)
            Vector2 p1 = poly[maxIdx];
            Vector2 p2 = poly[(maxIdx + 1) % poly.Count];
            Vector2 midPoint = (p1 + p2) * 0.5f;
            Vector2 edgeDir = (p2 - p1).normalized;
            Vector2 cutNormal = new Vector2(-edgeDir.y, edgeDir.x); // Perpendiculaire (INWARD pour un polygone CW)

            // 4. Découper le polygone avec cette ligne (Sutherland-Hodgman modifié pour 1 ligne)
            List<Vector2> leftPoly = new List<Vector2>();
            List<Vector2> rightPoly = new List<Vector2>();

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
                    // Intersection
                    Vector2 intersect = GetIntersection(curr, next, midPoint, cutNormal);
                    leftPoly.Add(intersect);
                    rightPoly.Add(intersect);
                }
            }

            // Pour éviter les boucles infinies sur des formes très étranges
            if (leftPoly.Count < 3 || rightPoly.Count < 3 || leftPoly.Count == poly.Count || rightPoly.Count == poly.Count)
            {
                result.Add(poly);
                return;
            }

            // Récursion
            SubdivideRecursive(leftPoly, result);
            SubdivideRecursive(rightPoly, result);
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
