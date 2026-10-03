#if UNITY_EDITOR && !UNITY_SERVER
namespace Novgov.Network
{
    /// <summary>
    /// Les 2 comptes de test (novgov.com) et le quartier de chacun — Éditeur uniquement. Se connecter avec
    /// l'un d'eux place la carte sur le quartier que ce compte possède réellement : Joueur 1
    /// (66648,44111, Lille Sud) et Joueur 2 juste au nord (66648,44110), voisins directs pour tester un
    /// siège tout de suite.
    /// </summary>
    public static class EditorTestPlayers
    {
        public readonly struct TestPlayer
        {
            public readonly int Number;
            public readonly string Email, Password, Username;
            public readonly int TileX, TileY;

            public TestPlayer(int number, string email, string password, string username, int tileX, int tileY)
            {
                Number = number; Email = email; Password = password; Username = username; TileX = tileX; TileY = tileY;
            }

            public string Label => $"Joueur {Number}";
        }

        public static readonly TestPlayer Player1 = new TestPlayer(1, "testlille1@novgov.test", "TestLille1!", "TestLille1", 66648, 44111);
        public static readonly TestPlayer Player2 = new TestPlayer(2, "testlille2@novgov.test", "TestLille2!", "TestLille2", 66648, 44110);

        public static bool TryGet(string email, out TestPlayer player)
        {
            if (string.Equals(email, Player1.Email, System.StringComparison.OrdinalIgnoreCase)) { player = Player1; return true; }
            if (string.Equals(email, Player2.Email, System.StringComparison.OrdinalIgnoreCase)) { player = Player2; return true; }
            player = default;
            return false;
        }

        public static TestPlayer Opponent(TestPlayer player) => player.Number == 1 ? Player2 : Player1;

        /// <summary>"au NORD", "au SUD"... : où se trouve le quartier de <paramref name="other"/> vu
        /// depuis celui de <paramref name="me"/> (tileY augmente vers le Sud).</summary>
        public static string Direction(TestPlayer me, TestPlayer other)
        {
            string dir = Novgov.UI.QuartierText.Direction(other.TileX - me.TileX, other.TileY - me.TileY);
            return dir.StartsWith("EST") || dir.StartsWith("OUEST") ? "à l'" + dir : "au " + dir;
        }

        /// <summary>Joueur conseillé pour CETTE fenêtre : 1 dans l'Éditeur principal, 2 dans un Joueur
        /// Virtuel — pour ne jamais connecter deux fois le même compte.</summary>
        public static int RecommendedNumberForThisWindow => Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor ? 1 : 2;
    }
}
#endif
