using UnityEngine;

/// <summary>
/// Marqueur de destination holographique (Sci-Fi/Tactique) généré par code.
/// Tourne sur lui-même et pulse au rythme d'un sinus.
/// </summary>
public class WaypointMarker : MonoBehaviour
{
    public bool isArtilleryTarget = false;
    private float timeAlive = 0f;
    private Material holoMat;
    private Color baseColor = new Color(0f, 0.8f, 1f, 0.6f);

    void Start()
    {
        float ringRadius = isArtilleryTarget ? 6.5f : 1.5f;
        baseColor = isArtilleryTarget ? new Color(1f, 0.35f, 0.05f, 0.75f) : new Color(0f, 0.8f, 1f, 0.6f);

        // 1. Création d'un anneau visuel (Cylindre aplati)
        GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.transform.SetParent(this.transform);
        ring.transform.localPosition = new Vector3(0, 0.1f, 0); // Légèrement au-dessus du sol
        ring.transform.localScale = new Vector3(ringRadius, 0.05f, ringRadius);
        
        Destroy(ring.GetComponent<Collider>()); // Pas de physique

        // 2. Création d'un Material Additif (Hologramme)
        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Transparent");
        holoMat = new Material(unlitShader);
        
        holoMat.SetColor("_BaseColor", baseColor);
        if (holoMat.HasProperty("_Color")) holoMat.SetColor("_Color", baseColor);
        
        holoMat.SetFloat("_Surface", 1); // Transparent in URP
        holoMat.SetFloat("_Blend", 0); // Alpha
        
        ring.GetComponent<MeshRenderer>().sharedMaterial = holoMat;

        // Son de confirmation d'objectif
        AudioSource audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 1f;
        audioSource.clip = ProceduralAudioBuilder.CreateTargetConfirmedSound();
        audioSource.Play();
        
        Destroy(gameObject, 6f);
    }

    void Update()
    {
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
