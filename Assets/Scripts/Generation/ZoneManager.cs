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

        // Quartier d'origine du joueur (son QG), fixé une fois par InitializeHomeZoneFromGps —
        // distinct de CurrentTileX/Y, qui suit le quartier affiché ou la bataille en cours.
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

#if UNITY_EDITOR
        /// <summary>Éditeur uniquement (Novgov.Network.EditorTestPlayers) : un compte de test a son
        /// propre quartier, qui devient le QG et la position de CETTE fenêtre — remplace la tuile
        /// domicile figée par fenêtre, qui ne correspondait à aucun des deux comptes.</summary>
        public void OverrideHomeZoneForEditorTest(int tileX, int tileY)
        {
            PlayerPrefs.SetInt(HomeTileXPrefKey, tileX);
            PlayerPrefs.SetInt(HomeTileYPrefKey, tileY);
            PlayerPrefs.Save();
            CurrentTileX = tileX;
            CurrentTileY = tileY;
        }
#endif

        /// <summary>Repositionne le joueur sur une Zone SANS la générer — pour juste avant un
        /// rechargement de scène (voir MultiplayerMatchController.ReturnToHub), qui rechargera de
        /// toute façon la Zone courante : la générer ici serait un travail jeté immédiatement.</summary>
        public void SetCurrentTileWithoutLoading(int tileX, int tileY)
        {
            CurrentTileX = tileX;
            CurrentTileY = tileY;
        }
    }
}
