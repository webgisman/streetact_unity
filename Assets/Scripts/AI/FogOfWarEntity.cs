using UnityEngine;

/// <summary>
/// Gère la visibilité des unités (Fog of War désactivé : toutes les unités sont visibles à 100%).
/// </summary>
public class FogOfWarEntity : MonoBehaviour
{
    public UnitAI unitAI;
    public bool isVisibleToPlayer => true;

    void Start()
    {
        if (unitAI == null) unitAI = GetComponent<UnitAI>();
        
        if (unitAI != null)
        {
            unitAI.isVisible = true;
            unitAI.SetVisualsVisibility(true);
        }
    }

    void Update()
    {
        // Visibilité totale permanente
        if (unitAI != null && !unitAI.isVisible)
        {
            unitAI.isVisible = true;
            unitAI.SetVisualsVisibility(true);
        }
    }

    public void SetVisibility(bool visible)
    {
        if (unitAI != null)
        {
            unitAI.isVisible = true;
            unitAI.SetVisualsVisibility(true);
        }
    }
    
    /// <summary>
    /// Déclenché quand l'unité fait feu : signale un tir en rouge sur le radar pendant 3.2s
    /// </summary>
    public void NotifyAttack()
    {
        if (TacticalRadarUI.Instance != null)
        {
            TacticalRadarUI.Instance.PingAttack(transform.position);
        }
    }
}
