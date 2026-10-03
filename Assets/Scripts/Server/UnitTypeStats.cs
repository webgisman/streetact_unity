namespace Novgov.Server
{
    /// <summary>Statistiques par type d'unité — extraites AU MOT PRÈS de UnitAI.cs (Start() :
    /// santé/portée par type) et du moteur de combat serveur (dégâts/cadence de tir, déjà en dur là-bas,
    /// jamais lus depuis un prefab). Un seul et même barème pour le déploiement serveur ET le calcul de
    /// budget de déplacement côté client (TacticalAIPlanner/TacticalPathManager_PathDrawing), jamais
    /// dupliqué ailleurs.
    ///
    /// 2026-09-13 : ce fichier s'appelait MatchState.cs et portait aussi les classes MatchState/
    /// MatchZoneState/MatchGeometry — toutes supprimées avec le reste de la famille "Pure" (voir
    /// l'historique git). Renommé pour refléter qu'il ne reste que ceci, seule
    /// partie encore utilisée par du code client (Solo) en plus du serveur.</summary>
    public static class UnitTypeStats
    {
        public static void Get(UnitSpawnerUI.UnitType type, out int health, out float porteeDetection, out int weaponDamage, out float weaponCooldownSeconds, out bool isMortar, out bool isTank)
        {
            isTank = type == UnitSpawnerUI.UnitType.CharLeopard;
            switch (type)
            {
                case UnitSpawnerUI.UnitType.CharLeopard:
                    health = 500; porteeDetection = 45f; weaponDamage = 150; weaponCooldownSeconds = 1.8f; isMortar = false;
                    break;
                case UnitSpawnerUI.UnitType.VehiculeCanon:
                    health = 250; porteeDetection = 45f; weaponDamage = 75; weaponCooldownSeconds = 1.4f; isMortar = false;
                    break;
                case UnitSpawnerUI.UnitType.Mortier:
                    health = 350; porteeDetection = 120f; weaponDamage = 0; weaponCooldownSeconds = 0f; isMortar = true;
                    break;
                default: // Fantassin
                    health = 100; porteeDetection = 15f; weaponDamage = 15; weaponCooldownSeconds = 0.35f; isMortar = false;
                    break;
            }
        }

        /// <summary>Distance maximale parcourue en un tour, en mètres. Source UNIQUE, partagée par le
        /// serveur (déploiement, moteur réel) et le client (TacticalAIPlanner, aperçu de trajectoire).
        ///
        /// Les blindés sont un peu plus lents que l'infanterie, ce qui correspond à leurs vitesses de
        /// déplacement respectives (UnitAI.OnNavMeshReady : 3.8 contre 4.2 m/s pour la traversée
        /// directe) et donne une raison tactique de plus de faire avancer l'infanterie en tête.</summary>
        public static float MovementBudget(UnitSpawnerUI.UnitType type)
        {
            switch (type)
            {
                case UnitSpawnerUI.UnitType.CharLeopard: return 42f;
                case UnitSpawnerUI.UnitType.VehiculeCanon: return 46f;
                case UnitSpawnerUI.UnitType.Mortier: return 34f; // pièce lourde à remettre en batterie
                default: return UnitAI.DefaultMaxMovementPerTurn; // Fantassin : 50m, la référence
            }
        }

        /// <summary>Type d'unité déduit des drapeaux d'une UnitAI. Le CLIENT et le serveur interrogent
        /// le même barème : l'aperçu de trajectoire colore la portion hors budget
        /// (TacticalPathManager_PathDrawing), et une valeur qui divergerait de celle du serveur
        /// referait exactement le bug qu'elle est censée rendre visible.
        /// L'ordre des tests est significatif : isTank est aussi vrai pour le véhicule canon et le
        /// mortier (voir UnitAI.Awake), donc les cas spécifiques passent en premier.</summary>
        public static UnitSpawnerUI.UnitType InferType(UnitAI u)
        {
            if (u == null) return UnitSpawnerUI.UnitType.Fantassin;
            if (u.isMortar) return UnitSpawnerUI.UnitType.Mortier;
            if (u.isCanonVehicle) return UnitSpawnerUI.UnitType.VehiculeCanon;
            if (u.isTank) return UnitSpawnerUI.UnitType.CharLeopard;
            return UnitSpawnerUI.UnitType.Fantassin;
        }

        /// <summary>Budget de déplacement d'une UnitAI — raccourci de MovementBudget(InferType(u)).</summary>
        public static float MovementBudgetFor(UnitAI u) => MovementBudget(InferType(u));
    }
}
