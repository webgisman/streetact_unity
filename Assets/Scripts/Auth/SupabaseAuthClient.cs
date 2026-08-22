using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace StreetAct.Auth
{
    [Serializable]
    public class AuthUser
    {
        public string id;
        public string email;
    }

    [Serializable]
    public class AuthSession
    {
        public string access_token;
        public string refresh_token;
        public int expires_in;
        public AuthUser user;
    }

    [Serializable]
    public class AuthErrorBody
    {
        public string msg;
        public string error_description;
        public string error;
    }

    /// <summary>
    /// Client minimal pour l'API REST de GoTrue (auth self-hosted), voir
    /// Assets/_ServerDocs/multiplayer/02-auth-supabase-unity.md pour le contexte.
    /// </summary>
    public static class SupabaseAuthClient
    {
        // Ajuster ces deux valeurs pour pointer vers le déploiement réel.
        public static string BaseUrl = "https://novgov.com/auth/v1";
        public static string AnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJyb2xlIjoiYW5vbiIsImlzcyI6InN1cGFiYXNlIiwiaWF0IjoxNzg3NDM1NzMzLCJleHAiOjIxMDI3OTU3MzN9.m6TOUVAAMgJiEl2cL7BCbdBwV4BptF2K8JxZ3UNf4C0";

        public static AuthSession CurrentSession { get; private set; }

        public static Task<(bool ok, string error)> SignUp(string email, string password, string username)
        {
            string escapedUsername = username.Replace("\"", "\\\"");
            string body = "{\"email\":\"" + EscapeJson(email) + "\",\"password\":\"" + EscapeJson(password) +
                          "\",\"data\":{\"username\":\"" + escapedUsername + "\"}}";
            return PostJson($"{BaseUrl}/signup", body);
        }

        public static Task<(bool ok, string error)> SignIn(string email, string password)
        {
            string body = "{\"email\":\"" + EscapeJson(email) + "\",\"password\":\"" + EscapeJson(password) + "\"}";
            return PostJson($"{BaseUrl}/token?grant_type=password", body);
        }

        public static Task<(bool ok, string error)> RefreshSession(string refreshToken)
        {
            string body = "{\"refresh_token\":\"" + EscapeJson(refreshToken) + "\"}";
            return PostJson($"{BaseUrl}/token?grant_type=refresh_token", body);
        }

        public static void SignOut()
        {
            CurrentSession = null;
        }

        private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static async Task<(bool ok, string error)> PostJson(string url, string jsonBody)
        {
            using var req = new UnityWebRequest(url, "POST");
            byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", AnonKey);

            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            string responseText = req.downloadHandler.text;

            if (req.result != UnityWebRequest.Result.Success)
            {
                string message = req.error;
                try
                {
                    var errBody = JsonUtility.FromJson<AuthErrorBody>(responseText);
                    if (errBody != null)
                    {
                        message = !string.IsNullOrEmpty(errBody.msg) ? errBody.msg
                                : !string.IsNullOrEmpty(errBody.error_description) ? errBody.error_description
                                : message;
                    }
                }
                catch { /* corps d'erreur non-JSON, on garde req.error */ }

                Debug.LogWarning($"[SupabaseAuthClient] Échec auth : {message}");
                return (false, message);
            }

            try
            {
                CurrentSession = JsonUtility.FromJson<AuthSession>(responseText);
                return (true, null);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SupabaseAuthClient] Réponse inattendue : {ex.Message}\n{responseText}");
                return (false, "Réponse serveur inattendue.");
            }
        }
    }
}
