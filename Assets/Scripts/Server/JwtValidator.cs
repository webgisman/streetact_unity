using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace StreetAct.Server
{
    [Serializable]
    internal class JwtClaims
    {
        public string sub;
        public long exp;
    }

    /// <summary>
    /// Vérification locale (HMAC-SHA256) des JWT émis par GoTrue, sans appel réseau —
    /// voir Assets/_ServerDocs/multiplayer/02-auth-supabase-unity.md, section "serveur de jeu".
    /// </summary>
    public static class JwtValidator
    {
        /// <summary>
        /// Retourne le user_id (claim "sub") si le token est valide et non expiré, sinon null.
        /// </summary>
        public static string ValidateAndGetUserId(string token, string jwtSecret)
        {
            if (string.IsNullOrEmpty(token)) return null;

            string[] parts = token.Split('.');
            if (parts.Length != 3) return null;

            string headerAndPayload = parts[0] + "." + parts[1];
            byte[] expectedSig;
            try
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(jwtSecret));
                expectedSig = hmac.ComputeHash(Encoding.UTF8.GetBytes(headerAndPayload));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JwtValidator] Erreur HMAC : {ex.Message}");
                return null;
            }

            byte[] actualSig;
            try
            {
                actualSig = Base64UrlDecode(parts[2]);
            }
            catch
            {
                return null;
            }

            if (!FixedTimeEquals(expectedSig, actualSig)) return null;

            JwtClaims claims;
            try
            {
                string payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                claims = JsonUtility.FromJson<JwtClaims>(payloadJson);
            }
            catch
            {
                return null;
            }

            if (claims == null || string.IsNullOrEmpty(claims.sub)) return null;

            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (claims.exp > 0 && nowUnix > claims.exp) return null; // token expiré

            return claims.sub;
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static byte[] Base64UrlDecode(string input)
        {
            string s = input.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
    }
}
