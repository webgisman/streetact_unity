using UnityEngine;

/// <summary>
/// orthographicSize ne contrôle QUE la demi-hauteur visible d'une caméra orthographique — la
/// largeur visible = orthographicSize * 2 * aspect. Une valeur d'orthographicSize calibrée à
/// l'œil sur un aspect de référence (ex: 16:9 en éditeur) affiche donc mécaniquement MOINS de
/// largeur de carte sur un téléphone en portrait (aspect très inférieur à 1), coupant les
/// bâtiments/rues sur les côtés. Ce utilitaire recalcule l'orthographicSize nécessaire pour
/// conserver la même largeur de monde visible que sur l'aspect de référence, quel que soit
/// l'aspect réel de l'appareil.
/// </summary>
public static class OrthographicAspectFit
{
    public static float GetCorrectedOrthoSize(float baseOrthoSize, float referenceAspect, float currentAspect)
    {
        float desiredWorldWidth = baseOrthoSize * 2f * referenceAspect;
        return desiredWorldWidth / (2f * currentAspect);
    }

    public static float CurrentScreenAspect() => (float)Screen.width / Screen.height;
}
