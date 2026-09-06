using UnityEngine;
using Novgov.Core;

namespace Novgov.Generation
{
    /// <summary>
    /// État client des Zones de Conquête (grille Slippy Map fixe, voir CityGenerator.ZONE_ZOOM).
    /// Le GPS de l'appareil n'est converti en (tileX, tileY) qu'UNE SEULE fois, au tout premier
    /// lancement (InitializeHomeZoneFromGps), puis persisté — ensuite le jeu ne raisonne plus jamais
    /// en coordonnées GPS, uniquement en index de tuile. Pilote le chargement local d'une Zone
    /// (exploration) et les demandes d'attaque/capture envoyées au serveur (voir
    /// MultiplayerMatchController.AttackZone).
    /// </summary>
    public class ZoneManager : MonoBehaviour
    {
        // 2026-09-06 : suffixe ajouté (voir Novgov.Core.EditorPlayerPrefsScope) — sans lui, PlayerPrefs
        // vit dans une case du Registre Windows PARTAGÉE entre l'Éditeur principal et tous ses clones
        // Multiplayer Play Mode : le premier lancement local (n'importe lequel) figeait la même tuile
        // pour toutes les instances futures, quelles que soient les coordonnées GPS simulées calculées
        // par ailleurs (GameManagerUI.PickEditorMockCity). Un ancien correctif (reset manuel à chaque
        // lancement d'un Joueur Virtuel, voir historique Git) réglait la lecture mais pas le fond du
        // problème ; ce suffixe donne à chaque identité sa PROPRE case, une fois pour toutes.
        private static string HomeTileXPrefKey => "ZoneManager_HomeTileX" + Novgov.Core.EditorPlayerPrefsScope.Suffix;
        private static string HomeTileYPrefKey => "ZoneManager_HomeTileY" + Novgov.Core.EditorPlayerPrefsScope.Suffix;

        public static ZoneManager Instance { get; private set; }

        public static ZoneManager EnsureInstance()
        {
            if (Instance == null)
            {
                var go = new GameObject("ZoneManager");
                DontDestroyOnLoad(go);
                go.AddComponent<ZoneManager>();
            }
            return Instance;
        }

        public int CurrentTileX { get; private set; }
        public int CurrentTileY { get; private set; }

        /// <summary>Vrai dès que la Zone d'origine du joueur a été fixée une première fois — au-delà,
        /// InitializeHomeZoneFromGps() n'a plus aucun effet, le GPS n'est plus jamais consulté.</summary>
        public bool HasHomeZone => PlayerPrefs.HasKey(HomeTileXPrefKey);

        // Tuile d'origine STABLE (jamais modifiée après InitializeHomeZoneFromGps), distincte de
        // CurrentTileX/Y ci-dessus qui, elle, change à chaque exploration (ExpandNorth/South/East/
        // West) ou chargement d'une Zone de combat (LoadZone) — voir MultiplayerMatchController,
        // 2026-08-30 ("des milliers de cartes") : le matchmaking Deathmatch/Zone de Contrôle a besoin
        // de LA tuile domicile, pas de la dernière Zone parcourue par le joueur.
        public int HomeTileX => PlayerPrefs.GetInt(HomeTileXPrefKey);
        public int HomeTileY => PlayerPrefs.GetInt(HomeTileYPrefKey);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            if (HasHomeZone)
            {
                CurrentTileX = PlayerPrefs.GetInt(HomeTileXPrefKey);
                CurrentTileY = PlayerPrefs.GetInt(HomeTileYPrefKey);
            }
        }

        /// <summary>Point d'entrée UNIQUE du GPS dans tout le système de Zones — appelé par
        /// GameManagerUI.StartDeviceGPS() une seule fois, à la toute première partie. Sans effet si
        /// une Zone d'origine existe déjà (voir HasHomeZone).</summary>
        public void InitializeHomeZoneFromGps(double latitude, double longitude)
        {
            if (HasHomeZone) return;

            GeoProjection.TileIndexFromCoordinate(latitude, longitude, CityGenerator.ZONE_ZOOM, out int tileX, out int tileY);
            CurrentTileX = tileX;
            CurrentTileY = tileY;

            PlayerPrefs.SetInt(HomeTileXPrefKey, tileX);
            PlayerPrefs.SetInt(HomeTileYPrefKey, tileY);
            PlayerPrefs.Save();

            Debug.Log($"[ZoneManager] Zone d'origine fixée une fois pour toutes : ({tileX},{tileY}) — le GPS ne sera plus consulté.");
        }

