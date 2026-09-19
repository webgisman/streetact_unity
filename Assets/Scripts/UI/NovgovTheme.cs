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

    /// <summary>Couleur d'identité d'équipe ABSOLUE — équipe 1 = bleu, équipe 2 = rouge, TOUJOURS,
    /// quel que soit le joueur qui regarde (jamais "mon camp"/"l'ennemi", voir isPlayerControlled
    /// ailleurs dans le code pour CETTE notion différente, relative au client local).
    ///
    /// Ajoutée le 2026-09-19, retour joueur (équipe rouge) : "il faut donner la barre rouge au
    /// joueur rouge" — la barre de vie au-dessus de chaque unité (UnitAI.SetupHealthBar/
    /// UpdateHealthBar) et son icône 2D (UnitTacticalMarker) coloraient jusqu'ici selon
    /// isPlayerControlled ("mes unités en bleu, peu importe mon équipe") alors que TOUT LE RESTE de
    /// l'identité visuelle du jeu (bannière d'équipe "ÉQUIPE ROUGE/BLEUE", barre de Zone de
    /// Contrôle, anneau de déploiement, radar) utilise déjà cette même convention ABSOLUE. Un
    /// joueur de l'équipe 2 voyait donc ses PROPRES unités en bleu alors que sa propre bannière
    /// affichait "ÉQUIPE ROUGE" — un vrai décalage d'identité visuelle, pas juste esthétique :
    /// c'est un contributeur plausible à la confusion "je crois qu'il y a un changement de couleur,
    /// je peux jouer les unités de l'autre joueur" rapportée par ailleurs la même session.</summary>
    public static Color ColorForTeam(int teamID) => teamID == 2 ? TeamEnemy : TeamPlayer;
}
