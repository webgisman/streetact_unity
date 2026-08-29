using UnityEngine;
using UnityEngine.InputSystem;

public partial class TacticalPathManager
{
    // ==========================================
    // TRACÉ DES TRAJECTOIRES (LineRenderer par unité, "battement de cœur", prévisualisation)
    // ==========================================

    private UnityEngine.AI.NavMeshPath cachedNavPath;
    private static Gradient selectedGradient;
    private static Gradient normalGradient;

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
            lr.colorGradient = isSelected ? selectedGradient : normalGradient;

            // Recalculer le chemin uniquement s'il est marqué 'dirty'
            if (unitAI.isPathDirty || isPathsDirty || unitAI.cachedDrawPoints.Count == 0 || (isSelected && positionClicTemporaire != Vector3.positiveInfinity))
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

                    if (isNodeOnRoof || positionCourante.y > 1.8f || DestructibleEnvironment.IsPositionInRubble(targetPos))
                    {
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

                // Prévisualisation pour l'unité sélectionnée vers la position du clic temporaire
                if (isSelected && (phaseActuelle == GamePhase.Planification || phaseActuelle == GamePhase.CreationPath) && positionClicTemporaire != Vector3.positiveInfinity)
                {
                    if (UnityEngine.AI.NavMesh.CalculatePath(positionCourante, positionClicTemporaire, areaMask, cachedNavPath) && cachedNavPath.corners.Length > 1)
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
        }
        isPathsDirty = false;
    }

    private Vector3 GetMousePositionOnNavMesh()
    {
        if (Pointer.current == null) return Vector3.zero;

        Ray ray = Camera.main.ScreenPointToRay(Pointer.current.position.ReadValue());
        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            UnityEngine.AI.NavMeshHit navHit;
            if (UnityEngine.AI.NavMesh.SamplePosition(hit.point, out navHit, 1.0f, UnityEngine.AI.NavMesh.AllAreas))
                return navHit.position;
        }
        return Vector3.zero;
    }
}
