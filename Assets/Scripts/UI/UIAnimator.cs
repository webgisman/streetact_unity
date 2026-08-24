using UnityEngine;

/// <summary>
/// Petits helpers d'animation pour l'UI IMGUI du jeu (panneaux, boutons). Pas de librairie de
/// tweening externe : du simple lissage image par image avec Time.unscaledDeltaTime, cohérent avec
/// la règle anti-jitter déjà appliquée à la caméra (TacticalCamera.cs) et fonctionnant même en pause.
/// </summary>
public static class UIAnimator
{
    /// <summary>Fait avancer "current" vers "target" (0..1) à la vitesse donnée (progression par seconde).</summary>
    public static float Advance(float current, bool target, float speedPerSecond)
    {
        float goal = target ? 1f : 0f;
        return Mathf.MoveTowards(current, goal, speedPerSecond * Time.unscaledDeltaTime);
    }

    /// <summary>Rect qui apparaît en "pop" (légère mise à l'échelle depuis le centre) selon une progression 0..1.</summary>
    public static Rect PopRect(Rect target, float progress)
    {
        float scale = Mathf.Lerp(0.85f, 1f, EaseOutQuad(progress));
        float w = target.width * scale;
        float h = target.height * scale;
        float cx = target.x + target.width * 0.5f;
        float cy = target.y + target.height * 0.5f;
        return new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h);
    }

    public static float EaseOutQuad(float t) => 1f - (1f - t) * (1f - t);

    /// <summary>Doux pulse continu (0..1), utile pour un léger effet de survol/respiration.</summary>
    public static float Pulse(float speed) => (Mathf.Sin(Time.unscaledTime * speed) + 1f) * 0.5f;

    /// <summary>À appeler avant de dessiner un panneau qui doit fondre en fonction de "progress" (0..1). Restaurer avec GUI.color = Color.white ensuite.</summary>
    public static void ApplyFadeColor(float progress)
    {
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(progress));
    }
}
