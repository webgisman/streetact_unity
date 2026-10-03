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

            /// <summary>Rang d'insertion — départage les ex æquo à FCost ET hCost identiques, pour
            /// que le chemin retenu ne dépende jamais de l'implémentation du tas.</summary>
            public int seq;
            /// <summary>Position dans le tas, -1 si l'unité n'y est plus (déjà dépilée).</summary>
            public int heapIndex = -1;
        }

        /// <summary>Tas binaire indexé (decrease-key) pour la liste ouverte de l'A* : O(log n) au lieu d'un
        /// balayage linéaire qui figeait le jeu sur une destination inatteignable. Ordre strictement total
        /// (FCost, hCost, rang d'insertion) : même chemin sur tous les appareils.</summary>
        private class NodeHeap
        {
            private readonly List<Node> items = new List<Node>();

            public int Count => items.Count;

            private static bool HasPriority(Node a, Node b)
            {
                if (a.FCost != b.FCost) return a.FCost < b.FCost;
                if (a.hCost != b.hCost) return a.hCost < b.hCost;
                return a.seq < b.seq;
            }

            public void Push(Node n)
            {
                n.heapIndex = items.Count;
                items.Add(n);
                SiftUp(n.heapIndex);
            }

            public Node Pop()
            {
                Node top = items[0];
                top.heapIndex = -1;

                int last = items.Count - 1;
                if (last == 0) { items.RemoveAt(0); return top; }

                items[0] = items[last];
                items[0].heapIndex = 0;
                items.RemoveAt(last);
                SiftDown(0);
                return top;
            }

            /// <summary>À appeler après avoir DIMINUÉ le gCost d'un nœud déjà présent.</summary>
            public void DecreaseKey(Node n)
            {
                if (n.heapIndex >= 0) SiftUp(n.heapIndex);
            }

            private void SiftUp(int i)
            {
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (!HasPriority(items[i], items[parent])) break;
                    Swap(i, parent);
                    i = parent;
                }
            }

            private void SiftDown(int i)
            {
                while (true)
                {
                    int left = 2 * i + 1, right = 2 * i + 2, best = i;
                    if (left < items.Count && HasPriority(items[left], items[best])) best = left;
                    if (right < items.Count && HasPriority(items[right], items[best])) best = right;
                    if (best == i) break;
                    Swap(i, best);
                    i = best;
                }
            }

            private void Swap(int a, int b)
            {
                Node tmp = items[a];
                items[a] = items[b];
                items[b] = tmp;
                items[a].heapIndex = a;
                items[b].heapIndex = b;
            }
        }

        public static List<Vector2> FindPath(TacticalGrid grid, Vector2 startWorld, Vector2 endWorld)
        {
            return FindPath(grid, startWorld, endWorld, null);
        }

        /// <summary>Même A*, CONFINÉ à une empreinte de bâtiment quand <paramref name="roofFootprint"/> est
        /// fourni (déplacement sur un toit) : seule l'empreinte compte, pas la marchabilité au sol.</summary>
        public static List<Vector2> FindPath(TacticalGrid grid, Vector2 startWorld, Vector2 endWorld, List<Vector2> roofFootprint)
        {
            if (!grid.TryWorldToCell(startWorld, out int startX, out int startZ)) return new List<Vector2>();
            if (!grid.TryWorldToCell(endWorld, out int endX, out int endZ)) return new List<Vector2>();

            // Arrivée impraticable (ex. intérieur d'un bâtiment) : abandon immédiat — sinon l'A* explorait toute
            // la carte (57 600 cellules) avant de conclure qu'il n'y a pas de chemin.
            if (!IsPassable(grid, roofFootprint, endX, endZ, startX, startZ)) return new List<Vector2>();

            // La cellule de départ est toujours praticable : l'unité s'y tient déjà. Sans cette
            // exception, une unité dont le centre de cellule tombe juste hors de l'empreinte (bord
            // de toit, décalage de ~1m dû à CellSize) n'aurait aucun voisin atteignable.
            var open = new NodeHeap();
            var closed = new HashSet<(int, int)>();
            var nodeAt = new Dictionary<(int, int), Node>();
            int pushOrder = 0;

            Node startNode = new Node { cx = startX, cz = startZ, gCost = 0, hCost = Heuristic(startX, startZ, endX, endZ), seq = pushOrder++ };
            open.Push(startNode);
            nodeAt[(startX, startZ)] = startNode;

            int maxIterations = grid.width * grid.height; // filet de sécurité, jamais de boucle infinie
            int iterations = 0;

            while (open.Count > 0 && iterations++ < maxIterations)
            {
                // Sélection déterministe : le plus petit FCost, puis le plus petit hCost, puis
                // l'ordre d'insertion (jamais un tri instable qui dépendrait de l'implémentation).
                // Assurée par NodeHeap, qui applique exactement cet ordre en O(log n) au lieu du
                // balayage linéaire de toute la frontière à chaque itération.
                Node current = open.Pop();
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
                        if (!IsPassable(grid, roofFootprint, nx, nz, startX, startZ)) continue;

                        // Empêche de couper un coin en diagonale à travers deux cellules bloquées
                        // adjacentes — sinon une unité "traverserait" visuellement le coin d'un mur.
                        if (dx != 0 && dz != 0)
                        {
                            if (!IsPassable(grid, roofFootprint, current.cx + dx, current.cz, startX, startZ) ||
                                !IsPassable(grid, roofFootprint, current.cx, current.cz + dz, startX, startZ))
                                continue;
                        }

                        int stepCost = (dx != 0 && dz != 0) ? DiagonalCost : OrthogonalCost;
                        int tentativeG = current.gCost + stepCost;

                        if (!nodeAt.TryGetValue((nx, nz), out Node neighbor))
                        {
                            neighbor = new Node { cx = nx, cz = nz, gCost = tentativeG, hCost = Heuristic(nx, nz, endX, endZ), parent = current, seq = pushOrder++ };
                            nodeAt[(nx, nz)] = neighbor;
                            open.Push(neighbor);
                        }
                        else if (tentativeG < neighbor.gCost)
                        {
                            neighbor.gCost = tentativeG;
                            neighbor.parent = current;
                            // Déjà dans la frontière : remonter sa priorité (O(log n)) au lieu de
                            // l'ancien open.Contains linéaire. Sinon (déjà dépilé mais pas fermé,
                            // impossible ici puisque tout dépilé est fermé), on le réempile.
                            if (neighbor.heapIndex >= 0) open.DecreaseKey(neighbor);
                            else { neighbor.seq = pushOrder++; open.Push(neighbor); }
                        }
                    }
                }
            }

            return new List<Vector2>(); // aucun chemin trouvé
        }

        /// <summary>Marchabilité d'une cellule pour CE trajet : au sol, la grille classique ; sur un
        /// toit (roofFootprint non nul), l'intérieur de l'empreinte et rien d'autre. La cellule de
        /// départ reste toujours praticable, sinon une unité posée pile sur un bord serait
        /// définitivement immobile (voir FindPath).</summary>
        private static bool IsPassable(TacticalGrid grid, List<Vector2> roofFootprint, int cx, int cz, int startX, int startZ)
        {
            if (cx < 0 || cx >= grid.width || cz < 0 || cz >= grid.height) return false;
            if (cx == startX && cz == startZ) return true;
            if (roofFootprint != null) return GeometryMath.PointInPolygon(roofFootprint, grid.CellToWorld(cx, cz));
            return grid.IsWalkable(cx, cz);
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
