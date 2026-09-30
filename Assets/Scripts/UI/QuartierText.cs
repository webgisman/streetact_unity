#if !UNITY_SERVER
using System;
using System.Text.RegularExpressions;

namespace Novgov.UI
{
    /// <summary>
    /// Vocabulaire du jeu en ligne tel que le joueur le lit (2026-09-30, "un joueur lambda ne va rien
    /// comprendre") : une Zone Slippy Map zoom 17 s'appelle un QUARTIER, repéré par rapport au QG du
    /// joueur ("au Nord-Est de votre QG") — jamais par ses coordonnées de tuile brutes
    /// ("(66648,44111)"), qu'on retrouvait partout (cartes, sièges, rapports). Rien ici ne change le
    /// protocole : les textes envoyés par le serveur (notifications) sont seulement réécrits à
    /// l'affichage, voir <see cref="HumanizeServerText"/>.
    /// </summary>
    public static class QuartierText
    {
        /// <summary>"NORD", "SUD-OUEST"... (tileY augmente vers le Sud, voir ZoneManager) ; vide au centre.</summary>
        public static string Direction(int dx, int dy)
        {
            string ns = dy < 0 ? "NORD" : (dy > 0 ? "SUD" : "");
            string eo = dx > 0 ? "EST" : (dx < 0 ? "OUEST" : "");
            return ns.Length > 0 && eo.Length > 0 ? $"{ns}-{eo}" : ns + eo;
        }

        private static string Capitalized(string upper) =>
            string.IsNullOrEmpty(upper) ? upper : string.Join("-", Array.ConvertAll(upper.Split('-'), p => p.Substring(0, 1) + p.Substring(1).ToLowerInvariant()));

        /// <summary>Position lisible d'un quartier par rapport au QG : "de votre QG" (c'est lui),
        /// "au Nord-Est de votre QG", ou "au Sud de votre QG, à 3 cases".</summary>
        public static string RelativeToHome(int tileX, int tileY)
        {
            var zm = Novgov.Generation.ZoneManager.Instance;
            if (zm == null || !zm.HasHomeZone) return "de votre secteur";
            int dx = tileX - zm.HomeTileX, dy = tileY - zm.HomeTileY;
            if (dx == 0 && dy == 0) return "de votre QG";
            int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
            string dir = Capitalized(Direction(dx, dy));
            string at = dir.StartsWith("Est") || dir.StartsWith("Ouest") ? "à l'" : "au "; // "à l'Est", "au Nord-Est"
            return dist <= 1 ? $"{at}{dir} de votre QG" : $"{at}{dir} de votre QG, à {dist} cases";
        }

        /// <summary>Titre de carte pour un quartier : "Quartier de votre QG" / "Quartier au Nord de votre QG".</summary>
        public static string Title(int tileX, int tileY) => "Q" + Name(tileX, tileY).Substring(1);

        /// <summary>Même chose en milieu de phrase : "quartier au Nord de votre QG".</summary>
        public static string Name(int tileX, int tileY) => $"quartier {RelativeToHome(tileX, tileY)}";

        private static readonly Regex TileCoordinates = new Regex(@"\s*\((-?\d+)\s*,\s*(-?\d+)\)");

        /// <summary>Réécrit un texte du serveur pour le joueur : "Zone"/"territoire" -> "quartier",
        /// et chaque "(x,y)" de tuile -> sa position par rapport au QG.</summary>
        public static string HumanizeServerText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string s = TileCoordinates.Replace(text, m =>
                int.TryParse(m.Groups[1].Value, out int x) && int.TryParse(m.Groups[2].Value, out int y)
                    ? $" ({RelativeToHome(x, y)})"
                    : m.Value);
            s = s.Replace("la Zone", "le quartier").Replace("votre territoire", "votre quartier")
                 .Replace("Zone", "quartier").Replace("elle est à vous", "il est à vous")
                 .Replace("la défendra", "le défendra");
            return s;
        }
    }
}
#endif
