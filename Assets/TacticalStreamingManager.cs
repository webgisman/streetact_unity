using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Gestionnaire haute performance du Streaming 3D (Zéro Garbage Collection).
/// Pré-enregistre tous les MeshRenderers 3D dans des structures mémoires contiguës
/// et n'allume que les bâtiments dans le rayon immédiat (32m) de l'unité active.
/// </summary>
public class TacticalStreamingManager : MonoBehaviour
{
    public static TacticalStreamingManager Instance;

    public class CachedBuildingChunk
    {
        public Vector3 position;
        public MeshRenderer[] renderers3D;
        public bool isCurrentlyVisible;
    }

    [Header("Streaming Settings")]
    [Tooltip("Rayon étendu pour la vue 3D garantissant une vision nette et complète de la ville")]
    public float streamingRadius = 260f;
    [Tooltip("Fréquence de vérification du streaming en secondes (pour sauver le CPU)")]
    public float updateInterval = 0.35f;

    private Transform currentFocusTarget = null;
    private bool isStreamingActive = false;

    private readonly List<CachedBuildingChunk> cachedChunks = new List<CachedBuildingChunk>();
    private Coroutine streamingCoroutine;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    public void RegisterAllBuildings()
    {
        cachedChunks.Clear();
        BuildingStructure[] allBuildings = FindObjectsByType<BuildingStructure>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        
        foreach (var building in allBuildings)
        {
            if (building == null) continue;

            MeshRenderer[] allRends = building.GetComponentsInChildren<MeshRenderer>(true);
            List<MeshRenderer> rends3DList = new List<MeshRenderer>();
            
            foreach (var r in allRends)
            {
                if (r != null && r.gameObject.name != "Footprint_2D")
                {
                    r.enabled = false;
                    rends3DList.Add(r);
                }
            }

            Vector3 bPos = building.centroid;
            if (bPos == Vector3.zero)
            {
                building.ComputeCentroidAndBounds();
                bPos = building.centroid;
            }
            if (bPos == Vector3.zero)
            {
                bPos = building.transform.position;
            }

            cachedChunks.Add(new CachedBuildingChunk
            {
                position = bPos,
                renderers3D = rends3DList.ToArray(),
                isCurrentlyVisible = false
            });
        }

        Debug.Log($"<color=green>[Streaming] {cachedChunks.Count} bâtiments pré-cachés avec leurs vrais centroïdes (Zéro GC allocation).</color>");
    }

    public void StartStreamingOnTarget(Transform target)
    {
        currentFocusTarget = target;
        isStreamingActive = true;

        if (cachedChunks.Count == 0) RegisterAllBuildings();

        if (streamingCoroutine != null) StopCoroutine(streamingCoroutine);
        streamingCoroutine = StartCoroutine(StreamingLoop());

        UpdateStreamingChunks();
    }

    public void StopStreamingAndClear()
    {
        isStreamingActive = false;
        currentFocusTarget = null;
        if (streamingCoroutine != null) StopCoroutine(streamingCoroutine);

        for (int i = 0; i < cachedChunks.Count; i++)
        {
            var chunk = cachedChunks[i];
            if (chunk.isCurrentlyVisible)
            {
                for (int r = 0; r < chunk.renderers3D.Length; r++)
                {
                    if (chunk.renderers3D[r] != null) chunk.renderers3D[r].enabled = false;
                }
                chunk.isCurrentlyVisible = false;
            }
        }
    }

    private IEnumerator StreamingLoop()
    {
        var wait = new WaitForSeconds(updateInterval);
        while (isStreamingActive)
        {
            UpdateStreamingChunks();
            yield return wait;
        }
    }

    private void UpdateStreamingChunks()
    {
        Vector3 targetPos = Vector3.zero;
        if (currentFocusTarget != null)
        {
            targetPos = currentFocusTarget.position;
        }
        else if (TacticalCamera.Instance != null)
        {
            targetPos = TacticalCamera.Instance.focusPosition;
        }
        else
        {
            return;
        }

        float sqrRadius = streamingRadius * streamingRadius;

        for (int i = 0; i < cachedChunks.Count; i++)
        {
            var chunk = cachedChunks[i];
            float dx = chunk.position.x - targetPos.x;
            float dz = chunk.position.z - targetPos.z;
            float sqrDist = dx * dx + dz * dz;

            bool shouldBeVisible = sqrDist <= sqrRadius;

            if (chunk.isCurrentlyVisible != shouldBeVisible)
            {
                chunk.isCurrentlyVisible = shouldBeVisible;
                for (int r = 0; r < chunk.renderers3D.Length; r++)
                {
                    if (chunk.renderers3D[r] != null) chunk.renderers3D[r].enabled = shouldBeVisible;
                }
            }
        }
    }
}
