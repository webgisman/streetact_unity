using UnityEngine;

/// <summary>
/// Fondu doux (alpha) pour un panneau uGUI (LoadingPanel/ErrorPanel), à la place d'un SetActive brut.
/// Ajoute son propre CanvasGroup si besoin — aucune modification de la scène nécessaire.
/// </summary>
public class CanvasGroupFader : MonoBehaviour
{
    public float fadeSpeedPerSecond = 6f;

    private CanvasGroup group;
    private bool targetVisible;

    private void EnsureGroup()
    {
        if (group != null) return;
        group = GetComponent<CanvasGroup>();
        if (group == null) group = gameObject.AddComponent<CanvasGroup>();
    }

    public void SetVisible(bool visible)
    {
        EnsureGroup();
        targetVisible = visible;
        if (visible)
        {
            gameObject.SetActive(true);
        }
    }

    private void Update()
    {
        EnsureGroup();
        float goal = targetVisible ? 1f : 0f;
        group.alpha = Mathf.MoveTowards(group.alpha, goal, fadeSpeedPerSecond * Time.unscaledDeltaTime);
        group.interactable = targetVisible;
        group.blocksRaycasts = targetVisible;

        if (!targetVisible && group.alpha <= 0.001f && gameObject.activeSelf)
        {
            gameObject.SetActive(false);
        }
    }

    /// <summary>Affiche/masque "panel" en fondu, en ajoutant le fader au besoin. Aucun risque de casser
    /// les call-sites existants qui faisaient un SetActive brut : même effet final, juste animé.</summary>
    public static void SetVisible(GameObject panel, bool visible)
    {
        if (panel == null) return;
        CanvasGroupFader fader = panel.GetComponent<CanvasGroupFader>();
        if (fader == null) fader = panel.AddComponent<CanvasGroupFader>();
        fader.SetVisible(visible);
    }
}
