using System;
using System.Collections;
using Novgov.Auth;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

/// <summary>
/// Écran classement (lecture seule) — lit directement /profiles?order=rating.desc via PostgREST,
/// déjà public en lecture pour tout joueur authentifié (voir schema.sql, policy "Un profil est
/// visible par tous les joueurs authentifiés"). Aucune logique serveur de jeu nécessaire.
/// Accessible depuis le menu de démarrage et depuis l'écran de fin de partie.
/// </summary>
public static class LeaderboardController
{
    private static MonoBehaviour runner;
    private static bool bound = false;

    public static void Show()
    {
        if (UIScreenManager.Instance == null) return;
        EnsureRunner();
        BindOnce();
        UIScreenManager.Instance.Show("Leaderboard");
        runner.StartCoroutine(FetchAndDisplay());
    }

    private static void EnsureRunner()
    {
        if (runner != null) return;
        var go = new GameObject("LeaderboardRunner");
        UnityEngine.Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<LeaderboardRunnerBehaviour>();
    }

    private static void BindOnce()
    {
        if (bound) return;
        bound = true;
        VisualElement root = UIScreenManager.Instance.GetScreen("Leaderboard");
        root.Q<Button>("close-button").clicked += () => UIScreenManager.Instance.HideAll();
    }

    [Serializable] private class ProfileEntry { public string username; public int rating; }
    [Serializable] private class ProfileList { public ProfileEntry[] items; }

    private static IEnumerator FetchAndDisplay()
    {
        VisualElement root = UIScreenManager.Instance.GetScreen("Leaderboard");
        Label loadingLabel = root.Q<Label>("loading-label");
        VisualElement listContainer = root.Q<VisualElement>("list-container");
        listContainer.Clear();
        loadingLabel.style.display = DisplayStyle.Flex;
        loadingLabel.text = "Chargement...";

        string url = $"{SupabaseAuthClient.RestBaseUrl}/profiles?select=username,rating&order=rating.desc&limit=50";
        string token = SupabaseAuthClient.CurrentSession != null ? SupabaseAuthClient.CurrentSession.access_token : SupabaseAuthClient.AnonKey;

        using var req = UnityWebRequest.Get(url);
        req.SetRequestHeader("apikey", SupabaseAuthClient.AnonKey);
        req.SetRequestHeader("Authorization", "Bearer " + token);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            loadingLabel.text = "Impossible de charger le classement.";
            yield break;
        }

        try
        {
            string wrapped = "{\"items\":" + req.downloadHandler.text + "}";
            var parsed = JsonUtility.FromJson<ProfileList>(wrapped);
            loadingLabel.style.display = DisplayStyle.None;

            int rank = 1;
            foreach (var entry in parsed.items)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.justifyContent = Justify.SpaceBetween;
                row.style.paddingTop = 8; row.style.paddingBottom = 8;
                row.style.borderBottomWidth = 1;
                row.style.borderBottomColor = new StyleColor(NovgovTheme.PanelBorderDim);

                var rankLabel = new Label($"#{rank}");
                rankLabel.style.width = 60;
                rankLabel.style.color = new StyleColor(rank == 1 ? NovgovTheme.Accent : NovgovTheme.TextDim);

                var nameLabel = new Label(entry.username);
                nameLabel.style.flexGrow = 1;

                var ratingLabelEl = new Label(entry.rating.ToString());
                ratingLabelEl.style.unityFontStyleAndWeight = FontStyle.Bold;

                row.Add(rankLabel);
                row.Add(nameLabel);
                row.Add(ratingLabelEl);
                listContainer.Add(row);
                rank++;
            }
        }
        catch (Exception ex)
        {
            loadingLabel.style.display = DisplayStyle.Flex;
            loadingLabel.text = "Erreur d'affichage du classement.";
            Debug.LogWarning($"[LeaderboardController] Parsing échoué : {ex.Message}");
        }
    }

    private class LeaderboardRunnerBehaviour : MonoBehaviour { }
}
