using System.Collections.Generic;
using UnityEngine;

namespace Novgov.TacticalCore
{
    /// <summary>
    /// A* déterministe sur TacticalGrid — coûts en entiers (10 = orthogonal, 14 ≈ 10*racine(2)
    /// arrondi, la constante classique pour éviter tout calcul de racine carrée à chaque étape),
    /// donc aucune divergence possible d'un appareil à l'autre. Remplace NavMeshAgent.CalculatePath
    /// pour le résultat OFFICIEL du déplacement — le NavMeshAgent visuel peut continuer d'exister
    /// côté rendu pour suivre joliment ce chemin, sans jamais influencer le résultat.
    /// </summary>
    public static class Pathfinding
    {
        private const int OrthogonalCost = 10;
        private const int DiagonalCost = 14;

        private class Node
        {
            public int cx, cz;
            public int gCost, hCost;
            public int FCost => gCost + hCost;
            public Node parent;
        }

        public static List<Vector2> FindPath(TacticalGrid grid, Vector2 startWorld, Vector2 endWorld)
        {
            if (!grid.TryWorldToCell(startWorld, out int startX, out int startZ)) return new List<Vector2>();
            if (!grid.TryWorldToCell(endWorld, out int endX, out int endZ)) return new List<Vector2>();

            var open = new List<Node>();
            var closed = new HashSet<(int, int)>();
            var nodeAt = new Dictionary<(int, int), Node>();

            Node startNode = new Node { cx = startX, cz = startZ, gCost = 0, hCost = Heuristic(startX, startZ, endX, endZ) };
            open.Add(startNode);
            nodeAt[(startX, startZ)] = startNode;

            int maxIterations = grid.width * grid.height; // filet de sécurité, jamais de boucle infinie
            int iterations = 0;

            while (open.Count > 0 && iterations++ < maxIterations)
            {
                // Sélection déterministe : le plus petit FCost, puis le plus petit hCost, puis
                // l'ordre d'insertion (jamais un tri instable qui dépendrait de l'implémentation).
                int bestIndex = 0;
                for (int i = 1; i < open.Count; i++)
                {
                    if (open[i].FCost < open[bestIndex].FCost ||
                        (open[i].FCost == open[bestIndex].FCost && open[i].hCost < open[bestIndex].hCost))
                    {
                        bestIndex = i;
                    }
                }

                Node current = open[bestIndex];
                open.RemoveAt(bestIndex);
                closed.Add((current.cx, current.cz));

                if (current.cx == endX && current.cz == endZ)
                {
                    return ReconstructPath(grid, current);
                }

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = current.cx + dx;
                        int nz = current.cz + dz;
                        if (closed.Contains((nx, nz))) continue;
                        if (!grid.IsWalkable(nx, nz)) continue;

                        // Empêche de couper un coin en diagonale à travers deux cellules bloquées
                        // adjacentes — sinon une unité "traverserait" visuellement le coin d'un mur.
                        if (dx != 0 && dz != 0)
                        {
                            if (!grid.IsWalkable(current.cx + dx, current.cz) || !grid.IsWalkable(current.cx, current.cz + dz))
                                continue;
                        }

                        int stepCost = (dx != 0 && dz != 0) ? DiagonalCost : OrthogonalCost;
                        int tentativeG = current.gCost + stepCost;

                        if (!nodeAt.TryGetValue((nx, nz), out Node neighbor))
                        {
                            neighbor = new Node { cx = nx, cz = nz, gCost = tentativeG, hCost = Heuristic(nx, nz, endX, endZ), parent = current };
                            nodeAt[(nx, nz)] = neighbor;
                            open.Add(neighbor);
                        }
                        else if (tentativeG < neighbor.gCost)
                        {
                            neighbor.gCost = tentativeG;
                            neighbor.parent = current;
                            if (!open.Contains(neighbor)) open.Add(neighbor);
                        }
                    }
                }
            }

            return new List<Vector2>(); // aucun chemin trouvé
        }

        private static int Heuristic(int x1, int z1, int x2, int z2)
        {
            int dx = Mathf.Abs(x1 - x2);
            int dz = Mathf.Abs(z1 - z2);
            // Distance diagonale (Chebyshev pondéré) — cohérente avec le déplacement à 8 directions
            // ci-dessus, jamais une sous-estimation qui casserait l'optimalité d'A*.
            return OrthogonalCost * (dx + dz) + (DiagonalCost - 2 * OrthogonalCost) * Mathf.Min(dx, dz);
        }

        private static List<Vector2> ReconstructPath(TacticalGrid grid, Node endNode)
        {
            var path = new List<Vector2>();
            Node current = endNode;
            while (current != null)
            {
                path.Add(grid.CellToWorld(current.cx, current.cz));
                current = current.parent;
            }
            path.Reverse();
            return path;
        }
    }
}
