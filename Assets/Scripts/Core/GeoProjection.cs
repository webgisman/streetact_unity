using UnityEngine;
using System;

namespace Novgov.Core
{
    /// <summary>
    /// Classe utilitaire pour garantir une précision absolue entre les données OSM et les tuiles de carte.
    /// Utilise la projection EPSG:3857 (Web Mercator) avec correction locale (Equirectangular)
    /// pour obtenir des mètres réels dans Unity tout en conservant l'alignement parfait.
    /// </summary>
    public static class GeoProjection
    {
        private const double EARTH_RADIUS = 6378137.0; // Rayon de la Terre pour Web Mercator (EPSG:3857)

        private static double currentCenterLat;
        private static double currentCenterLon;
        private static double currentCenterX;
        private static double currentCenterY;
        private static double currentScale;

        /// <summary>
        /// Définit le point central (origine 0,0,0) pour la projection dans Unity.
        /// Doit être appelé une seule fois avant la génération de la ville et de la carte.
        /// </summary>
        public static void SetCenter(float latitude, float longitude)
        {
            currentCenterLat = latitude;
            currentCenterLon = longitude;

            double centerLonRad = currentCenterLon * Math.PI / 180.0;
            double centerLatRad = currentCenterLat * Math.PI / 180.0;

            currentCenterX = EARTH_RADIUS * centerLonRad;
            currentCenterY = EARTH_RADIUS * Math.Log(Math.Tan(Math.PI / 4.0 + centerLatRad / 2.0));

            // Echelle pour convertir la projection Mercator déformée en vrais mètres au niveau de cette latitude
            currentScale = Math.Cos(currentCenterLat * Math.PI / 180.0);
        }

        /// <summary>
        /// Convertit une coordonnée GPS (Latitude, Longitude) en position X,Z dans le monde 3D Unity.
        /// L'origine de la carte est définie par le point central (SetCenter).
        /// </summary>
        public static Vector3 CoordinateToWorldPoint(double lat, double lon)
        {
            double lonRad = lon * Math.PI / 180.0;
            double latRad = lat * Math.PI / 180.0;

            double x = EARTH_RADIUS * lonRad;
            double y = EARTH_RADIUS * Math.Log(Math.Tan(Math.PI / 4.0 + latRad / 2.0));

            float unityX = (float)((x - currentCenterX) * currentScale);
            float unityZ = (float)((y - currentCenterY) * currentScale);

            return new Vector3(unityX, 0, unityZ);
        }

        public static int LonToTileX(double lon, int zoom)
        {
            return (int)(Math.Floor((lon + 180.0) / 360.0 * (1 << zoom)));
        }

        public static int LatToTileY(double lat, int zoom)
        {
            return (int)(Math.Floor((1 - Math.Log(Math.Tan(lat * Math.PI / 180.0) + 1.0 / Math.Cos(lat * Math.PI / 180.0)) / Math.PI) / 2.0 * (1 << zoom)));
        }

        public static double TileXToLon(int x, int zoom)
        {
            return x / (double)(1 << zoom) * 360.0 - 180.0;
        }

        public static double TileYToLat(int y, int zoom)
        {
            double n = Math.PI - 2.0 * Math.PI * y / (double)(1 << zoom);
            return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
        }
    }
}