        /// <summary>Charge une Zone précise (bâtiments + sol + NavMesh) sur CE client, sans passer par
        /// le serveur — pour l'exploration locale (déplacement libre entre Zones déjà possédées) ou
        /// pour afficher la Zone d'un combat de conquête en cours (voir
        /// MultiplayerMatchController.LoadConquestZoneThenOpenDeployment).</summary>
        public void LoadZone(int tileX, int tileY)
        {
            CurrentTileX = tileX;
            CurrentTileY = tileY;

            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
            if (cityGen == null)
            {
                Debug.LogError("[ZoneManager] CityGenerator introuvable dans la scène.");
                return;
            }

            cityGen.zoneTileX = tileX;
            cityGen.zoneTileY = tileY;
            cityGen.GenerateCity();

            MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
            if (mapLoader != null) mapLoader.LoadMap();
        }

        /// <summary>ÉQUITÉ MULTIJOUEUR (2026-09-05) — variante de LoadZone pour un VRAI match : charge
        /// la Zone à partir du JSON Overpass exact que le serveur autoritaire a lui-même utilisé,
        /// au lieu de refaire une requête Overpass indépendante qui pourrait diverger du résultat
        /// serveur (voir CityGenerator.LoadZoneFromServerData pour le détail du risque corrigé).
        /// Appelée par MultiplayerMatchController dès que "match_found" apporte un
        /// city_data_json.</summary>
        public void LoadZoneFromServerData(int tileX, int tileY, string json)
        {
            CurrentTileX = tileX;
            CurrentTileY = tileY;

            CityGenerator cityGen = FindAnyObjectByType<CityGenerator>();
            if (cityGen == null)
            {
                Debug.LogError("[ZoneManager] CityGenerator introuvable dans la scène.");
                return;
            }

            cityGen.LoadZoneFromServerData(tileX, tileY, json);

            MapTileLoader mapLoader = FindAnyObjectByType<MapTileLoader>();
            if (mapLoader != null) mapLoader.LoadMap();
        }

        public void LoadCurrentZone() => LoadZone(CurrentTileX, CurrentTileY);

        // Exploration locale (pas de demande serveur) — Slippy Map : tileY augmente vers le Sud.
        public void ExpandNorth() => LoadZone(CurrentTileX, CurrentTileY - 1);
        public void ExpandSouth() => LoadZone(CurrentTileX, CurrentTileY + 1);
        public void ExpandEast() => LoadZone(CurrentTileX + 1, CurrentTileY);
        public void ExpandWest() => LoadZone(CurrentTileX - 1, CurrentTileY);

        // AttackZone() n'existe que sous #if !UNITY_SERVER dans MultiplayerMatchController (tout le
        // reste de cette classe, hors membres statiques, réagit à un serveur distant — aucune raison
        // de tourner sur le serveur lui-même). Un build Dedicated Server compile quand même TOUT
        // Assembly-CSharp, donc un appel non gardé ici casserait la compilation côté serveur — voir
        // le même piège déjà documenté pour GameManagerUI/UnitSpawnerUI dans
        // 08-known-issues-and-todo.md.
#if !UNITY_SERVER
        /// <summary>Demande au serveur l'attaque/capture de la Zone adjacente indiquée — ne change
        /// PAS la Zone affichée localement : c'est la réponse du serveur ("zone_captured" ou
        /// "match_found") qui déclenche le vrai chargement, voir MultiplayerMatchController.</summary>
        public void AttackNorth() => Novgov.Network.MultiplayerMatchController.EnsureInstance().AttackZone(CurrentTileX, CurrentTileY - 1);
        public void AttackSouth() => Novgov.Network.MultiplayerMatchController.EnsureInstance().AttackZone(CurrentTileX, CurrentTileY + 1);
        public void AttackEast() => Novgov.Network.MultiplayerMatchController.EnsureInstance().AttackZone(CurrentTileX + 1, CurrentTileY);
        public void AttackWest() => Novgov.Network.MultiplayerMatchController.EnsureInstance().AttackZone(CurrentTileX - 1, CurrentTileY);
#endif
    }
}
