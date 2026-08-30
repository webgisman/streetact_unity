using UnityEngine;

/// <summary>
/// Gère la visibilité des unités. En solo, le Fog of War est désactivé (toutes les unités sont
/// visibles à 100%) — cette classe force alors la visibilité en continu. En multijoueur, le VRAI
/// brouillard de guerre est calculé côté serveur (voir MatchSessionManager.ComputeVisibleUnitIds) et
/// appliqué unité par unité par MultiplayerMatchController.PlaySnapshotsCoroutine — cette classe ne
/// doit alors JAMAIS re-forcer une unité ennemie à "visible", sous peine d'annuler ce calcul dès la
/// frame suivante (voir Novgov.Network.MultiplayerMatchController.IsActive, vrai uniquement pendant
/// une partie multijoueur en cours).
/// </summary>
public class FogOfWarEntity : MonoBehaviour
{
    public UnitAI unitAI;
    public bool isVisibleToPlayer => true;

    void Start()
    {
        if (unitAI == null) unitAI = GetComponent<UnitAI>();

        if (unitAI != null && !Novgov.Network.MultiplayerMatchController.IsActive)
        {
            unitAI.isVisible = true;
            unitAI.SetVisualsVisibility(true);
        }
    }

    void Update()
    {
        if (Novgov.Network.MultiplayerMatchController.IsActive) return;

        // Visibilité totale permanente (Solo uniquement)
        if (unitAI != null && !unitAI.isVisible)
        {
            unitAI.isVisible = true;
            unitAI.SetVisualsVisibility(true);
        }
    }

    public void SetVisibility(bool visible)
    {
        if (Novgov.Network.MultiplayerMatchController.IsActive) return;
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
