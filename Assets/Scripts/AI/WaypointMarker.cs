using UnityEngine;

/// <summary>
/// Marqueur de destination holographique (Sci-Fi/Tactique) généré par code.
/// Tourne sur lui-même et pulse au rythme d'un sinus.
/// </summary>
public class WaypointMarker : MonoBehaviour
{
    public bool isArtilleryTarget = false;

    // ------------------------------------------------------------------------------------------
    // RATTACHEMENT À L'UNITÉ ET AU NŒUD (2026-09-07)
    // Un marqueur était jusqu'ici un GameObject anonyme, sans aucun lien de retour vers l'unité ni
    // vers le point de trajet qu'il illustre : il n'était détruit qu'EN MASSE au lancement du tour
    // (TacticalPathManager_Execution). Annuler un checkpoint raccourcissait donc bien la ligne bleue
    // mais laissait son hologramme en place jusqu'à la fin du tour — le joueur croyait son point
    // toujours posé et replanifiait autour d'un ordre fantôme.
    /// <summary>Unité dont ce marqueur illustre un point de trajet (null = marqueur libre).</summary>
    public UnitAI owner;
    /// <summary>Index, dans owner.tacticalPath, du nœud illustré au moment de la création.</summary>
    public int nodeIndex = -1;

    /// <summary>Tous les marqueurs vivants — même idiome que BuildingStructure.AllBuildings, pour
    /// retrouver ceux d'une unité sans balayer la scène entière.</summary>
    public static readonly System.Collections.Generic.List<WaypointMarker> AllMarkers = new System.Collections.Generic.List<WaypointMarker>();

    /// <summary>Détruit les marqueurs de <paramref name="unit"/> dont le nœud n'existe plus (index
    /// supérieur ou égal à <paramref name="fromNodeIndex"/>). Passer 0 les enlève tous.</summary>
    public static void DestroyMarkersOf(UnitAI unit, int fromNodeIndex)
    {
        if (unit == null) return;
        for (int i = AllMarkers.Count - 1; i >= 0; i--)
        {
            WaypointMarker m = AllMarkers[i];
            if (m == null) { AllMarkers.RemoveAt(i); continue; }
            if (m.owner == unit && m.nodeIndex >= fromNodeIndex) Destroy(m.gameObject);
        }
    }

    void OnEnable()
    {
        if (!AllMarkers.Contains(this)) AllMarkers.Add(this);
        if (owner != null) hadOwner = true;
    }
    void OnDisable() { AllMarkers.Remove(this); }

    /// <summary>Ce marqueur a-t-il été rattaché à une unité à un moment ? Distingue « orphelin »
    /// (unité détruite) de « volontairement libre » — voir Update.</summary>
    private bool hadOwner = false;

    private float timeAlive = 0f;
    private Material holoMat;
    private Color baseColor = new Color(0f, 0.8f, 1f, 0.6f);

    void Start()
    {
        float ringRadius = isArtilleryTarget ? 6.5f : 1.5f;
        baseColor = isArtilleryTarget ? new Color(1f, 0.35f, 0.05f, 0.75f) : new Color(0f, 0.8f, 1f, 0.6f);

        // 1. Création d'un disque visuel holographique au sol
        GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.transform.SetParent(this.transform, false);
        ring.transform.localPosition = new Vector3(0, 0.15f, 0); // Au ras du sol
        ring.transform.localScale = new Vector3(ringRadius, 0.02f, ringRadius);
        ring.layer = 0; // Layer Default visible dans toutes les caméras
        
        Destroy(ring.GetComponent<Collider>()); // Pas de physique

        // 2. Création d'un Material Additif (Hologramme)
        holoMat = SafeMaterialFactory.CreateUnlit(baseColor);
        ring.GetComponent<MeshRenderer>().sharedMaterial = holoMat;

        // Son de confirmation d'objectif
        AudioSource audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 0.5f;
        audioSource.clip = ProceduralAudioBuilder.CreateTargetConfirmedSound();
        audioSource.Play();
        
        // Plus d'auto-destruction temporisée (2026-09-07). Elle valait 60 s, alors que la phase de
        // planification en dure désormais 300 (PlanningSeconds, relevé le même jour) et que le
        // compte à rebours n'est plus affiché : un joueur qui réfléchit voyait ses marqueurs
        // disparaître un par un alors que les nœuds correspondants existaient toujours — l'exact
        // inverse du marqueur fantôme que le rattachement owner/nodeIndex vient de supprimer.
        // Le cycle de vie est maintenant entièrement explicite : détruit avec son nœud
        // (UnitAI.RemoveLastTacticalNode/ClearTacticalPath) ou au lancement du tour
        // (TacticalPathManager_Execution).
    }

    void Update()
    {
        // Marqueur ORPHELIN : son unité a été détruite (mort en cours de tour). DestroyMarkersOf ne
        // peut plus l'atteindre — il est indexé par une référence que Unity rapporte désormais comme
        // null — et il n'y a plus d'auto-destruction temporisée pour le ramasser. On se retire donc
        // nous-mêmes. `owner != null` à la création est ce qui distingue un vrai orphelin d'un
        // marqueur volontairement libre (owner jamais renseigné).
        if (hadOwner && owner == null)
        {
            Destroy(gameObject);
            return;
        }

        timeAlive += Time.deltaTime;

        // Effet de rotation continue
        transform.Rotate(Vector3.up, (isArtilleryTarget ? 90f : 180f) * Time.deltaTime);

        // Effet de pulsation (Scale)
        float scale = 1f + Mathf.Sin(timeAlive * 8f) * 0.08f;
        transform.localScale = new Vector3(scale, 1f, scale);

        // Effet de clignotement (Alpha)
        if (holoMat != null)
        {
            float alpha = (isArtilleryTarget ? 0.5f : 0.4f) + Mathf.Sin(timeAlive * 15f) * 0.3f;
            Color c = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
            holoMat.SetColor("_BaseColor", c);
            if (holoMat.HasProperty("_Color")) holoMat.SetColor("_Color", c);
        }
    }
}
