using UnityEngine;

/// <summary>
/// Générateur de sons procéduraux 8-bit / Synthétiseur pour donner du feedback
/// audio professionnel sans avoir besoin de fichiers MP3 ou WAV.
/// </summary>
public static class ProceduralAudioBuilder
{
    private const int sampleRate = 44100;

    // --- SONS D'INTERFACE (UI) ---

    public static AudioClip CreateHoverSound()
    {
        float duration = 0.05f;
        int sampleCount = (int)(sampleRate * duration);
        float[] samples = new float[sampleCount];
        
        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            // Onde sinusoïdale aiguë très courte (style "blip")
            samples[i] = Mathf.Sin(t * Mathf.PI * 2 * 800f) * Mathf.Exp(-t * 60f) * 0.3f;
        }

        AudioClip clip = AudioClip.Create("HoverSound", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    public static AudioClip CreateClickSound()
    {
        float duration = 0.1f;
        int sampleCount = (int)(sampleRate * duration);
        float[] samples = new float[sampleCount];
        
        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            // Carré/Sinus rapide qui descend en fréquence ("pew" métallique)
            float freq = Mathf.Lerp(1200f, 400f, t / duration);
            samples[i] = Mathf.Sin(t * Mathf.PI * 2 * freq) * Mathf.Exp(-t * 30f) * 0.4f;
        }

        AudioClip clip = AudioClip.Create("ClickSound", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    public static AudioClip CreateErrorSound()
    {
        float duration = 0.3f;
        int sampleCount = (int)(sampleRate * duration);
        float[] samples = new float[sampleCount];
        
        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            // Double note grave dissonante
            samples[i] = (Mathf.Sin(t * Mathf.PI * 2 * 150f) + Mathf.Sin(t * Mathf.PI * 2 * 160f)) * Mathf.Exp(-t * 10f) * 0.3f;
        }

        AudioClip clip = AudioClip.Create("ErrorSound", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    // --- SONS DE MOUVEMENT ---

    public static AudioClip CreateFootstepSound()
    {
        float duration = 0.15f;
        int sampleCount = (int)(sampleRate * duration);
        float[] samples = new float[sampleCount];
        
        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            // Bruit blanc filtré (grave) pour simuler un impact sur le béton/terre
            float noise = Random.Range(-1f, 1f);
            float lowPass = Mathf.Sin(t * Mathf.PI * 2 * 50f); // Résonance grave
            
            samples[i] = (noise * 0.3f + lowPass * 0.7f) * Mathf.Exp(-t * 25f) * 0.2f;
        }

        AudioClip clip = AudioClip.Create("FootstepSound", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    public static AudioClip CreateTargetConfirmedSound()
    {
        float duration = 0.5f;
        int sampleCount = (int)(sampleRate * duration);
        float[] samples = new float[sampleCount];
        
        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            // Accord parfait majeur montant (C-E-G) très rapide façon "Objectif acquis"
            float freq1 = 523.25f; // C5
            float freq2 = 659.25f; // E5
            float freq3 = 783.99f; // G5
            
            float mix = 0f;
            if (t < 0.1f) mix = Mathf.Sin(t * Mathf.PI * 2 * freq1);
            else if (t < 0.2f) mix = Mathf.Sin(t * Mathf.PI * 2 * freq2);
            else mix = Mathf.Sin(t * Mathf.PI * 2 * freq3);

            samples[i] = mix * Mathf.Exp(-t * 5f) * 0.3f;
        }

        AudioClip clip = AudioClip.Create("TargetConfirmed", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
    public static AudioClip CreateGunshotSound()
    {
        float duration = 0.4f; // Plus long pour la résonance
        int sampleCount = (int)(sampleRate * duration);
        float[] samples = new float[sampleCount];
        
        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            
            // 1. Le "Crack" initial supersonique (bruit blanc très fort qui chute immédiatement)
            float crack = Random.Range(-1f, 1f) * Mathf.Exp(-t * 200f);
            
            // 2. Le "Thump" mécanique du fusil (fréquence grave qui glisse de 150hz vers 40hz)
            float thumpFreq = Mathf.Lerp(150f, 40f, t / 0.1f);
            float thump = Mathf.Sin(t * Mathf.PI * 2 * thumpFreq) * Mathf.Exp(-t * 40f);
            
            // 3. L'écho / Détonation de poudre (bruit avec descente lente)
            float echo = Random.Range(-1f, 1f) * Mathf.Exp(-t * 15f) * 0.3f;

            // Mélange
            float mix = crack + thump * 1.5f + echo;
            
            // 4. Hard clipping (Saturation numérique) pour donner l'impression d'un volume extrême et explosif
            mix = Mathf.Clamp(mix * 3.0f, -0.95f, 0.95f);

            samples[i] = mix;
        }

        AudioClip clip = AudioClip.Create("Gunshot", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
