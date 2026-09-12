using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Novgov.TacticalCore
{
    /// <summary>
    /// Fonction de résolution PURE : (état, ordres des deux équipes) -> (nouvel état, journal
    /// d'événements). Remplace la simulation Unity (NavMeshAgent + Physics.RaycastAll pendant
    /// plusieurs secondes) comme source du résultat OFFICIEL — le rendu Unity rejoue ensuite le
    /// journal d'événements pour le spectacle, sans jamais recalculer quoi que ce soit.
    ///
    /// Toutes les valeurs numériques sont extraites de l'audit du code solo (2026-08-30, voir
    /// TacticalTypes.cs pour la liste des fichiers sources) — rien n'est approximé ou improvisé.
    /// </summary>
    public static class TacticalResolver
    {
        public const float TickSeconds = 0.25f; // durée réelle simulée d'un pas — MatchSessionManager s'aligne dessus pour le rythme visuel client
        private const float MoveStepDistance = 1f; // mètres parcourus par tick, granularité du déplacement simulé

        public static List<TacticalEvent> Resolve(TacticalWorldState state, List<UnitOrders> ordersTeam1, List<UnitOrders> ordersTeam2, List<Vector2> mortarStrikes)
        {
            var events = new List<TacticalEvent>();

            // 1. Frappes de zone D'ABORD : la destruction doit être visible pour le déplacement/tir
            // qui suit dans la MÊME résolution (voir "Terrain Dynamique" dans l'architecture).
            if (mortarStrikes != null)
            {
                foreach (var strike in mortarStrikes) ApplyAreaDamage(state, strike, events);
            }

            var orderByUnitId = new Dictionary<string, UnitOrders>();
            foreach (var o in ordersTeam1) orderByUnitId[o.unitId] = o;
            foreach (var o in ordersTeam2) orderByUnitId[o.unitId] = o;

            var movers = state.units
                .Where(u => !u.isDead && orderByUnitId.ContainsKey(u.id) && orderByUnitId[u.id].checkpoints.Count > 0)
                .OrderBy(u => u.id, System.StringComparer.Ordinal) // ordre STABLE, jamais dépendant du hash/plateforme
                .ToList();

            // Expansion PRÉALABLE (une fois, avant la boucle de tick) de chaque ordre en un chemin
            // fin qui suit RÉELLEMENT la géométrie (Pathfinding.FindPath, la même grille A* que le
            // serveur utilise déjà pour tout le reste) entre chaque paire de checkpoints consécutifs
            // — plus une simple ligne droite entre eux, qui pouvait couper tout droit à travers un
            // bâtiment ("prend le raccourci", corrigé 2026-09-02). Chaque checkpoint garde la trace
            // de l'INDEX exact, dans ce chemin étendu, où l'unité l'atteint réellement — c'est cet
            // index qui déclenche sa commande, pas la fin de l'ordre entier (voir ExpandOrder).
            var expandedByUnitId = new Dictionary<string, ExpandedOrder>();
            foreach (var u in movers) expandedByUnitId[u.id] = ExpandOrder(state, u, orderByUnitId[u.id]);

            // outputY veut dire "hauteur CHANGÉE pendant cette résolution" (voir TacticalUnit.outputY) :
            // il faut donc le remettre à null au début, sinon la valeur d'un tour précédent est
            // réappliquée en boucle et le champ ne signifie plus rien. L'expansion ci-dessus est faite
            // AVANT cette purge car elle a besoin de connaître la strate de départ.
            foreach (var u in state.units) u.outputY = null;

            var progress = new Dictionary<string, int>();
            foreach (var u in movers) progress[u.id] = 0;

            // Halte tactique en cours (NodeAction.Attendre30s -> PathCheckpoint.waitSeconds) : nombre
            // de ticks encore à décompter IMMOBILE avant de reprendre le chemin au-delà de ce
            // checkpoint. Une entrée n'existe dans ce dictionnaire que pendant une attente active —
            // absente sinon (voir TryGetValue plus bas).
            var waitTicksRemaining = new Dictionary<string, int>();

            // 2. Boucle principale : déplacement pas à pas ET combat continu (cadence de tir
            // réelle, voir UnitAI_Combat.cs §1.1) tant que quelqu'un bouge encore OU qu'un
            // engagement reste possible — même structure en deux temps que l'ancien
            // ExecuterTourCoroutine (mouvement, puis fenêtre de combat courte), fusionnée ici en
            // une seule boucle puisqu'il n'y a plus de temps réel à attendre.
            int tick = 0;
            const int maxTicks = 3200; // filet de sécurité (800s simulées) — chemins réels plus longs qu'une ligne droite, jamais une boucle infinie pour autant

            // FENÊTRE DE COMBAT (correctif 2026-09-03). La boucle se poursuivait tant qu'il y avait
            // du mouvement OU un tir. Or un tir n'a lieu que lorsqu'un cooldown arrive à échéance :
            // avec une cadence de 0.35s et des pas de 0.25s, un fantassin tire un pas sur deux. Dès
            // que TOUS les tireurs se trouvaient en rechargement le même pas — c'est-à-dire au pas
            // n°2 pour deux fantassins immobiles — la condition tombait et la résolution s'arrêtait
            // net. Un engagement se limitait donc à UN échange de deux balles par tour (2×15 PV sur
            // 100), et les parties s'éternisaient jusqu'au plafond de tours sans que rien n'arrive.
            //
            // On reproduit maintenant explicitement la structure du solo (mouvement, puis fenêtre de
            // combat courte) : le combat tourne pendant tout le mouvement, puis pendant exactement
            // 2.0s après son arrêt — la même durée que le `combatResolutionTimer < 2.0f` de
            // TacticalPathManager_Execution. La durée de la résolution reste donc bornée par la
            // longueur du chemin + 8 pas, quoi qu'il arrive.
            const int CombatWindowTicks = 8; // 8 × TickSeconds = 2.0s exactement
            int ticksSinceMovementStopped = 0;

            bool anyActivity = true;
            while (anyActivity && tick < maxTicks)
            {
                tick++;
                bool anyMovement = false;
                bool anyWaiting = false;

                foreach (var unit in movers)
                {
                    if (unit.isDead) continue;
                    var expanded = expandedByUnitId[unit.id];
                    int idx = progress[unit.id];
                    if (idx >= expanded.steps.Count) continue;

                    // HALTE TACTIQUE EN COURS (NodeAction.Attendre30s) : l'unité reste immobile sur
                    // place, sans réévaluer ni ré-appliquer le checkpoint déjà atteint (voir plus bas
                    // où l'attente est armée) — seul le décompte progresse. Le combat continu
                    // (ResolveContinuousCombat, hors de cette boucle par unité) tourne quand même
                    // normalement à chaque tick, attente ou pas.
                    if (waitTicksRemaining.TryGetValue(unit.id, out int ticksLeft) && ticksLeft > 0)
                    {
                        anyWaiting = true;
                        ticksLeft--;
                        if (ticksLeft <= 0)
                        {
                            waitTicksRemaining.Remove(unit.id);
                            progress[unit.id] = idx + 1; // attente terminée : reprendre vers le nœud suivant
                        }
                        else
                        {
                            waitTicksRemaining[unit.id] = ticksLeft;
                        }
                        continue;
                    }

                    // 2026-09-06 : correctif "les unités ne démarrent pas ensemble" (aucun ordre de
                    // halte/posture en jeu — confirmé par le joueur). MoveTowards() vers un seul
                    // point de cheminement par tick gaspillait le reste du budget MoveStepDistance
                    // dès que ce point était plus proche que 1m — fréquent, un point de cheminement
                    // est souvent un simple coin du chemin A* (Pathfinding.FindPath), espacé de
                    // ~1m à ~1.41m selon qu'il est orthogonal ou diagonal, jamais aligné sur la
                    // position réelle (non quantifiée) de l'unité. Deux unités parcourant la même
                    // distance totale pouvaient donc avancer de quantités très différentes au même
                    // tick selon le hasard de cet alignement — l'une semblant "démarrer en retard"
                    // alors qu'elle avançait déjà, juste de quelques centimètres invisibles.
                    // Le budget restant est maintenant reporté sur le(s) point(s) de cheminement
                    // SUIVANT(S) dans le MÊME tick, tant qu'aucun repère ne l'interrompt — l'unité
                    // avance ainsi de MoveStepDistance à chaque tick, quel que soit l'espacement réel
                    // des points intermédiaires. Toujours UN SEUL événement "Move" par unité par tick
                    // (position finale seulement) : le format réseau/la relecture client ne changent
                    // pas. Un repère (checkpoint, transition verticale ou halte armée) arrête quand
                    // même la progression pour CE tick, exactement comme avant.
                    Vector2 currentPos = unit.position;
                    float remainingBudget = MoveStepDistance;
                    bool stoppedOnCheckpoint = false;

                    while (remainingBudget > 0f && idx < expanded.steps.Count && !stoppedOnCheckpoint)
                    {
                        Vector2 target = expanded.steps[idx];
                        float distToTarget = Vector2.Distance(currentPos, target);

                        if (distToTarget > remainingBudget)
                        {
                            currentPos = MoveTowards(currentPos, target, remainingBudget);
                            remainingBudget = 0f;
                            break;
                        }

                        // Arrivée exacte à ce point de cheminement, avec du budget en réserve.
                        currentPos = target;
                        remainingBudget -= distToTarget;

                        // Franchissement vertical (montée/descente de toit) résolu par la GÉOMÉTRIE
                        // pendant l'expansion, pas par un drapeau de fin d'ordre : appliqué au pas
                        // exact où l'unité passe le parapet (voir ExpandOrder, VerticalTransition).
                        float? elevationBefore = unit.outputY;

                        if (expanded.transitionAtStep.TryGetValue(idx, out VerticalTransition transition))
                        {
                            unit.outputY = transition.y;
                            unit.zStrata = transition.toRoof ? ZStrata.Toit : ZStrata.Sol;
                        }

                        // Commande exécutée EXACTEMENT ici, dès l'arrivée à CE checkpoint précis —
                        // jamais reportée à la fin de l'ordre entier (voir ExpandOrder/PathCheckpoint).
                        PathCheckpoint checkpoint = null;
                        bool hasCheckpoint = expanded.checkpointAtStep.TryGetValue(idx, out checkpoint);
                        if (hasCheckpoint)
                        {
                            ApplyCheckpointEffects(unit, checkpoint);
                        }

                        // Changement de hauteur daté DU PAS où il a lieu, pour que le rejeu client
                        // suive la montée/descente au bon moment (voir TacticalEvent.Kind.Elevation).
                        if (unit.outputY.HasValue && (!elevationBefore.HasValue || !Mathf.Approximately(elevationBefore.Value, unit.outputY.Value)))
                        {
                            events.Add(new TacticalEvent { kind = TacticalEvent.Kind.Elevation, unitId = unit.id, position = currentPos, y = unit.outputY.Value, tick = tick });
                        }

                        // ATTENDRE30S : armer la halte au lieu d'avancer tout de suite (correctif
                        // 2026-09-05, voir PathCheckpoint.waitSeconds). L'unité reste marquée sur CE
                        // pas — la garde en tête de boucle décomptera les ticks suivants sans jamais
                        // réappliquer ApplyCheckpointEffects — et ne progresse vers le nœud suivant
                        // qu'une fois le décompte écoulé.
                        if (checkpoint != null && checkpoint.waitSeconds.HasValue && checkpoint.waitSeconds.Value > 0f)
                        {
                            waitTicksRemaining[unit.id] = Mathf.Max(1, Mathf.RoundToInt(checkpoint.waitSeconds.Value / TickSeconds));
                            anyWaiting = true;
                            stoppedOnCheckpoint = true;
                        }
                        else if (hasCheckpoint || transition != null)
                        {
                            // Un vrai repère (checkpoint sans halte, ou franchissement vertical) —
                            // comportement inchangé : on avance vers le nœud suivant mais on
                            // n'enchaîne pas d'autre point dans CE tick.
                            progress[unit.id] = idx + 1;
                            idx++;
                            stoppedOnCheckpoint = true;
                        }
                        else
                        {
                            // Simple point de cheminement (coin de trajet A* sans signification pour
                            // le joueur) : on enchaîne avec le budget restant, c'est le cœur du
                            // correctif.
                            progress[unit.id] = idx + 1;
                            idx++;
                        }
                    }

                    unit.position = currentPos;
                    events.Add(new TacticalEvent { kind = TacticalEvent.Kind.Move, unitId = unit.id, position = currentPos, tick = tick });
                    anyMovement = true;

                    // Interruption Overwatch (NOUVELLE fonctionnalité, voir TacticalTypes.cs) : un
                    // ennemi qui surveille cette position tire AVANT que qui que ce soit d'autre
                    // ne bouge. Toujours une seule vérification par unité par tick, sur la position
                    // finale une fois tout le budget du tick consommé.
                    CheckOverwatchInterrupt(state, unit, events, tick);
                    if (unit.isDead) progress[unit.id] = expanded.steps.Count;
                }

                // Combat continu (UnitAI_Combat.Update, cadence réelle par cooldown d'arme) : à
                // CHAQUE tick, toute unité vivante dont le cooldown est écoulé engage sa meilleure
                // cible visible. S'applique aux unités immobiles ET à celles en cours de
                // déplacement (une unité peut tirer tout en marchant, comme en solo).
                ResolveContinuousCombat(state, events, tick);

                // Le compteur de fenêtre ne repart que sur une activité réelle (mouvement OU halte
                // Attendre30s en cours) : le combat ne dépend donc plus de la chance qu'un cooldown
                // ait expiré pile à ce pas. Une halte compte comme activité (correctif 2026-09-05) —
                // sinon, dès qu'aucune AUTRE unité ne bouge pendant les 2s de fenêtre de combat, la
                // résolution entière se serait arrêtée alors qu'une unité est encore en plein milieu
                // de ses 30 secondes d'attente planifiées, son reste de chemin jamais parcouru.
                bool anyOrderActivity = anyMovement || anyWaiting;
                ticksSinceMovementStopped = anyOrderActivity ? 0 : ticksSinceMovementStopped + 1;
                anyActivity = anyOrderActivity || ticksSinceMovementStopped < CombatWindowTicks;
            }

            return events;
        }

        /// <summary>Seuil exact "l'unité est sur un toit" — identique à UnitAI.isRooftopSniper côté
        /// rendu. Centralisé ici parce que la strate (Sol/Toit) est maintenant décidée par la
        /// géométrie pendant l'expansion des ordres, plus par un drapeau posé par l'appelant.</summary>
        public const float RoofStrataThresholdY = 2.2f;

        /// <summary>Franchissement vertical (monter sur un toit / en redescendre) inséré par
        /// ExpandOrder à l'INDEX DE PAS exact où il a lieu — le moment où l'unité passe le parapet,
        /// jamais un effet global de fin d'ordre.</summary>
        private class VerticalTransition
        {
            public float y;
            public bool toRoof;
        }

        /// <summary>Chemin fin (suivant réellement la géométrie) issu de l'expansion d'un UnitOrders
        /// — voir Resolve().</summary>
        private class ExpandedOrder
        {
            public readonly List<Vector2> steps = new List<Vector2>();
            public readonly Dictionary<int, PathCheckpoint> checkpointAtStep = new Dictionary<int, PathCheckpoint>();
            public readonly Dictionary<int, VerticalTransition> transitionAtStep = new Dictionary<int, VerticalTransition>();
            /// <summary>Pas INDIVISIBLES autres qu'un franchissement vertical : aujourd'hui le
            /// franchissement de seuil d'une porte (voir ExpandOrder, entrée dans un bâtiment). Comme
            /// pour une montée/descente de toit, s'arrêter AU MILIEU d'un tel pas est incohérent — la
            /// position 2D franchirait à moitié la façade et se retrouverait DANS l'empreinte, sur une
            /// cellule creusée non franchissable, pendant que le checkpoint porteur d'enterBuildingId
            /// serait supprimé par la troncature (tout index >= la coupe). L'unité serait alors
            /// géométriquement dedans et logiquement dehors : exactement l'état incohérent que
            /// l'entrée par la porte vise à supprimer.</summary>
            public readonly HashSet<int> atomicStep = new HashSet<int>();
        }

        /// <summary>Relie chaque paire de checkpoints consécutifs par le VRAI chemin de la grille
        /// A* (Pathfinding.FindPath, exactement l'algorithme déjà utilisé pour l'aperçu et pour tout
        /// le reste du mouvement côté serveur) au lieu d'une ligne droite — c'est ce qui empêche une
        /// unité de couper à travers un bâtiment entre deux points éloignés d'un même ordre. Le
        /// DERNIER pas de chaque tronçon est ramené EXACTEMENT sur la position demandée par le
        /// checkpoint (pas le centre de la cellule de grille, qui peut être décalé de ~1m) — important
        /// pour les commandes liées à une géométrie précise (fenêtre, porte). Repli sur une ligne
        /// droite si la grille est absente ou si aucun chemin n'existe (zone coupée par des
        /// décombres, etc.) — mieux vaut un mouvement direct qu'une unité totalement bloquée.</summary>
        /// <remarks>
        /// TRAVERSÉE DES TOITS (correctif 2026-09-03 — "l'infanterie quand elle monte sur le toit").
        /// L'expansion suit maintenant la STRATE de l'unité, pas seulement sa position 2D. C'était le
        /// vrai bug : Pathfinding.FindPath est une grille 2D dont l'intérieur des bâtiments est creusé
        /// comme non-franchissable (TacticalGrid.CarveBuildingInteriors), donc pour une unité perchée
        /// la cellule de départ ET toutes ses voisines sur le toit étaient déclarées bloquées. A*
        /// renvoyait une liste vide et le repli "ligne droite" ci-dessous s'appliquait à CHAQUE
        /// déplacement de toit : le fantassin traversait le vide au-dessus de la rue et les bâtiments
        /// voisins. Le même repli frappait l'approche d'une Escalade, dont le checkpoint est un point
        /// SUR le toit, donc lui aussi dans une cellule creusée.
        ///
        /// Trois transitions sont désormais construites explicitement :
        ///   - sol -> toit  : chemin AU SOL jusqu'au pied de la façade, puis montée, puis traversée
        ///                    du toit jusqu'au point demandé ;
        ///   - toit -> sol  : traversée du toit jusqu'au bord le plus proche de la cible, descente,
        ///                    puis chemin AU SOL jusqu'à la cible ;
        ///   - toit -> toit : aucune passerelle n'existe entre deux bâtiments — on redescend d'abord,
        ///                    on traverse la rue au sol, puis on remonte.
        /// La strate résultante est appliquée au pas exact du franchissement (VerticalTransition), ce
        /// qui remplace UnitOrders.implicitDescentY : ce drapeau, posé par l'appelant dès qu'un ordre
        /// ne contenait pas d'Escalade, faisait aussi redescendre une unité qui se contentait de se
        /// déplacer SUR son toit ("CONTINUER SUR LE TOIT") — le serveur la ramenait au sol alors que
        /// le client la laissait perchée, une divergence directe entre les deux.
        /// </remarks>
        private static ExpandedOrder ExpandOrder(TacticalWorldState state, TacticalUnit unit, UnitOrders order)
        {
            var expanded = new ExpandedOrder();
            Vector2 startPos = unit.position;
            Vector2 cursor = unit.position;

            // Strate de départ déduite de la GÉOMÉTRIE : une unité marquée "Toit" doit réellement se
            // trouver au-dessus d'une empreinte de bâtiment. Si ce n'est pas le cas (état hérité d'un
            // tour résolu par l'ancienne ligne droite, qui pouvait la laisser au-dessus du vide), on
            // la traite comme au sol plutôt que de la confiner à un toit inexistant.
            int currentRoof = (unit.zStrata == ZStrata.Toit) ? FindRoofUnder(state, cursor) : -1;

            // Bâtiment dont l'unité occupe actuellement l'INTÉRIEUR au sol — état persistant entre
            // les ordres (via unit.currentBuildingId, posé/effacé par ApplyCheckpointEffects),
            // symétrique de currentRoof pour les toits (correctif 2026-09-06, "l'infanterie ne
            // rentre pas dans les bâtiments"). Mutuellement exclusif avec currentRoof : zStrata vaut
            // Sol XOR Toit, donc jamais les deux à la fois.
            int currentInterior = (currentRoof < 0 && unit.currentBuildingId >= 0 && unit.currentBuildingId < state.buildings.Count && !state.buildings[unit.currentBuildingId].destroyed)
                ? unit.currentBuildingId : -1;

            foreach (var checkpoint in order.checkpoints)
            {
                // Strate VISÉE par ce checkpoint. Une Escalade explicite (setPositionY) désigne le
                // bâtiment à gravir ; sinon, une unité DÉJÀ perchée reste sur son toit tant que le
                // point demandé tombe encore dans l'empreinte de ce même bâtiment — c'est le cas de
                // "CONTINUER SUR LE TOIT", qui n'émet qu'un simple nœud Continuer sans setPositionY et
                // ne doit donc surtout pas être compris comme un ordre de descendre.
                bool explicitClimb = checkpoint.setPositionY.HasValue && checkpoint.setPositionY.Value > RoofStrataThresholdY;
                int targetRoof;
                if (explicitClimb)
                {
                    targetRoof = FindRoofUnder(state, checkpoint.position);
                }
                else if (currentRoof >= 0 && GeometryMath.PointInPolygon(state.buildings[currentRoof].footprint, checkpoint.position))
                {
                    targetRoof = currentRoof;
                }
                else
                {
                    targetRoof = -1;
                }

                // 1. Quitter le toit courant s'il n'est pas celui visé (cible au sol, ou autre toit).
                if (currentRoof >= 0 && currentRoof != targetRoof)
                {
                    List<Vector2> roofFootprint = state.buildings[currentRoof].footprint;

                    Vector2 exitPoint = NearestCellInsideFootprint(state.grid, roofFootprint, checkpoint.position, cursor);
                    AppendLeg(state, expanded, ref cursor, exitPoint, roofFootprint);

                    Vector2 landing = NearestGroundCellOutsideFootprint(state.grid, roofFootprint, exitPoint);
                    expanded.steps.Add(landing);
                    expanded.transitionAtStep[expanded.steps.Count - 1] = new VerticalTransition { y = 0f, toRoof = false };
                    cursor = landing;
                    currentRoof = -1;
                }

                if (currentRoof < 0 && targetRoof >= 0)
                {
                    // 2. Escalade : approche AU SOL du pied de la façade la plus proche du point visé,
                    //    puis montée, puis traversée du toit jusqu'à ce point.
                    List<Vector2> targetFootprint = state.buildings[targetRoof].footprint;

                    Vector2 wallBase = NearestGroundCellOutsideFootprint(state.grid, targetFootprint, checkpoint.position);
                    AppendLeg(state, expanded, ref cursor, wallBase, null);

                    int firstRoofStep = expanded.steps.Count;
                    AppendLeg(state, expanded, ref cursor, checkpoint.position, targetFootprint);
                    // Le franchissement du parapet a lieu au PREMIER pas posé sur le toit.
                    int transitionStep = Mathf.Min(firstRoofStep, expanded.steps.Count - 1);
                    if (transitionStep >= 0)
                    {
                        expanded.transitionAtStep[transitionStep] = new VerticalTransition { y = checkpoint.setPositionY.Value, toRoof = true };
                    }

                    currentRoof = targetRoof;
                }
                else if (currentRoof >= 0)
                {
                    // 3a. Reste sur le MÊME toit ("CONTINUER SUR LE TOIT") : trajet confiné à son
                    //     empreinte, comme avant.
                    AppendLeg(state, expanded, ref cursor, checkpoint.position, state.buildings[currentRoof].footprint);
                }
                else
                {
                    // 3b. AU SOL : bâtiment visé par CE checkpoint, explicite (enterBuildingId, posé
                    // par EntrerBatiment/GuetterPorte/GarnisonFenetre) ou déduit géométriquement
                    // quand l'unité est DÉJÀ à l'intérieur (nœud "Continuer" ajouté après un
                    // EntrerBatiment pour rejoindre un point choisi à l'intérieur — voir
                    // TacticalPathManager_ContextMenu.ConfirmerBuildingAction choix 1 — qui ne porte
                    // lui-même aucun drapeau, seule sa géométrie le trahit).
                    //
                    // CORRECTIF 2026-09-06 ("l'infanterie ne rentre pas dans les bâtiments") : sans
                    // ce bloc, ces deux cas retombaient dans l'AppendLeg confinement=null ci-dessous,
                    // dont la destination tombe en plein dans l'intérieur creusé non-franchissable
                    // par TacticalGrid.CarveBuildingInteriors — Pathfinding.FindPath échouait
                    // systématiquement (cellule d'arrivée impraticable, voir le garde-fou de
                    // Pathfinding.cs) et le repli en ligne droite pouvait couper à travers n'importe
                    // quel mur depuis la position ACTUELLE de l'unité, parfois tronqué par le budget
                    // de déplacement avant même d'atteindre la porte.
                    int targetInterior = checkpoint.exitBuilding
                        ? -1
                        : (checkpoint.enterBuildingId >= 0 && checkpoint.enterBuildingId < state.buildings.Count)
                            ? checkpoint.enterBuildingId
                            : (currentInterior >= 0 && GeometryMath.PointInPolygon(state.buildings[currentInterior].footprint, checkpoint.position) ? currentInterior : -1);

                    if (currentInterior >= 0 && currentInterior != targetInterior)
                    {
                        // Sortie (SortirBatiment, ou sortie géométrique implicite) : rejoindre le
                        // seuil le plus proche de la cible EN RESTANT confiné à l'empreinte
                        // (symétrique de la sortie de toit ci-dessus), franchir la porte en un seul
                        // pas, puis rejoindre la cible par un vrai chemin au sol.
                        List<Vector2> insideFootprint = state.buildings[currentInterior].footprint;
                        Vector2 exitPoint = NearestCellInsideFootprint(state.grid, insideFootprint, checkpoint.position, cursor);
                        AppendLeg(state, expanded, ref cursor, exitPoint, insideFootprint);
                        Vector2 landing = NearestGroundCellOutsideFootprint(state.grid, insideFootprint, exitPoint);
                        expanded.steps.Add(landing);
                        cursor = landing;
                        currentInterior = -1;
                    }

                    if (currentInterior < 0 && targetInterior >= 0)
                    {
                        // Entrée : rejoindre le pied de façade le plus proche de la porte par un vrai
                        // chemin au sol, puis franchir le seuil en un seul pas direct (même principe
                        // que l'escalade ci-dessus, sans changement d'altitude).
                        List<Vector2> targetFootprint = state.buildings[targetInterior].footprint;
                        Vector2 approach = NearestGroundCellOutsideFootprint(state.grid, targetFootprint, checkpoint.position);
                        AppendLeg(state, expanded, ref cursor, approach, null);
                        // 2026-09-07 : franchir le seuil doit poser l'unité DEDANS. Le point envoyé
                        // par le client pour une entrée est celui de la PORTE, qui est toujours
                        // légèrement en dehors de l'empreinte (CityGenerator place une porte à 5 cm
                        // vers l'extérieur de sa façade, et le « seuil extérieur » à 1.25 m) : y
                        // atterrir tel quel laissait l'unité marquée « à l'intérieur » (currentInterior)
                        // tout en étant géométriquement DEHORS — un état incohérent qui fausse ensuite
                        // le confinement du déplacement intérieur et l'exemption de mur de
                        // LineOfSight.WallBelongsToOwnBuilding (l'unité tirait à travers sa propre
                        // façade, ou n'était vue de personne, selon le côté).
                        // Sentinelle volontairement IMPOSSIBLE plutôt que checkpoint.position (qui est
                        // dehors) : NearestCellInsideFootprint renvoie son repli quand AUCUN centre de
                        // cellule ne tombe dans l'empreinte (bâtiment plus petit qu'une cellule d'1 m).
                        // Se replier alors sur le point de porte poserait l'unité DEHORS tout en la
                        // marquant à l'intérieur — l'incohérence même que cette entrée corrige. Dans ce
                        // cas dégénéré on n'entre tout simplement pas.
                        Vector2 noInteriorSentinel = new Vector2(float.MaxValue, float.MaxValue);
                        Vector2 entryPoint = GeometryMath.PointInPolygon(targetFootprint, checkpoint.position)
                            ? checkpoint.position
                            : NearestCellInsideFootprint(state.grid, targetFootprint, checkpoint.position, noInteriorSentinel);

                        if (entryPoint.x == float.MaxValue)
                        {
                            // Empreinte trop petite pour contenir un point de grille : on s'arrête au
                            // pied de la façade, sans rattachement. Mieux vaut un ordre qui n'aboutit
                            // pas qu'une unité dans un état contradictoire pour le reste de la partie.
                            cursor = approach;
                        }
                        else
                        {
                            expanded.atomicStep.Add(expanded.steps.Count); // franchissement indivisible (voir atomicStep)
                            expanded.steps.Add(entryPoint);
                            cursor = entryPoint;
                            currentInterior = targetInterior;
                        }
                    }
                    else
                    {
                        // Trajet ordinaire : au sol dehors (confinement nul), ou d'un point à un
                        // autre du MÊME intérieur (confiné à son empreinte, comme sur un toit).
                        List<Vector2> confinement = currentInterior >= 0 ? state.buildings[currentInterior].footprint : null;

                        // 2026-09-06 ("un char peut foncer dans un bâtiment"), généralisé le
                        // 2026-09-12 ("les unités rentrent dans les polygones", retour multijoueur
                        // Deathmatch) : une unité ordinaire ne porte enterBuildingId QUE si le joueur a
                        // explicitement choisi "Entrer" au menu (targetInterior ci-dessus) — mais rien
                        // ne vérifiait que la destination géométrique elle-même ne tombe pas quand même
                        // dans l'empreinte d'un bâtiment que l'unité n'a JAMAIS demandé à visiter (tir
                        // group-move dont le point cliqué recouvre un bâtiment, checkpoint automatique
                        // du repli/formation, etc.). Dans ce cas AppendLeg ci-dessous visait une
                        // cellule creusée non-franchissable (TacticalGrid.CarveBuildingInteriors) :
                        // Pathfinding.FindPath échouait systématiquement (cellule d'arrivée
                        // impraticable) et son repli en ligne droite, jamais vérifié, coupait tout
                        // droit à travers le mur — initialement corrigé pour les seuls chars
                        // (unit.isTank), mais le même repli en ligne droite s'applique à N'IMPORTE
                        // QUELLE unité dont AppendLeg ne trouve pas de chemin, char ou pas : le
                        // fantassin, le véhicule et le mortier "entraient dans le polygone" exactement
                        // pareil, simplement jamais vérifié par un test avant ce jour. Toute unité qui
                        // n'entre pas explicitement redirige donc sa destination sur le point au sol
                        // praticable le plus proche, HORS de ce bâtiment.
                        Vector2 finalTarget = checkpoint.position;
                        if (confinement == null)
                        {
                            int blockingBuilding = FindRoofUnder(state, finalTarget); // vérifie l'appartenance à UNE empreinte, pas seulement les toits malgré le nom
                            if (blockingBuilding >= 0)
                            {
                                finalTarget = NearestGroundCellOutsideFootprint(state.grid, state.buildings[blockingBuilding].footprint, finalTarget);
                            }
                        }

                        AppendLeg(state, expanded, ref cursor, finalTarget, confinement);
                    }
                }

                expanded.checkpointAtStep[expanded.steps.Count - 1] = checkpoint;
            }

            TruncateToMovementBudget(expanded, unit, startPos);
            return expanded;
        }

        /// <summary>Coupe le chemin étendu au budget de déplacement du tour
        /// (TacticalUnit.movementBudget, en mètres). La mesure porte sur la DISTANCE réellement
        /// parcourue le long du chemin, pas sur le nombre de pas : un tronçon de repli en ligne
        /// droite peut condenser des centaines de mètres en un seul pas, et compter les pas
        /// laisserait alors passer un déplacement illimité. Les commandes et franchissements situés
        /// au-delà du budget sont abandonnés — l'unité s'arrête exactement là où son budget
        /// s'épuise, et une posture placée hors de portée ne s'applique pas par anticipation.</summary>
        private static void TruncateToMovementBudget(ExpandedOrder expanded, TacticalUnit unit, Vector2 startPos)
        {
            if (unit.movementBudget <= 0f) return;

            float remaining = unit.movementBudget;
            Vector2 cursor = startPos;

            for (int i = 0; i < expanded.steps.Count; i++)
            {
                float legLength = Vector2.Distance(cursor, expanded.steps[i]);
                if (legLength <= remaining)
                {
                    remaining -= legLength;
                    cursor = expanded.steps[i];
                    continue;
                }

                // Budget épuisé au milieu de ce tronçon : on s'arrête pile à la limite — SAUF si ce
                // tronçon porte un franchissement vertical (correctif 2026-09-05, bug reproduit
                // expérimentalement : une unité peut finir avec sa position 2D d'un côté de la
                // frontière du toit et son zStrata/outputY de l'autre).
                //
                // ExpandOrder ajoute une montée/descente de toit comme UN SEUL pas direct (jamais
                // découpé par AppendLeg/A*), avec sa VerticalTransition indexée sur CE pas précis. Si
                // l'interpolation ci-dessous coupait À L'INTÉRIEUR de ce pas, la position 2D
                // franchirait partiellement l'empreinte du bâtiment (l'unité se retrouve par ex. hors
                // du polygone en pleine "descente"), alors que la transition qui doit justement mettre
                // à jour zStrata/outputY pour ce même pas est supprimée juste en dessous (tout index
                // >= i est retiré) — l'unité reste alors marquée sur l'ancienne strate indéfiniment
                // (rien ne la corrige avant un futur ordre explicite), avec un impact direct sur sa
                // portée de vue/tir (bonus toit) pour le reste de la partie.
                //
                // Un franchissement vertical est donc traité comme ATOMIQUE : hors de portée, on
                // s'arrête AVANT lui (au dernier point déjà validé), jamais en son milieu.
                bool legIsVerticalTransition = expanded.transitionAtStep.ContainsKey(i) || expanded.atomicStep.Contains(i);

                if (!legIsVerticalTransition)
                {
                    Vector2 cut = (legLength > 0.0001f)
                        ? cursor + (expanded.steps[i] - cursor) * (remaining / legLength)
                        : cursor;
                    expanded.steps.RemoveRange(i, expanded.steps.Count - i);
                    expanded.steps.Add(cut);
                }
                else
                {
                    expanded.steps.RemoveRange(i, expanded.steps.Count - i);
                }

                var dropped = new List<int>();
                foreach (var kv in expanded.checkpointAtStep) if (kv.Key >= i) dropped.Add(kv.Key);
                foreach (int k in dropped) expanded.checkpointAtStep.Remove(k);

                dropped.Clear();
                foreach (var kv in expanded.transitionAtStep) if (kv.Key >= i) dropped.Add(kv.Key);
                foreach (int k in dropped) expanded.transitionAtStep.Remove(k);

                return;
            }
        }

        /// <summary>Un tronçon de chemin, suivi RÉELLEMENT sur la géométrie. <paramref name="confinement"/>
        /// non nul = l'unité est sur ce toit et n'en sort pas (voir Pathfinding.FindPath). Repli sur une
        /// ligne droite si la grille est absente ou si aucun chemin n'existe (zone coupée par des
        /// décombres, etc.) — mieux vaut un mouvement direct qu'une unité définitivement bloquée. Le
        /// DERNIER pas est ramené EXACTEMENT sur la position demandée, jamais le centre de la cellule
        /// de grille (décalage possible de ~1m), ce qui compte pour les commandes liées à une géométrie
        /// précise (fenêtre, porte).</summary>
        private static void AppendLeg(TacticalWorldState state, ExpandedOrder expanded, ref Vector2 cursor, Vector2 destination, List<Vector2> confinement)
        {
            List<Vector2> leg = state.grid != null
                ? Pathfinding.FindPath(state.grid, cursor, destination, confinement)
                : new List<Vector2>();

            if (leg.Count < 2) leg = new List<Vector2> { cursor, destination };
            leg[leg.Count - 1] = destination;

            for (int i = 1; i < leg.Count; i++) expanded.steps.Add(leg[i]);
            cursor = destination;
        }

        /// <summary>Index du bâtiment dont l'empreinte contient ce point (donc le toit sur lequel une
        /// unité perchée se tient), -1 si aucun. Un bâtiment détruit n'a plus de toit.</summary>
        private static int FindRoofUnder(TacticalWorldState state, Vector2 point)
        {
            for (int i = 0; i < state.buildings.Count; i++)
            {
                var b = state.buildings[i];
                if (b.destroyed || b.footprint == null || b.footprint.Count < 3) continue;
                if (GeometryMath.PointInPolygon(b.footprint, point)) return i;
            }
            return -1;
        }

        /// <summary>Cellule de grille INTÉRIEURE à l'empreinte (donc sur le toit) la plus proche de
        /// <paramref name="target"/> — le point de descente quand la cible est ailleurs. Balayage à
        /// ordre fixe : jamais dépendant du hash ni de la plateforme. Repli sur <paramref name="fallback"/>
        /// si l'empreinte est plus petite qu'une cellule.</summary>
        private static Vector2 NearestCellInsideFootprint(TacticalGrid grid, List<Vector2> footprint, Vector2 target, Vector2 fallback)
        {
            if (grid == null) return fallback;
            FootprintCellBounds(grid, footprint, 0, out int minCx, out int minCz, out int maxCx, out int maxCz);

            Vector2 best = fallback;
            float bestSqr = float.MaxValue;
            for (int cz = minCz; cz <= maxCz; cz++)
            {
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    Vector2 world = grid.CellToWorld(cx, cz);
                    if (!GeometryMath.PointInPolygon(footprint, world)) continue;
                    float sqr = GeometryMath.SqrDistance(world, target);
                    if (sqr < bestSqr) { bestSqr = sqr; best = world; }
                }
            }
            return best;
        }

        /// <summary>Cellule franchissable AU SOL, hors de l'empreinte, la plus proche de
        /// <paramref name="target"/> — le pied de façade pour une montée, le point d'atterrissage pour
        /// une descente. Balayage à ordre fixe (déterminisme). Repli sur <paramref name="target"/> si
        /// le bâtiment est entièrement ceinturé de décombres ou de barricades : mieux vaut atterrir
        /// approximativement que rendre l'ordre impossible.</summary>
        private static Vector2 NearestGroundCellOutsideFootprint(TacticalGrid grid, List<Vector2> footprint, Vector2 target)
        {
            if (grid == null) return target;
            FootprintCellBounds(grid, footprint, GroundSearchMarginCells, out int minCx, out int minCz, out int maxCx, out int maxCz);

            Vector2 best = target;
            float bestSqr = float.MaxValue;
            for (int cz = minCz; cz <= maxCz; cz++)
            {
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    if (!grid.IsWalkable(cx, cz)) continue; // l'intérieur des bâtiments est creusé : donc déjà "hors empreinte"
                    Vector2 world = grid.CellToWorld(cx, cz);
                    if (GeometryMath.PointInPolygon(footprint, world)) continue;
                    float sqr = GeometryMath.SqrDistance(world, target);
                    if (sqr < bestSqr) { bestSqr = sqr; best = world; }
                }
            }
            return best;
        }

        // Combien de cellules autour de l'empreinte on accepte de fouiller pour trouver un pied de
        // façade / un point d'atterrissage praticable. 4m suffit largement (trottoir), et borne le coût.
        private const int GroundSearchMarginCells = 4;

        private static void FootprintCellBounds(TacticalGrid grid, List<Vector2> footprint, int marginCells, out int minCx, out int minCz, out int maxCx, out int maxCz)
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < footprint.Count; i++)
            {
                Vector2 p = footprint[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minZ) minZ = p.y;
                if (p.y > maxZ) maxZ = p.y;
            }

            grid.TryWorldToCell(new Vector2(minX, minZ), out int loX, out int loZ);
            grid.TryWorldToCell(new Vector2(maxX, maxZ), out int hiX, out int hiZ);

            minCx = Mathf.Max(0, Mathf.Min(loX, hiX) - marginCells);
            minCz = Mathf.Max(0, Mathf.Min(loZ, hiZ) - marginCells);
            maxCx = Mathf.Min(grid.width - 1, Mathf.Max(loX, hiX) + marginCells);
            maxCz = Mathf.Min(grid.height - 1, Mathf.Max(loZ, hiZ) + marginCells);
        }

        private static Vector2 MoveTowards(Vector2 from, Vector2 to, float maxStep)
        {
            float sqrDist = GeometryMath.SqrDistance(from, to);
            if (sqrDist <= maxStep * maxStep) return to;
            float dist = Mathf.Sqrt(sqrDist);
            Vector2 dir = (to - from) / dist;
            return from + dir * maxStep;
        }

        /// <summary>Applique les postures/transitions d'UN checkpoint, DÈS L'ARRIVÉE à celui-ci —
        /// miroir direct des NodeAction du rapport d'audit §2.6-2.10, maintenant déclenché par
        /// checkpoint plutôt qu'une seule fois en bout de chemin (correctif "prend le raccourci",
        /// 2026-09-02 — voir PathCheckpoint/ExpandOrder). isGuarding/isCamouflaged ne sont JAMAIS
        /// remis à false automatiquement ici : fidèle à l'original (rapport §8.2), ils persistent
        /// jusqu'à une prise de dégâts (camouflage) ou indéfiniment (guet — particularité connue
        /// de l'ancien code, reproduite telle quelle).</summary>
        private static void ApplyCheckpointEffects(TacticalUnit unit, PathCheckpoint checkpoint)
        {
            if (checkpoint.setGuarding) unit.isGuarding = true;
            if (checkpoint.setCamouflaged) unit.isCamouflaged = true;

            if (checkpoint.setGarrisonWindow)
            {
                unit.isGarrisoned = true;
                unit.windowNormal = checkpoint.windowNormalToSet;
            }
            if (checkpoint.setGarrisonDoor)
            {
                unit.isGarrisoned = true;
                unit.windowNormal = null; // pas de restriction de cône pour une garnison de porte (rapport §2.9)
            }
            // Rattachement sans entrée (GUETTER PAR LA PORTE) — voir PathCheckpoint.attachBuildingId.
            if (checkpoint.attachBuildingId >= 0)
            {
                unit.currentBuildingId = checkpoint.attachBuildingId;
            }

            if (checkpoint.enterBuildingId >= 0)
            {
                unit.currentBuildingId = checkpoint.enterBuildingId;
            }
            if (checkpoint.exitBuilding)
            {
                // SortirBatiment appelle aussi LeaveGarrison() dans l'original (rapport §2.8) :
                // sortir annule la garnison même si elle ne venait pas d'une fenêtre de CE bâtiment.
                unit.currentBuildingId = -1;
                unit.isGarrisoned = false;
                unit.windowNormal = null;
            }
            if (checkpoint.setPositionY.HasValue)
            {
                unit.outputY = checkpoint.setPositionY;
                unit.zStrata = checkpoint.setPositionY.Value > 2.2f ? ZStrata.Toit : ZStrata.Sol; // seuil exact UnitAI.isRooftopSniper
            }

            // Overwatch armé DÈS CE checkpoint (pas seulement en fin d'ordre) : un "Guetter"/
            // "Embuscade"/"GuetterPorte" posé au milieu d'un trajet surveille depuis là immédiatement,
            // même si l'unité continue ensuite vers d'autres checkpoints — répond explicitement à la
            // demande du designer que chaque checkpoint exécute sa commande sur place.
            if (checkpoint.enterOverwatchAtEnd && checkpoint.overwatchToSet != null)
            {
                unit.watchTrigger = checkpoint.overwatchToSet;
            }
        }

        private static void CheckOverwatchInterrupt(TacticalWorldState state, TacticalUnit mover, List<TacticalEvent> events, int tick)
        {
            foreach (var watcher in state.units)
            {
                if (watcher.isDead || watcher.team == mover.team || watcher.watchTrigger == null) continue;
                if (!TriggerCrossed(watcher.watchTrigger, mover.position)) continue;
                if (!LineOfSight.HasLineOfSight(state, watcher.position, mover.position, watcher.visionType)) continue;

                events.Add(new TacticalEvent { kind = TacticalEvent.Kind.OverwatchTriggered, unitId = watcher.id, targetUnitId = mover.id, position = mover.position, tick = tick });
                ApplyDamage(state, watcher, mover, events, tick);
                watcher.watchTrigger = null; // un seul tir d'interruption par Overwatch, par tour
            }
        }

        private static bool TriggerCrossed(OverwatchTrigger trigger, Vector2 point)
        {
            if (trigger.linePointA.HasValue && trigger.linePointB.HasValue)
            {
                return DistancePointToSegment(point, trigger.linePointA.Value, trigger.linePointB.Value) < 1f;
            }
            return GeometryMath.InsideCone(trigger.origin, trigger.facing, trigger.cosHalfAngle, trigger.range, point);
        }

        private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float sqrLen = ab.x * ab.x + ab.y * ab.y;
            if (sqrLen < 0.0001f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(((p.x - a.x) * ab.x + (p.y - a.y) * ab.y) / sqrLen);
            Vector2 proj = a + ab * t;
            return Vector2.Distance(p, proj);
        }

        /// <summary>UnitAI_Combat.Update (§1.1) : chaque unité vivante non-mortier dont le cooldown
        /// est écoulé engage la cible la plus proche visible (GetVisibleEnemy) et lui tire dessus,
        /// puis relance son cooldown. Renvoie vrai si au moins un tir a eu lieu (pour garder la
        /// boucle principale active tant que des échanges de tirs sont encore possibles, comme
        /// l'ancienne "fenêtre de combat" de 2s en fin de mouvement).</summary>
        private static bool ResolveContinuousCombat(TacticalWorldState state, List<TacticalEvent> events, int tick)
        {
            bool anyShot = false;
            var shooters = state.units.Where(u => !u.isDead && !u.isMortar).OrderBy(u => u.id, System.StringComparer.Ordinal).ToList();

            // RÉSOLUTION SIMULTANÉE (correctif 2026-09-03). Les dégâts étaient appliqués au fil de
            // l'itération, dans l'ordre alphabétique des identifiants d'unité. Or ceux-ci sont
            // préfixés par l'équipe ("Fantassin_1_1" contre "Fantassin_2_1"), donc l'équipe 1 tirait
            // TOUJOURS la première : dans un duel à armes égales elle tuait son adversaire avant
            // qu'il ait riposté, et gagnait donc systématiquement. Le tir d'un même pas est
            // maintenant décidé pour tout le monde AVANT que le moindre dégât ne soit appliqué : une
            // unité abattue à ce pas riposte quand même, et le résultat ne dépend plus du nom des
            // unités. L'ordre d'itération reste alphabétique pour garder un déterminisme strict.
            var volley = new List<(TacticalUnit shooter, TacticalUnit target)>();

            foreach (var shooter in shooters)
            {
                shooter.cooldownRemaining -= TickSeconds;
                if (shooter.cooldownRemaining > 0f) continue;
                if (shooter.weaponCooldownSeconds <= 0f) continue; // pas d'arme de tir direct (ex: mortier)

                TacticalUnit target = FindClosestEngageableTarget(state, shooter);
                if (target == null) continue;

                volley.Add((shooter, target));

                // Report du reliquat au lieu d'une remise à la valeur pleine : la cadence était
                // arrondie AU PAS DE SIMULATION (0.35s se comportait comme 0.50s), donc l'infanterie
                // tirait à 30 DPS au lieu des 43 sur lesquels est calibré le coût de déploiement.
                shooter.cooldownRemaining += shooter.weaponCooldownSeconds;
                if (shooter.cooldownRemaining < 0f) shooter.cooldownRemaining = 0f; // arme plus rapide que le pas : un tir par pas au maximum
                anyShot = true;
            }

            foreach (var (shooter, target) in volley)
            {
                ApplyDamage(state, shooter, target, events, tick);
            }

            return anyShot;
        }

        /// <summary>GetVisibleEnemy (UnitAI_Combat.cs:242-337) : la cible ENNEMIE VIVANTE NON
        /// CAMOUFLÉE la plus proche dans la portée d'engagement, avec ligne de vue — "le plus
        /// proche qui passe le test", pas "le meilleur tactiquement", fidèle à l'original.</summary>
        private static TacticalUnit FindClosestEngageableTarget(TacticalWorldState state, TacticalUnit shooter)
        {
            TacticalUnit best = null;
            float bestSqrDist = float.MaxValue;
            foreach (var target in state.units)
            {
                if (target.isDead || target.team == shooter.team || target.isCamouflaged) continue;
                float sqrDist = GeometryMath.SqrDistance(shooter.position, target.position);
                if (sqrDist >= bestSqrDist) continue;
                if (!LineOfSight.CanEngageTarget(state, shooter, target)) continue;
                best = target;
                bestSqrDist = sqrDist;
            }
            return best;
        }

        // Distance de couverture d'une barricade encore debout — RoadBarrier.cs:193-207 (2.2m exact).
        private const float BarricadeCoverDistance = 2.2f;

        /// <summary>Priorité stricte garnison > barricade > guet (UnitAI.cs:665-682, if/else if —
        /// jamais cumulée). Renvoie le multiplicateur à appliquer aux dégâts BRUTS.</summary>
        private static float ComputeCoverMultiplier(TacticalWorldState state, TacticalUnit target)
        {
            if (target.isGarrisoned) return 0.25f; // -75%
            foreach (var barricade in state.barricades)
            {
                if (!barricade.BlocksSight) continue;
                if (DistancePointToSegment(target.position, barricade.p1, barricade.p2) <= BarricadeCoverDistance) return 0.4f; // -60%
            }
            if (target.isGuarding) return 0.5f; // -50%
            return 1f;
        }

        /// <summary>UnitAI.TakeDamage (UnitAI.cs:665-725), ordre exact : bonus d'embuscade du
        /// TIREUR camouflé (x1.75, casse son camouflage), PUIS réduction de couverture de la
        /// CIBLE, PUIS plancher de 1 dégât minimum, PUIS troncature en entier (pas d'arrondi —
        /// health -= (int)amount dans l'original). Le camouflage de la CIBLE casse
        /// INCONDITIONNELLEMENT dès qu'elle encaisse un dégât, qu'il soit réduit ou non.</summary>
        private static void ApplyDamage(TacticalWorldState state, TacticalUnit shooter, TacticalUnit target, List<TacticalEvent> events, int tick)
        {
            float rawDamage = shooter.weaponDamage;

            if (shooter.isCamouflaged)
            {
                rawDamage *= 1.75f; // UnitAI_Combat.cs:411-416 — bonus d'embuscade, un seul coup
                shooter.isCamouflaged = false;
            }

            float coverMultiplier = ComputeCoverMultiplier(state, target);
            float finalDamage = Mathf.Max(1f, rawDamage * coverMultiplier);
            int damage = (int)finalDamage; // troncature, pas Mathf.Round — fidèle à l'original

            target.health -= damage;
            target.isCamouflaged = false; // UnitAI.cs:685 — casse toujours, même si le tireur n'était pas camouflé

            events.Add(new TacticalEvent { kind = TacticalEvent.Kind.Shot, unitId = shooter.id, targetUnitId = target.id, position = target.position, damage = damage, tick = tick });

            if (target.health <= 0 && !target.isDead)
            {
                target.isDead = true;
                events.Add(new TacticalEvent { kind = TacticalEvent.Kind.Death, unitId = target.id, position = target.position, tick = tick });
            }
        }

        // MortarShell.cs:17-18 — constantes exactes (explosionRadius, maxDamage).
        private const float MortarExplosionRadius = 6.5f;
        private const float MortarMaxDamage = 150f;

        /// <summary>MortarShell.ApplySplashDamage (§6 du rapport). Trois cibles distinctes, trois
        /// formules distinctes — ne jamais les uniformiser :
        ///   - Unités : dégâts Lerp(30,150,%) SI ligne de vue dégagée (bloqué en tout-ou-rien par
        ///     un mur/une barricade, pas de réduction partielle).
        ///   - Bâtiments (TacticalBuilding, PV partagé) : dégâts Lerp(100,375,%) (MortarShell.cs,
        ///     maxDamage*2.5 = 375) et AUCUN test de mur (l'environnement encaisse même à travers
        ///     un autre bâtiment).
        ///   - Barricades : formule LINÉAIRE SANS PLANCHER, dmg = 150 * (1 - dist/rayon) — pas un
        ///     Lerp(30,...), une vraie différence de formule avec les unités (RoadBarrier.cs:212-228).
        /// </summary>
        private static void ApplyAreaDamage(TacticalWorldState state, Vector2 impact, List<TacticalEvent> events)
        {
            // --- Unités ---
            foreach (var unit in state.units)
            {
                if (unit.isDead) continue;
                float dist = Vector2.Distance(unit.position, impact);
                if (dist > MortarExplosionRadius) continue;
                if (!LineOfSight.HasLineOfSight(state, impact, unit.position, VisionType.Normale)) continue; // bloqué en tout-ou-rien

                float damagePercent = 1f - (dist / MortarExplosionRadius);
                int damage = Mathf.RoundToInt(Mathf.Lerp(30f, MortarMaxDamage, damagePercent));
                ApplyDamage(state, new TacticalUnit { id = "mortar", weaponDamage = damage }, unit, events, 0);
            }

            // --- Bâtiments (pool de PV partagé, aucun test de mur — rapport §6/§7) ---
            foreach (var building in state.buildings)
            {
                if (building.destroyed) continue;

                float closestDist = float.MaxValue;
                foreach (var wall in state.wallSegments)
                {
                    if (wall.buildingId != building.id) continue;
                    closestDist = Mathf.Min(closestDist, DistancePointToSegment(impact, wall.p1, wall.p2));
                }
                if (closestDist > MortarExplosionRadius) continue;

                float damagePercent = Mathf.Clamp01(1f - (closestDist / MortarExplosionRadius));
                float damage = Mathf.Lerp(100f, MortarMaxDamage * 2.5f, damagePercent); // 100 à 375
                building.health -= damage;

                if (building.health <= 0f && !building.destroyed)
                {
                    DestroyBuilding(state, building, events);
                }
            }

            // --- Barricades (formule linéaire sans plancher, RoadBarrier.cs:212-228) ---
            foreach (var barricade in state.barricades)
            {
                if (barricade.Destroyed) continue;
                float dist = DistancePointToSegment(impact, barricade.p1, barricade.p2);
                if (dist > MortarExplosionRadius) continue;
                float factor = 1f - Mathf.Clamp01(dist / MortarExplosionRadius);
                barricade.hp -= MortarMaxDamage * factor;
            }
        }

        /// <summary>DestructibleEnvironment.DestroyEnvironment (§7) : tous les murs de ce bâtiment
        /// cessent de bloquer EN MÊME TEMPS (pas de destruction progressive mur par mur), et toute
        /// unité dont la position tombe dans l'empreinte au sol (marge 1m) est écrasée (9999
        /// dégâts — mort certaine, même à travers une couverture).</summary>
        private static void DestroyBuilding(TacticalWorldState state, TacticalBuilding building, List<TacticalEvent> events)
        {
            building.destroyed = true;
            events.Add(new TacticalEvent { kind = TacticalEvent.Kind.WallDestroyed, buildingId = building.id, tick = 0 });

            if (building.footprint == null) return;
            state.grid?.OpenBuildingInterior(building.footprint);
            foreach (var unit in state.units)
            {
                if (unit.isDead) continue;
                bool inside = unit.currentBuildingId == building.id || IsInsideWithMargin(building.footprint, unit.position, 1f);
                if (!inside) continue;

                unit.health = -9999;
                unit.isDead = true;
                events.Add(new TacticalEvent { kind = TacticalEvent.Kind.Death, unitId = unit.id, position = unit.position, tick = 0 });
            }
        }

        private static bool IsInsideWithMargin(List<Vector2> footprint, Vector2 point, float margin)
        {
            if (GeometryMath.PointInPolygon(footprint, point)) return true;
            if (margin <= 0f) return false;
            // Approximation suffisante de la marge : teste aussi les 4 points décalés — évite un
            // vrai offset de polygone (coûteux et inutile ici, l'empreinte est petite face à 1m).
            return GeometryMath.PointInPolygon(footprint, point + new Vector2(margin, 0)) ||
                   GeometryMath.PointInPolygon(footprint, point + new Vector2(-margin, 0)) ||
                   GeometryMath.PointInPolygon(footprint, point + new Vector2(0, margin)) ||
                   GeometryMath.PointInPolygon(footprint, point + new Vector2(0, -margin));
        }
    }
}
