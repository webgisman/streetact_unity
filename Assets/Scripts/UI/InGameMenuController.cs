using UnityEngine;
#if !UNITY_SERVER
using UnityEngine.UIElements;
#endif

namespace Novgov.UI
{
    /// <summary>
    /// Menu PAUSE en partie (Assets/Resources/UI/PauseMenuScreen.uxml), ouvert par le bouton "MENU"
    /// du bandeau tactique (TacticalBottomBarScreen.uxml, "hud-menu-button") — 2026-09-30, refonte du
    /// parcours des menus. Jusqu'ici, une fois une partie lancée (solo comme en ligne), il n'existait
    /// AUCUN moyen d'en sortir autrement qu'en tuant l'application.
    ///
    /// - Solo : vraie pause (Time.timeScale = 0), RECOMMENCER, MENU PRINCIPAL.
    /// - En ligne : le temps n'est JAMAIS gelé (le serveur arbitre le tour), seul QUITTER est proposé
    ///   — la connexion coupée fait passer les unités du joueur en garde automatique côté serveur.
    /// Toute action destructrice demande un second appui (texte "TOUCHEZ À NOUVEAU...").
    ///
    /// Tant que le menu est ouvert, TacticalPathManager ignore tout tap (voir <see cref="IsOpen"/>) et
    /// son fond "pause-backdrop" absorbe les taps (liste blanche de UnitSpawnerUI.IsPointerOverOnGUI,
    /// qui protège aussi la caméra).
    /// </summary>
    public static class InGameMenuController
    {
#if !UNITY_SERVER
        private static VisualElement screenRoot;
        private static Button resumeButton, restartButton, quitButton;
        private static Label hintLabel;
        private static bool screenBound;
        private static string armedAction; // "restart" / "quit" : premier appui reçu, en attente de confirmation

        /// <summary>Lu depuis la VISIBILITÉ RÉELLE de l'écran plutôt qu'un booléen séparé : un Show()
        /// ailleurs (fin de partie, rechargement de scène via HideAll) masque cet écran sans passer
        /// par Close() — un booléen serait resté vrai et aurait bloqué les taps de la partie suivante.</summary>
        public static bool IsOpen => screenRoot != null && screenRoot.style.display == DisplayStyle.Flex;

        private static bool IsOnline => Novgov.Network.MultiplayerMatchController.IsFlowActive;

        /// <summary>Câble le bouton "MENU" du bandeau tactique — appelé à chaque BindTacticalUI (une
        /// fois par scène) ; userData évite un double abonnement sur ce bouton persistant
        /// (UIScreenManager survit aux rechargements de scène).</summary>
        public static void BindHudButton(Button hudMenuButton)
        {
            if (hudMenuButton == null) return;
            if (hudMenuButton.userData != null) return;
            hudMenuButton.userData = true;
            hudMenuButton.clicked += Open;
        }

        private static bool BindScreenOnce()
        {
            if (screenBound) return true;
            if (UIScreenManager.Instance == null) return false;
            screenRoot = UIScreenManager.Instance.GetScreen("PauseMenu");
            if (screenRoot == null)
            {
                Debug.LogError("[InGameMenuController] Écran 'PauseMenu' introuvable (UXML non chargé).");
                return false;
            }
            resumeButton = screenRoot.Q<Button>("btn-pause-resume");
            restartButton = screenRoot.Q<Button>("btn-pause-restart");
            quitButton = screenRoot.Q<Button>("btn-pause-quit");
            hintLabel = screenRoot.Q<Label>("lbl-pause-hint");
            if (resumeButton != null) resumeButton.clicked += Close;
            if (restartButton != null) restartButton.clicked += () => OnDestructiveClicked("restart");
            if (quitButton != null) quitButton.clicked += () => OnDestructiveClicked("quit");
            screenBound = true;
            return true;
        }

        public static void Open()
        {
            if (!BindScreenOnce()) return;
            armedAction = null;
            RefreshTexts();
            if (!IsOnline) Time.timeScale = 0f;
            UIScreenManager.Instance.SetVisible("PauseMenu", true);
        }

        public static void Close()
        {
            armedAction = null;
            Time.timeScale = 1f;
            UIScreenManager.Instance?.SetVisible("PauseMenu", false);
        }

        private static void RefreshTexts()
        {
            bool online = IsOnline;
            if (restartButton != null)
            {
                restartButton.style.display = online ? DisplayStyle.None : DisplayStyle.Flex;
                restartButton.text = armedAction == "restart" ? "TOUCHEZ À NOUVEAU POUR RECOMMENCER" : "RECOMMENCER LA PARTIE";
            }
            if (quitButton != null)
            {
                string label = online ? "QUITTER LA PARTIE" : "MENU PRINCIPAL";
                quitButton.text = armedAction == "quit" ? "TOUCHEZ À NOUVEAU POUR CONFIRMER" : label;
            }
            if (hintLabel != null)
            {
                hintLabel.text = online
                    ? "La partie en ligne continue pendant ce menu. Si vous quittez, vos unités passent en garde automatique et la partie se poursuit sans vous."
                    : "Partie en pause. Recommencer ou revenir au menu abandonne la partie en cours.";
            }
        }

        private static void OnDestructiveClicked(string action)
        {
            if (armedAction != action)
            {
                armedAction = action;
                RefreshTexts();
                return;
            }

            bool online = IsOnline;
            Close();
            if (online)
            {
                Novgov.Network.MultiplayerMatchController.EnsureInstance().QuitCurrentMatch();
                return;
            }

            TacticalPathManager.IsSoloGameOver = false;
            GameManagerUI.ReloadSceneThen(action == "restart"
                ? GameManagerUI.AfterReloadAction.StartSolo
                : GameManagerUI.AfterReloadAction.None);
        }
#else
        public static bool IsOpen => false;
#endif
    }
}
