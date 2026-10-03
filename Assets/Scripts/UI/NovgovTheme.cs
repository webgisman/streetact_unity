using UnityEngine;

/// <summary>
/// Miroir C# des tokens de couleur de Assets/UI/Theme.tss, pour le code qui construit de l'UI
/// Toolkit dynamiquement (menu contextuel tactique, classement, radar) et ne peut donc pas
/// utiliser var(--color-...) directement. Garder les deux fichiers synchronisés à la main :
/// c'est la seule vraie duplication tolérée, USS ne pouvant pas être lu depuis du C# pur.
/// </summary>
public static class NovgovTheme
{
    public static readonly Color Accent = new Color32(176, 48, 40, 255);
    public static readonly Color AccentHover = new Color32(204, 74, 62, 255);
    public static readonly Color Success = new Color32(96, 132, 64, 255);
    public static readonly Color Danger = new Color32(150, 42, 32, 255);
    public static readonly Color Info = new Color32(90, 130, 152, 255);
    public static readonly Color Neutral = new Color32(232, 222, 196, 255);
    public static readonly Color TextDim = new Color32(176, 162, 126, 255);
    public static readonly Color PanelBorderDim = new Color32(94, 82, 54, 255);
    public static readonly Color TeamPlayer = new Color32(72, 128, 168, 255);
    public static readonly Color TeamEnemy = new Color32(188, 62, 48, 255);

    /// <summary>Couleur d'équipe ABSOLUE : équipe 1 = bleu, équipe 2 = rouge, quel que soit le joueur qui
    /// regarde (barres de vie, marqueurs, bandeau, radar) — un joueur de l'équipe 2 voit donc ses unités
    /// en rouge, comme son bandeau.</summary>
    public static Color ColorForTeam(int teamID) => teamID == 2 ? TeamEnemy : TeamPlayer;
}
