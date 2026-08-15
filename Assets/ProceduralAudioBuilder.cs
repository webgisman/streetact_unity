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
        // Boucle de marche cadencée réaliste (deux pas par seconde avec impact et silence)
        float duration = 0.65f;
        int sampleCount = (int)(sampleRate * duration);
        float[] samples = new float[sampleCount];
        
        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            float step1 = Mathf.Max(0f, 1f - (t / 0.12f));
            float step2 = Mathf.Max(0f, 1f - (Mathf.Abs(t - 0.32f) / 0.12f));
            
            float noise = Random.Range(-0.8f, 0.8f);
            float lowRumble = Mathf.Sin(t * Mathf.PI * 2 * 65f);
            
            float val = 0f;
            if (t < 0.15f) val = (noise * 0.4f + lowRumble * 0.6f) * Mathf.Exp(-t * 22f) * 0.35f;
            else if (t >= 0.32f && t < 0.47f)
            {
                float tRel = t - 0.32f;
                val = (noise * 0.4f + lowRumble * 0.6f) * Mathf.Exp(-tRel * 22f) * 0.32f;
            }
            samples[i] = Mathf.Clamp(val, -1f, 1f);
        }

        AudioClip clip = AudioClip.Create("FootstepSound", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    public static AudioClip CreateVehicleEngineSound()
    {
        // Boucle de grondement de moteur diesel lourd et chenilles blindées
        float duration = 1.0f;
        int sampleCount = (int)(sampleRate * duration);
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            float enginePiston = Mathf.Sin(t * Mathf.PI * 2 * 45f) * 0.4f 
                               + Mathf.Sin(t * Mathf.PI * 2 * 90f) * 0.25f
                               + Mathf.Sin(t * Mathf.PI * 2 * 135f) * 0.15f;
            float trackRumble = Random.Range(-0.15f, 0.15f);
            samples[i] = Mathf.Clamp((enginePiston + trackRumble) * 0.45f, -1f, 1f);
        }

        AudioClip clip = AudioClip.Create("VehicleEngineSound", sampleCount, 1, sampleRate, false);
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

    public static AudioClip CreateMortarLaunchSound()
    {
        float duration = 0.8f;
        int sampleCount = (int)(sampleRate * duration);
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            // 1. Coup sourd de tube (Thump)
            float freq = Mathf.Lerp(90f, 30f, t / 0.3f);
            float tubeThump = Mathf.Sin(t * Mathf.PI * 2 * freq) * Mathf.Exp(-t * 12f) * 1.8f;

            // 2. Souffle de gaz comprimé (Whoosh)
            float gasNoise = Random.Range(-1f, 1f) * Mathf.Exp(-t * 6f) * 0.5f;

            float mix = Mathf.Clamp((tubeThump + gasNoise) * 2.0f, -0.95f, 0.95f);
            samples[i] = mix;
        }

        AudioClip clip = AudioClip.Create("MortarLaunch", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    public static AudioClip CreateMortarExplosionSound()
    {
        float duration = 1.5f;
        int sampleCount = (int)(sampleRate * duration);
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            // 1. Onde de choc initiale
            float shock = Random.Range(-1f, 1f) * Mathf.Exp(-t * 25f) * 2.0f;

            // 2. Grondement sub-bass lourd (45Hz -> 20Hz)
            float subBass = Mathf.Sin(t * Mathf.PI * 2 * Mathf.Lerp(45f, 20f, t / duration)) * Mathf.Exp(-t * 3f) * 1.5f;

            // 3. Débris et réverbération urbaine
            float rumble = Random.Range(-0.8f, 0.8f) * Mathf.Exp(-t * 2f) * 0.6f;

            float mix = Mathf.Clamp((shock + subBass + rumble) * 2.5f, -0.95f, 0.95f);
            samples[i] = mix;
        }

        AudioClip clip = AudioClip.Create("MortarExplosion", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
