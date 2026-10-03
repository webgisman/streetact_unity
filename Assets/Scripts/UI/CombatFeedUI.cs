using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Fil de combat du HUD en match (InMatchHudScreen "combat-feed", 2026-09-16) — une ligne par unité
/// détruite, en Solo comme en ligne : voir UnitAI.Die(),
/// seul point d'appel, commun au chemin temps réel (TakeDamage) et au rejeu réseau (ApplyNetworkDeath).
/// Avant ça, une unité pouvait mourir hors du champ de vision du joueur sans le moindre indice
/// (rapport utilisateur : "les joueurs ne comprennent rien, il y a des morts sans savoir pourquoi").
///
/// Purement informatif, jamais interactif (picking-mode: Ignore dans l'UXML). No-op silencieux côté
/// serveur dédié : UIScreenManager.Instance y reste toujours null (UIBootstrap ne tourne jamais côté
/// UNITY_SERVER, voir GameManagerUI.BindStartupUI pour le même garde), donc ReportDeath ne fait rien
/// sur le serveur alors même que Die() s'y exécute aussi pendant la résolution du tour.
/// </summary>
public static class CombatFeedUI
{
    private const int MaxEntries = 5;

    public static void ReportDeath(UnitAI victim)
    {
        if (victim == null) return;
        VisualElement feed = UIScreenManager.Instance?.GetScreen("InMatchHud")?.Q<VisualElement>("combat-feed");
        if (feed == null) return;

        var entry = new Label($"☠ {GetUnitDisplayLabel(victim)} détruit(e)");
        entry.AddToClassList("combat-feed-entry");
        entry.AddToClassList(victim.teamID == 1 ? "combat-feed-entry--team1" : "combat-feed-entry--team2");
        feed.Add(entry);

        while (feed.childCount > MaxEntries) feed.RemoveAt(0);
    }

    /// <summary>Même convention de détection par nom que UnitAI.Awake (isLeopard/isCanonVehicle/
    /// isMortar, "auto-détection si le prefab est posé brut sans configuration") — pas de nouvel
    /// enum/champ ajouté rien que pour ce libellé.</summary>
    private static string GetUnitDisplayLabel(UnitAI unit)
    {
        string n = unit.gameObject.name.ToLowerInvariant();
        if (n.Contains("leopard")) return "Char Leopard 2";
        if (n.Contains("canon")) return "Véhicule Canon";
        if (n.Contains("mortier") || n.Contains("mortar")) return "Mortier";
        if (n.Contains("barricade") || n.Contains("barrier")) return "Barricade";
        return "Fantassin";
    }
}
