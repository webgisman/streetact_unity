# Authentification Supabase côté client Unity

Le projet a déjà `com.unity.modules.unitywebrequest` dans `Packages/manifest.json` — pas besoin
d'installer de package externe. On appelle directement l'API REST de GoTrue (exposée via Nginx
sur `https://ton-domaine/auth/v1/`).

## Pourquoi pas le SDK officiel `supabase-csharp` ?

Il fonctionne, mais ajoute une dépendance externe (NuGet/UPM tierce) et une couche
d'abstraction pour un besoin très ciblé (inscription, connexion, refresh token). Des appels
`UnityWebRequest` directs sont plus simples à déboguer, plus légers en build mobile, et
suffisants ici. À réévaluer seulement si vous avez besoin de Realtime/Storage côté client plus
tard.

## Endpoints utilisés

| Action | Endpoint | Méthode |
|---|---|---|
| Inscription | `/auth/v1/signup` | POST `{ "email", "password", "data": { "username" } }` |
| Connexion | `/auth/v1/token?grant_type=password` | POST `{ "email", "password" }` |
| Refresh token | `/auth/v1/token?grant_type=refresh_token` | POST `{ "refresh_token" }` |
| Déconnexion | `/auth/v1/logout` | POST (avec header `Authorization: Bearer <access_token>`) |

Toutes les requêtes ont besoin du header `apikey: <ANON_KEY>` (la clé anonyme définie dans
`.env`, publique côté client — ce n'est pas un secret, c'est l'équivalent d'une clé d'API
publique Supabase standard).

## Squelette C# (`Assets/Scripts/Auth/SupabaseAuthClient.cs`, à créer)

```csharp
using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace StreetAct.Auth
{
    [Serializable]
    public class AuthSession
    {
        public string access_token;
        public string refresh_token;
        public int expires_in;
        public string user_id;
    }

    public static class SupabaseAuthClient
    {
        private const string BaseUrl = "https://ton-domaine.com/auth/v1";
        private const string AnonKey = "PASTE_ANON_KEY_HERE"; // même valeur que ANON_KEY côté serveur

        public static async Task<AuthSession> SignUp(string email, string password, string username)
        {
            string body = $"{{\"email\":\"{email}\",\"password\":\"{password}\"," +
                          $"\"data\":{{\"username\":\"{username}\"}}}}";
            return await PostJson($"{BaseUrl}/signup", body);
        }

        public static async Task<AuthSession> SignIn(string email, string password)
        {
            string body = $"{{\"email\":\"{email}\",\"password\":\"{password}\"}}";
            return await PostJson($"{BaseUrl}/token?grant_type=password", body);
        }

        public static async Task<AuthSession> RefreshSession(string refreshToken)
        {
            string body = $"{{\"refresh_token\":\"{refreshToken}\"}}";
            return await PostJson($"{BaseUrl}/token?grant_type=refresh_token", body);
        }

        private static async Task<AuthSession> PostJson(string url, string jsonBody)
        {
            using var req = new UnityWebRequest(url, "POST");
            byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
            req.uploadHandler = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("apikey", AnonKey);

            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[Auth] Échec : {req.error} — {req.downloadHandler.text}");
                return null;
            }

            // Parsing minimal — remplacer par JsonUtility.FromJson<AuthSession> une fois
            // le modèle exact validé contre la réponse réelle de GoTrue (elle contient aussi
            // un objet "user" imbriqué qu'il faudra mapper si vous avez besoin du username ici).
            return JsonUtility.FromJson<AuthSession>(req.downloadHandler.text);
        }
    }
}
```

## Stockage de la session côté client

- `access_token` (JWT, expire selon `GOTRUE_JWT_EXP`, 3600s dans `.env.example`) : gardé en
  mémoire, jamais persisté en clair sur disque.
- `refresh_token` : seul élément à persister, via `PlayerPrefs` en test rapide, mais pour la
  version qui ira sur un store, utiliser le Keystore Android / Keychain iOS (Unity propose
  `UnityEngine.Security` limité — un plugin natif dédié sera nécessaire pour un stockage
  vraiment sécurisé ; à traiter avant la mise en prod publique, pas bloquant pour le test à 2
  téléphones).
- Au démarrage de l'app, si un `refresh_token` existe, appeler `RefreshSession()` avant
  d'afficher l'écran de login.

## Ce que le serveur de jeu Unity headless fait du JWT

Le client envoie son `access_token` dans le premier message TCP après connexion (voir
[03-network-protocol.md](03-network-protocol.md), message `auth`). Le serveur :

1. Vérifie la signature du JWT avec `JWT_SECRET` (le même secret que GoTrue/PostgREST — c'est
   pour ça qu'il est partagé dans `.env`).
2. Extrait `sub` (= `user_id`) du payload.
3. Associe la connexion TCP à ce `user_id` pour le reste de la session.

Aucun appel réseau à GoTrue n'est nécessaire côté serveur pour valider le token — la
vérification de signature JWT est locale (HMAC avec le secret partagé), donc rapide et sans
dépendance réseau pendant la partie.
