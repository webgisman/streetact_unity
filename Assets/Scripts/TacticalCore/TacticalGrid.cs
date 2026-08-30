using System.Collections.Generic;
using UnityEngine;

namespace Novgov.TacticalCore
{
    /// <summary>
    /// Grille grossière de marchabilité/élévation dérivée des segments de murs et barricades du
    /// TacticalWorldState — utilisée pour le pathfinding (Pathfinding.cs) uniquement. La ligne de
    /// vue et la couverture, elles, se calculent contre les WallSegment/Barricade directement
    /// (LineOfSight.cs), pas contre cette grille : la grille est trop grossière pour représenter
    /// précisément un pan de mur détruit, les segments si (voir la discussion d'architecture).
    /// </summary>
    public class TacticalGrid
    {
        public const float CellSize = 1f; // mètres par cellule — grossier exprès, uniquement pour le pathfinding

        public readonly float minX, minZ;
        public readonly int width, height;
        private readonly bool[] walkable;
        private readonly ZStrata[] elevation;

        public TacticalGrid(float minX, float minZ, int width, int height)
        {
            this.minX = minX;
            this.minZ = minZ;
            this.width = width;
            this.height = height;
            walkable = new bool[width * height];
            elevation = new ZStrata[width * height];
            for (int i = 0; i < walkable.Length; i++) walkable[i] = true;
        }

        /// <summary>Reconstruction directe depuis des tableaux déjà calculés (voir
        /// TacticalGridBuilder, cache disque L2) — évite de rejouer CarveBuildingInteriors (le test
        /// point-dans-polygone coûteux) quand la grille a déjà été calculée et sérialisée une
        /// première fois pour cette carte/tuile.</summary>
        public TacticalGrid(float minX, float minZ, int width, int height, bool[] walkable, ZStrata[] elevation)
        {
            this.minX = minX;
            this.minZ = minZ;
            this.width = width;
            this.height = height;
            this.walkable = walkable;
            this.elevation = elevation;
        }

        /// <summary>Copie brute des cellules praticables — pour la sérialisation uniquement (voir
        /// TacticalGridBuilder cache disque L2). Ne jamais muter le tableau renvoyé : il n'est PAS
        /// une copie défensive côté lecture normale (IsWalkable/SetWalkable), seulement ici.</summary>
        public bool[] GetWalkableArrayForSerialization() => walkable;

        /// <summary>Voir GetWalkableArrayForSerialization — même remarque, pour l'élévation.</summary>
        public ZStrata[] GetElevationArrayForSerialization() => elevation;

        public bool TryWorldToCell(Vector2 worldPos, out int cx, out int cz)
        {
            cx = Mathf.FloorToInt((worldPos.x - minX) / CellSize);
            cz = Mathf.FloorToInt((worldPos.y - minZ) / CellSize);
            return cx >= 0 && cx < width && cz >= 0 && cz < height;
        }

        public Vector2 CellToWorld(int cx, int cz)
        {
            return new Vector2(minX + (cx + 0.5f) * CellSize, minZ + (cz + 0.5f) * CellSize);
        }

        private int Index(int cx, int cz) => cz * width + cx;

        public bool IsWalkable(int cx, int cz)
        {
            if (cx < 0 || cx >= width || cz < 0 || cz >= height) return false;
            return walkable[Index(cx, cz)];
        }

        public void SetWalkable(int cx, int cz, bool value)
        {
            if (cx < 0 || cx >= width || cz < 0 || cz >= height) return;
            walkable[Index(cx, cz)] = value;
        }

        public ZStrata GetElevation(int cx, int cz)
        {
            if (cx < 0 || cx >= width || cz < 0 || cz >= height) return ZStrata.Sol;
            return elevation[Index(cx, cz)];
        }

        public void SetElevation(int cx, int cz, ZStrata value)
        {
            if (cx < 0 || cx >= width || cz < 0 || cz >= height) return;
            elevation[Index(cx, cz)] = value;
        }

        /// <summary>Marque comme non-franchissables toutes les cellules dont le centre tombe à
        /// l'intérieur d'un des polygones de bâtiment fournis (intérieur = non marchable au Sol,
        /// on y entre uniquement par une porte — voir Pathfinding.cs pour le franchissement des
        /// portes/liens verticaux, hors scope de cette grille 2D).</summary>
        public void CarveBuildingInteriors(List<List<Vector2>> buildingFootprints)
        {
            for (int cz = 0; cz < height; cz++)
            {
                for (int cx = 0; cx < width; cx++)
                {
                    Vector2 world = CellToWorld(cx, cz);
                    foreach (var footprint in buildingFootprints)
                    {
                        if (GeometryMath.PointInPolygon(footprint, world))
                        {
                            SetWalkable(cx, cz, false);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>Un bâtiment entier détruit (DestructibleEnvironment.DestroyEnvironment, voir
        /// TacticalResolver.DestroyBuilding) : toutes ses cellules deviennent franchissables d'un
        /// coup — jamais de destruction progressive mur par mur (voir TacticalBuilding).</summary>
        public void OpenBuildingInterior(List<Vector2> footprint)
        {
            for (int cz = 0; cz < height; cz++)
            {
                for (int cx = 0; cx < width; cx++)
                {
                    if (GeometryMath.PointInPolygon(footprint, CellToWorld(cx, cz))) SetWalkable(cx, cz, true);
                }
            }
        }

        /// <summary>Recalcule uniquement les cellules touchées par un segment donné (pose/
        /// destruction d'une barricade) — jamais besoin de refaire toute la grille.</summary>
        public void UpdateAroundSegment(Vector2 p1, Vector2 p2, bool blocked)
        {
            if (!TryWorldToCell(p1, out int x1, out int z1)) return;
            if (!TryWorldToCell(p2, out int x2, out int z2)) return;

            int minCx = Mathf.Max(0, Mathf.Min(x1, x2) - 1);
            int maxCx = Mathf.Min(width - 1, Mathf.Max(x1, x2) + 1);
            int minCz = Mathf.Max(0, Mathf.Min(z1, z2) - 1);
            int maxCz = Mathf.Min(height - 1, Mathf.Max(z1, z2) + 1);

            for (int cz = minCz; cz <= maxCz; cz++)
            {
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    SetWalkable(cx, cz, !blocked);
                }
            }
        }
    }
}
