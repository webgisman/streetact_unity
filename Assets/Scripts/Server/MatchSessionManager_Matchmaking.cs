using Novgov.Network;

namespace Novgov.Server
{
    public partial class MatchSessionManager
    {
        // Nombre de parties Deathmatch/Zone de Contrôle RÉELLEMENT en cours sur ce processus — sert
        // uniquement à la télémétrie (logs).
        private int activeMatchCount = 0;

        /// <summary>Choisit la tuile Slippy Map de la partie (2026-08-30, "des milliers de cartes") —
        /// celle de p1 (premier arrivé en file) par défaut ; bascule sur celle de p2 UNIQUEMENT si
        /// elle est déjà en cache et pas celle de p1, pour garder les démarrages de partie rapides le
        /// plus souvent possible (voir TacticalGridBuilder.IsTileCached). "Default" si aucun des deux
        /// n'a de tuile domicile connue (jamais eu de position GPS, voir NetMessage.has_home_tile).</summary>
        private static (string cacheKey, string owningUserId) DetermineMatchCacheKey(PlayerConnection p1, PlayerConnection p2)
        {
            string p1Key = p1.HasHomeTile ? $"Z{CityGenerator.ZONE_ZOOM}_{p1.HomeTileX}_{p1.HomeTileY}" : null;
            string p2Key = p2.HasHomeTile ? $"Z{CityGenerator.ZONE_ZOOM}_{p2.HomeTileX}_{p2.HomeTileY}" : null;

            if (p1Key == null && p2Key == null) return ("Default", null);
            if (p1Key == null) return (p2Key, p2.UserId);
            if (p2Key == null) return (p1Key, p1.UserId);

            bool p1Cached = TacticalGridBuilder.IsTileCached(p1Key);
            bool p2Cached = TacticalGridBuilder.IsTileCached(p2Key);
            if (p2Cached && !p1Cached) return (p2Key, p2.UserId);
            return (p1Key, p1.UserId);
        }

        private static void AbortMatchSafely(PlayerConnection p)
        {
            try { if (!p.IsDisconnected) p.Send(new NetMessage { type = "match_over", winner_team = 0 }); } catch { }
            try { p.Close(); } catch { }
        }

        /// <summary>"Z{zoom}_{tileX}_{tileY}" -> (tileX, tileY) — false pour "Default" ou toute autre
        /// valeur malformée (l'appelant doit alors traiter comme "Default").</summary>
        private static bool TryParseTileCacheKey(string cacheKey, out int tileX, out int tileY)
        {
            tileX = 0; tileY = 0;
            if (string.IsNullOrEmpty(cacheKey) || cacheKey == "Default") return false;
            string[] parts = cacheKey.Split('_');
            return parts.Length == 3 && int.TryParse(parts[1], out tileX) && int.TryParse(parts[2], out tileY);
        }
    }
}
