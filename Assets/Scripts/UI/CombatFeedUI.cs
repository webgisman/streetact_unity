using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Fil de combat du HUD : une ligne par unité détruite, en Solo comme en ligne (appelé par
/// UnitAI.Die). Sans effet sur le serveur (pas d'UIScreenManager).
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
