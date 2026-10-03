using UnityEngine;

#if UNITY_EDITOR && !UNITY_SERVER
/// <summary>Panneau "MODE TEST" de l'Éditeur (jamais compilé dans un build appareil — voir #if
/// UNITY_EDITOR ci-dessus), affiché dans CHAQUE fenêtre (Éditeur principal ou Joueur Virtuel
/// Multiplayer Play Mode).
///
/// 2026-10-03, retour joueur : "le mode test multijoueur est incompréhensible". L'ancien panneau
/// "[DIAGNOSTIC]" listait un email, des coordonnées de tuile brutes "(66648,44111)" et une "ville
/// simulée" sans dire quoi faire. Il dit maintenant, en clair et selon l'étape où en est CETTE
/// fenêtre : qui je suis (Joueur 1 / Joueur 2), où est l'adversaire, et les étapes pour tester un
/// combat entre les deux fenêtres (voir Novgov.Network.EditorTestPlayers).</summary>
public class EditorDebugOverlay : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("EditorDebugOverlay");
        DontDestroyOnLoad(go);
        go.AddComponent<EditorDebugOverlay>();
    }

    private GUIStyle boxStyle;
    private GUIStyle labelStyle;
    private bool hidden = false;

    private string BuildText()
    {
        bool mainWindow = Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor;
        string window = mainWindow ? "fenêtre principale" : "fenêtre « Joueur virtuel »";
        string email = Novgov.Auth.SupabaseAuthClient.CurrentSession?.user?.email;

        if (string.IsNullOrEmpty(email))
        {
            int n = Novgov.Network.EditorTestPlayers.RecommendedNumberForThisWindow;
            return
                $"<b>MODE TEST — {window}</b>\n" +
                "Pour tester un combat entre 2 joueurs :\n" +
                $"1. Touchez JOUER EN LIGNE, puis JOUEUR {n} (bouton rouge).\n" +
                $"2. Dans l'autre fenêtre, faites pareil avec JOUEUR {(n == 1 ? 2 : 1)}.\n" +
                "Pas d'autre fenêtre ? Menu Window > Multiplayer >\n" +
                "Multiplayer Play Mode : cochez « Player 2 », puis relancez Play.";
        }

        if (!Novgov.Network.EditorTestPlayers.TryGet(email, out var me))
            return $"<b>MODE TEST — {window}</b>\nConnecté avec un compte qui n'est PAS un compte de test :\n{email}\nPour le test à 2 joueurs, utilisez JOUEUR 1 / JOUEUR 2.";

        var other = Novgov.Network.EditorTestPlayers.Opponent(me);
        return
            $"<b>MODE TEST — vous êtes {me.Label.ToUpperInvariant()} ({me.Username})</b>\n" +
            $"Votre quartier : au centre de la CARTE.\n" +
            $"{other.Label} ({other.Username}) : le quartier juste {Novgov.Network.EditorTestPlayers.Direction(me, other)}.\n" +
            "\n<b>Tester une bataille au tour par tour :</b>\n" +
            "1. ATTAQUANT : touchez le quartier de l'autre joueur\n" +
            "    > LANCER UN SIÈGE (écran « en attente de... »).\n" +
            "2. DÉFENSEUR (autre fenêtre) : bandeau rouge « assiège »\n" +
            "    > DÉFENDRE (affiché sous ~15 s).\n" +
            "3. Les deux : placez vos troupes > CONFIRMER, puis à chaque\n" +
            "    tour tracez vos trajectoires > FIN DE TOUR, et regardez\n" +
            "    la simulation, jusqu'à la victoire.\n" +
            "Après un siège, le quartier visé est protégé 6 h :\n" +
            "pour un 2e essai, attaquez dans l'autre sens.";
    }

    private void OnGUI()
    {
        // Jamais pendant une partie (2026-10-03, retour joueur : « les logs empêchent d'appuyer sur
        // les boutons ») : le guide ne sert qu'à lancer le test depuis les menus, et ce panneau
        // dessiné par-dessus l'interface couvrait des boutons du combat. Il interceptait aussi tout
        // clic dans sa zone pour se replier, ce qui avalait le clic destiné au bouton situé dessous.
        if (hidden) return;
        bool onTitleScreen = GameManagerUI.Instance != null && GameManagerUI.Instance.IsStartupSelectionActive;
        bool inOnlineMenus = Novgov.Network.MultiplayerMatchController.IsFlowActive
            && !Novgov.Network.MultiplayerMatchController.IsInMatch
            && !Novgov.Network.MultiplayerMatchController.IsDeploymentPhaseActive;
        if (!onTitleScreen && !inOnlineMenus) return; // partie en cours (Solo ou en ligne)

        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft };
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = false, richText = true };
        }

        // En BAS à droite, pour ne pas couvrir l'en-tête (bouton RAPPORTS) de l'écran Conquête.
        string text = BuildText();
        Vector2 size = labelStyle.CalcSize(new GUIContent(text));
        float x = Screen.width - size.x - 32;
        float y = Screen.height - size.y - 32f;
        Rect box = new Rect(x, y, size.x + 24, size.y + 16);
        GUI.Box(box, GUIContent.none, boxStyle);
        GUI.Box(box, GUIContent.none, boxStyle); // double passe : fond plus opaque, lisible sur la carte
        GUI.Label(new Rect(x + 10, y + 8, size.x + 12, size.y), text, labelStyle);

        // Seul ce petit bouton capte un clic (le reste du panneau laisse passer les clics).
        if (GUI.Button(new Rect(box.xMax - 26, box.y + 4, 22, 22), "×")) hidden = true;
    }
}
#endif
