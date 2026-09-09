using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Novgov.TacticalCore;

public partial class TacticalPathManager
{
    // ==========================================
    // TRACÉ DES TRAJECTOIRES (LineRenderer par unité, "battement de cœur", prévisualisation)
    // ==========================================

    private UnityEngine.AI.NavMeshPath cachedNavPath;
    private static Gradient selectedGradient;
    private static Gradient normalGradient;
    /// <summary>Dégradé « budget dépassé » réutilisé d'un redessin à l'autre — voir BuildBudgetGradient.</summary>
    private static Gradient budgetGradient;

    // Fréquence et amplitude du "battement de cœur" des lignes de trajectoire.
    private const float HEARTBEAT_FREQUENCY = 5.24f; // ~1,2s par battement
    private const float HEARTBEAT_AMPLITUDE = 0.35f;

    private void AnimateTacticalLinesHeartbeat()
    {
        // Pic bref suivi d'un repos (sin élevé à une puissance impaire) plutôt qu'une simple
        // respiration sinusoïdale continue : ça se lit comme un pouls, pas comme un néon qui clignote.
        float pulse = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(Time.time * HEARTBEAT_FREQUENCY)), 4f);
        float widthMultiplier = 1f + pulse * HEARTBEAT_AMPLITUDE;

        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            UnitAI unitAI = UnitAI.AllLivingUnits[i];
            if (unitAI == null || unitAI.tacticalLineRenderer == null) continue;
            if (unitAI.tacticalLineRenderer.positionCount == 0) continue;

            unitAI.tacticalLineRenderer.widthMultiplier = widthMultiplier;
        }
    }

    private void DessinerTousLesChemins()
    {
        if (phaseActuelle != GamePhase.Planification && phaseActuelle != GamePhase.CreationPath && phaseActuelle != GamePhase.Execution)
        {
            for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
            {
                UnitAI u = UnitAI.AllLivingUnits[i];
                if (u != null && u.tacticalLineRenderer != null) u.tacticalLineRenderer.positionCount = 0;
            }
            if (lineRenderer != null) lineRenderer.positionCount = 0;
            return;
        }

        if (selectedGradient == null)
        {
            selectedGradient = new Gradient();
            selectedGradient.SetKeys(
                new GradientColorKey[] { new GradientColorKey(new Color(0f, 0.95f, 1f), 0.0f), new GradientColorKey(new Color(0f, 0.45f, 1f), 1.0f) },
                new GradientAlphaKey[] { new GradientAlphaKey(1.0f, 0.0f), new GradientAlphaKey(0.85f, 1.0f) }
            );

            normalGradient = new Gradient();
            normalGradient.SetKeys(
                new GradientColorKey[] { new GradientColorKey(new Color(0.2f, 0.7f, 1f), 0.0f), new GradientColorKey(new Color(0.1f, 0.35f, 0.8f), 1.0f) },
                new GradientAlphaKey[] { new GradientAlphaKey(0.65f, 0.0f), new GradientAlphaKey(0.40f, 1.0f) }
            );
        }

        // GRILLE D'APERÇU CONSTRUITE UNE SEULE FOIS PAR PASSE (correctif 2026-09-04).
        //
        // AppendGridPathSegment appelait TacticalGridBuilder.BuildFromScene() une fois par SEGMENT
        // tracé — donc une fois par nœud et par unité, plus une fois pour l'aperçu. Le commentaire
        // de la méthode affirmait que l'appel était « bon marché » grâce au cache interne : c'est
        // faux. Même sur son chemin de cache valide, BuildFromScene fait un
        // FindAnyObjectByType<CityGenerator>() puis DEUX passes sur BuildingStructure.AllBuildings
        // (236 bâtiments sur la carte de référence) avec un GetComponent<DestructibleEnvironment>()
        // à chaque tour, et réalloue autant d'objets TacticalBuilding plus un TacticalWorldState.
        // Avec 4 unités de 2-3 nœuds, un seul redessin payait une dizaine de reconstructions.
        // Une passe de tracé n'a besoin que de la GRILLE, et elle ne change pas en cours de passe.
        TacticalGrid previewGrid = null;
        if (Novgov.Network.MultiplayerMatchController.IsActive)
        {
            previewGrid = TacticalGridBuilder.BuildFromScene().grid;
        }

        for (int uIdx = 0; uIdx < UnitAI.AllLivingUnits.Count; uIdx++)
        {
            UnitAI unitAI = UnitAI.AllLivingUnits[uIdx];
            if (unitAI == null || unitAI.isDead || !unitAI.isPlayerControlled)
            {
                if (unitAI != null && unitAI.tacticalLineRenderer != null) unitAI.tacticalLineRenderer.positionCount = 0;
                continue;
            }

            bool isSelected = (uniteSelectionnee == unitAI.gameObject);
            bool hasPath = (unitAI.tacticalPath.Count > 0);

            if (!hasPath && !isSelected)
            {
                if (unitAI.tacticalLineRenderer != null) unitAI.tacticalLineRenderer.positionCount = 0;
                continue;
            }

            if (unitAI.tacticalLineRenderer == null)
            {
                GameObject lineGo = new GameObject("TacticalLine_" + unitAI.name);
                lineGo.transform.SetParent(unitAI.transform, false);
                lineGo.layer = 0; // Layer par défaut (toujours visible pour Camera 2D et 3D)
                unitAI.tacticalLineRenderer = lineGo.AddComponent<LineRenderer>();

                Material lineMat = SafeMaterialFactory.CreateUnlit(Color.white);
                lineMat.enableInstancing = true;
                unitAI.tacticalLineRenderer.sharedMaterial = lineMat;
            }

            LineRenderer lr = unitAI.tacticalLineRenderer;
            lr.startWidth = isSelected ? 0.42f : 0.22f;
            lr.endWidth = isSelected ? 0.20f : 0.10f;

            // Recalculer le chemin uniquement s'il est marqué 'dirty'
            // HasTapTarget, jamais `!= Vector3.positiveInfinity` : ce test-là est toujours vrai (voir
            // TacticalPathManager.HasTapTarget), donc le chemin de l'unité sélectionnée était
            // recalculé à chaque appel, même sans aucun tap en attente.
            if (unitAI.isPathDirty || isPathsDirty || unitAI.cachedDrawPoints.Count == 0 || (isSelected && HasTapTarget(positionClicTemporaire)))
            {
                unitAI.cachedDrawPoints.Clear();
                Vector3 positionCourante = unitAI.transform.position;
                unitAI.cachedDrawPoints.Add(positionCourante + Vector3.up * 0.2f);

                if (cachedNavPath == null) cachedNavPath = new UnityEngine.AI.NavMeshPath();
                UnityEngine.AI.NavMeshAgent agent = unitAI.GetComponent<UnityEngine.AI.NavMeshAgent>();
                int areaMask = (agent != null) ? agent.areaMask : UnityEngine.AI.NavMesh.AllAreas;

                for (int i = unitAI.GetCurrentNodeIndex(); i < unitAI.tacticalPath.Count; i++)
                {
                    Vector3 targetPos = unitAI.tacticalPath[i].position;
                    bool isNodeOnRoof = targetPos.y > 1.8f;

                    if (!isNodeOnRoof && UnityEngine.AI.NavMesh.SamplePosition(targetPos, out UnityEngine.AI.NavMeshHit hitTarget, 10f, areaMask))
                    {
                        targetPos = hitTarget.position;
                    }

                    bool isMultiplayer = Novgov.Network.MultiplayerMatchController.IsActive;

                    // 2026-09-06 : un tir de mortier n'est pas un déplacement — la ligne de visée
                    // suivait pourtant le même routage (NavMesh en solo, grille A* en multijoueur)
                    // qu'une unité qui marche, la faisant contourner les bâtiments comme si le tir
                    // devait emprunter les rues au sol. Un mortier tire au vol d'oiseau (voir
                    // UnitAI.FireMortarShell/MortarShell.Launch, aucune vérification de ligne de vue
                    // ni de chemin) : la prévisualisation doit montrer un segment direct vers la
                    // cible, jamais un détour, quel que soit le mode (solo ou multijoueur).
                    if (unitAI.tacticalPath[i].action == NodeAction.TirMortier)
                    {
                        unitAI.cachedDrawPoints.Add(targetPos + Vector3.up * 0.2f);
                        positionCourante = targetPos;
                    }
                    else if (isNodeOnRoof || positionCourante.y > 1.8f || DestructibleEnvironment.IsPositionInRubble(targetPos))
                    {
                        unitAI.cachedDrawPoints.Add(targetPos + Vector3.up * 0.2f);
                        positionCourante = targetPos;
                    }
                    else if (isMultiplayer)
                    {
                        // Brouillon différent de NavMesh (voir AppendGridPathSegment) : en
                        // multijoueur, le déplacement RÉEL est calculé par TacticalCore.Pathfinding
                        // (grille A* déterministe), jamais par le NavMeshAgent — la ligne bleue doit
                        // suivre EXACTEMENT ce même calcul, sinon elle montre un chemin que l'unité
                        // ne suit pas réellement à l'exécution (bug remonté en jeu, 2026-08-30).
                        if (!AppendGridPathSegment(previewGrid, unitAI.cachedDrawPoints, positionCourante, targetPos))
                            unitAI.cachedDrawPoints.Add(targetPos + Vector3.up * 0.2f);
                        positionCourante = targetPos;
                    }
                    else if (UnityEngine.AI.NavMesh.CalculatePath(positionCourante, targetPos, areaMask, cachedNavPath) && cachedNavPath.corners.Length > 1)
                    {
                        for (int j = 1; j < cachedNavPath.corners.Length; j++)
                        {
                            unitAI.cachedDrawPoints.Add(cachedNavPath.corners[j] + Vector3.up * 0.2f);
                        }
                        positionCourante = cachedNavPath.corners[cachedNavPath.corners.Length - 1];
                    }
                    else
                    {
                        unitAI.cachedDrawPoints.Add(targetPos + Vector3.up * 0.2f);
                        positionCourante = targetPos;
                    }
                }

                // Prévisualisation pour l'unité sélectionnée vers la position du clic temporaire.
                // HasTapTarget est indispensable ici : avec l'ancien `!= Vector3.positiveInfinity`
                // (toujours vrai), ce bloc s'exécutait sans aucune cible et finissait par pousser un
                // point INFINI dans le LineRenderer de l'unité sélectionnée — bornes de rendu
                // infinies, ligne monstrueuse partant vers l'infini, et un chemin calculé vers un
                // point hors carte à chaque redessin.
                if (isSelected && (phaseActuelle == GamePhase.Planification || phaseActuelle == GamePhase.CreationPath) && HasTapTarget(positionClicTemporaire))
                {
                    if (Novgov.Network.MultiplayerMatchController.IsActive)
                    {
                        if (!AppendGridPathSegment(previewGrid, unitAI.cachedDrawPoints, positionCourante, positionClicTemporaire))
                            unitAI.cachedDrawPoints.Add(positionClicTemporaire + Vector3.up * 0.2f);
                    }
                    else if (UnityEngine.AI.NavMesh.CalculatePath(positionCourante, positionClicTemporaire, areaMask, cachedNavPath) && cachedNavPath.corners.Length > 1)
                    {
                        for (int j = 1; j < cachedNavPath.corners.Length; j++)
                        {
                            unitAI.cachedDrawPoints.Add(cachedNavPath.corners[j] + Vector3.up * 0.2f);
                        }
                    }
                    else
                    {
                        unitAI.cachedDrawPoints.Add(positionClicTemporaire + Vector3.up * 0.2f);
                    }
                }

                unitAI.isPathDirty = false;
            }

            lr.positionCount = unitAI.cachedDrawPoints.Count;
            for (int p = 0; p < unitAI.cachedDrawPoints.Count; p++)
            {
                lr.SetPosition(p, unitAI.cachedDrawPoints[p]);
            }

            // BUDGET DE DÉPLACEMENT RENDU VISIBLE (2026-09-07).
            // Le serveur tronque le chemin au budget de l'unité et SUPPRIME tout checkpoint au-delà
            // de la coupe (TacticalResolver.TruncateToMovementBudget) : la posture finale — GUETTER,
            // garnison, SE CACHER — est donc purement et simplement annulée si elle est hors de
            // portée. Rien ne le disait au joueur : il traçait une longue ligne bleue, appuyait sur
            // FIN DE TOUR, et son unité s'arrêtait au milieu sans embuscade et sans explication.
            // La portion hors budget est maintenant tracée en rouge : elle se lit immédiatement
            // comme "pas ce tour-ci".
            // Un tir de mortier trace une ligne de visee a vol d'oiseau, pas un deplacement (et le
            // serveur l'extrait des checkpoints avant toute troncature, voir BuildUnitOrdersPure) :
            // pour ces ordres-la le trace ne represente pas une distance parcourue, donc pas de
            // coloration de budget.
            bool hasMortarShot = false;
            for (int n = 0; n < unitAI.tacticalPath.Count; n++)
            {
                if (unitAI.tacticalPath[n].action == NodeAction.TirMortier) { hasMortarShot = true; break; }
            }

            lr.colorGradient = hasMortarShot
                ? (isSelected ? selectedGradient : normalGradient)
                : BuildBudgetGradient(unitAI.cachedDrawPoints, MovementBudgetOf(unitAI), isSelected);
        }
        isPathsDirty = false;
    }

    /// <summary>Budget de déplacement d'un tour, en mètres. Lit le barème AUTORITAIRE du serveur
    /// plutôt que d'en recopier les valeurs : un aperçu calé sur une table divergente recréerait
    /// exactement le bug qu'il est censé rendre visible.</summary>
    private static float MovementBudgetOf(UnitAI unit) => Novgov.Server.UnitTypeStats.MovementBudgetFor(unit);

    /// <summary>Dégradé qui bascule au rouge exactement là où le budget du tour est épuisé. Le
    /// dégradé d'un LineRenderer se répartit sur la LONGUEUR de la ligne, donc la bascule est
    /// placée à la fraction "distance du budget / longueur totale tracée".</summary>
    private static Gradient BuildBudgetGradient(List<Vector3> points, float budgetMeters, bool isSelected)
    {
        // Distance AU SOL (XZ) uniquement, comme TruncateToMovementBudget côté serveur, qui mesure en
        // Vector2 sur l'empreinte au sol. Compter la composante verticale gonflait le total d'autant
        // que le bâtiment était haut (une escalade sur un toit de 9 m ajoutait ~9 m fictifs), donc la
        // coupe rouge était dessinée PLUS LOIN que la vraie : l'aperçu promettait au joueur plus de
        // chemin qu'il n'en aura — précisément l'erreur que cette coloration existe pour éviter.
        float total = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            float dx = points[i].x - points[i - 1].x;
            float dz = points[i].z - points[i - 1].z;
            total += Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Tout tient dans le budget : dégradé normal, aucune alerte à afficher.
        if (total <= budgetMeters || total < 0.01f) return isSelected ? selectedGradient : normalGradient;

        float cut = Mathf.Clamp01(budgetMeters / total);
        Color reachable = isSelected ? new Color(0f, 0.95f, 1f) : new Color(0.2f, 0.7f, 1f);
        Color unreachable = new Color(1f, 0.25f, 0.2f);
        float alpha = isSelected ? 1.0f : 0.65f;

        // Bascule NETTE (deux clés très rapprochées) plutôt qu'un fondu : c'est une limite précise,
        // pas une zone d'incertitude.
        // Instance RÉUTILISÉE (jamais un `new Gradient()` par unité et par redessin) : ce tracé est
        // déjà un point chaud connu sur mobile — voir la note sur BuildFromScene plus haut — et la
        // phase CreationPath le rappelle à chaque frame. LineRenderer.colorGradient copie les clés
        // à l'affectation, donc réutiliser l'objet est sans effet de bord.
        Gradient g = budgetGradient ?? (budgetGradient = new Gradient());
        g.SetKeys(
            new GradientColorKey[]
            {
                new GradientColorKey(reachable, 0f),
                new GradientColorKey(reachable, Mathf.Max(0f, cut - 0.001f)),
                new GradientColorKey(unreachable, Mathf.Min(1f, cut + 0.001f)),
                new GradientColorKey(unreachable, 1f),
            },
            new GradientAlphaKey[] { new GradientAlphaKey(alpha, 0f), new GradientAlphaKey(alpha * 0.85f, 1f) }
        );
        return g;
    }

    /// <summary>Ajoute à <paramref name="drawPoints"/> le chemin RÉELLEMENT calculé par
    /// TacticalCore.Pathfinding entre deux points — le même algorithme (grille A* déterministe,
    /// voir Pathfinding.cs) que celui utilisé côté serveur pour le résultat officiel du
    /// déplacement en multijoueur. Avant ce correctif, l'aperçu (ligne bleue) utilisait
    /// NavMesh.CalculatePath (précis, suit les rues) alors que l'exécution réelle suivait cette
    /// grille bien plus grossière (1m/cellule) — les deux pouvaient diverger nettement, surtout
    /// dans les virages, donnant l'impression que l'unité "ignore" le chemin dessiné (bug remonté
    /// en jeu, 2026-08-30).
    ///
    /// La grille est fournie par l'APPELANT, construite une seule fois par passe de tracé (voir
    /// DessinerTousLesChemins) : elle était auparavant reconstruite ici à chaque segment, ce que la
    /// documentation de cette méthode déclarait à tort « bon marché ».
    ///
    /// Retourne false si aucun chemin n'a été trouvé (grille absente, point hors zone, arrivée
    /// impraticable — typiquement l'intérieur d'un bâtiment) : l'appelant se rabat alors sur une
    /// ligne droite.</summary>
    private static bool AppendGridPathSegment(TacticalGrid grid, List<Vector3> drawPoints, Vector3 from, Vector3 to)
    {
        if (grid == null) return false;

        List<Vector2> gridPath = Pathfinding.FindPath(grid, new Vector2(from.x, from.z), new Vector2(to.x, to.z));
        if (gridPath.Count < 2) return false;

        for (int j = 1; j < gridPath.Count; j++)
        {
            drawPoints.Add(new Vector3(gridPath[j].x, from.y, gridPath[j].y) + Vector3.up * 0.2f);
        }
        return true;
    }

}
