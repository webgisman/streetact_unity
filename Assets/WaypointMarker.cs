using UnityEngine;

/// <summary>
/// Marqueur de destination holographique (Sci-Fi/Tactique) généré par code.
/// Tourne sur lui-même et pulse au rythme d'un sinus.
/// </summary>
public class WaypointMarker : MonoBehaviour
{
    private float timeAlive = 0f;
    private Material holoMat;

    void Start()
    {
        // 1. Création d'un anneau visuel simple (Cylindre aplati)
        GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.transform.SetParent(this.transform);
        ring.transform.localPosition = new Vector3(0, 0.1f, 0); // Légèrement au-dessus du sol
        ring.transform.localScale = new Vector3(1.5f, 0.05f, 1.5f);
        
        Destroy(ring.GetComponent<Collider>()); // Pas de physique

        // 2. Création d'un Material Additif (Hologramme)
        // On cherche le shader URP Unlit, ou Standard si introuvable
        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null) unlitShader = Shader.Find("Unlit/Transparent");
        
        holoMat = new Material(unlitShader);
        
        // Configuration de transparence Additive par le code (si possible sur ce shader)
        holoMat.SetColor("_BaseColor", new Color(0f, 0.8f, 1f, 0.6f)); // Cyan transparent
        if (holoMat.HasProperty("_Color")) holoMat.SetColor("_Color", new Color(0f, 0.8f, 1f, 0.6f));
        
        // Rendre transparent
        holoMat.SetFloat("_Surface", 1); // Transparent in URP
        holoMat.SetFloat("_Blend", 0); // Alpha
        
        ring.GetComponent<MeshRenderer>().sharedMaterial = holoMat;

        // Son de confirmation d'objectif généré
        AudioSource audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.spatialBlend = 1f; // 3D sound
        audioSource.clip = ProceduralAudioBuilder.CreateTargetConfirmedSound();
        audioSource.Play();
        
        // Détruire après 5 secondes pour ne pas polluer la scène si l'unité n'arrive pas
        Destroy(gameObject, 5f);
    }

    void Update()
    {
        timeAlive += Time.deltaTime;

        // Effet de rotation continue
        transform.Rotate(Vector3.up, 180f * Time.deltaTime);

        // Effet de pulsation (Scale)
        float scale = 1f + Mathf.Sin(timeAlive * 8f) * 0.1f;
        transform.localScale = new Vector3(scale, 1f, scale);

        // Effet de clignotement (Alpha)
        if (holoMat != null)
        {
            float alpha = 0.4f + Mathf.Sin(timeAlive * 15f) * 0.3f;
            Color c = new Color(0f, 0.8f, 1f, alpha);
            holoMat.SetColor("_BaseColor", c);
            if (holoMat.HasProperty("_Color")) holoMat.SetColor("_Color", c);
        }
    }
}
