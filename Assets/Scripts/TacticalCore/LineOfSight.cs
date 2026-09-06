using System.Collections.Generic;
using UnityEngine;

namespace Novgov.TacticalCore
{
    /// <summary>
    /// Trois règles de ligne de vue DISTINCTES, fidèles à l'audit du code solo (2026-08-30) — à ne
    /// jamais fusionner naïvement, l'ancien code ne les traite pas pareil :
    ///   - HasLineOfSight  : blocage tout-ou-rien, aucune exception — MortarShell.HasWallBetween.
    ///   - CanBeSpotted    : IsUnitSpottedByTeam — portée de repérage + exception "sniper de toit
    ///                       à moins de 2m d'un obstacle" (le parapet du toit ne bloque pas).
    ///   - CanEngageTarget : GetVisibleEnemy — portée d'engagement, cône de fenêtre (-0.1 de
    ///                       produit scalaire), et 3 exceptions de "tir à travers une ouverture"
    ///                       (CAS1/2/3 du rapport d'audit, seuil 2.5m).
    /// </summary>
    public static class LineOfSight
    {
        /// <summary>Blocage strict, sans exception — utilisé par les dégâts de zone (mortier sur
        /// les unités, voir TacticalResolver.ApplyAreaDamage) : soit la ligne est dégagée, soit le
        /// souffle est intercepté en totalité.</summary>
        public static bool HasLineOfSight(TacticalWorldState state, Vector2 from, Vector2 to, VisionType visionType)
        {
            foreach (var wall in state.wallSegments)
            {
                if (!wall.BlocksSight(state)) continue;
                if (GeometryMath.SegmentsIntersect(from, to, wall.p1, wall.p2)) return false;
            }
            foreach (var barricade in state.barricades)
            {
                if (!barricade.BlocksSight) continue;
                if (GeometryMath.SegmentsIntersect(from, to, barricade.p1, barricade.p2)) return false;
            }
            return true;
        }

        /// <summary>IsUnitSpottedByTeam (UnitAI_Combat.cs:197-237) : un OBSERVATEUR quelconque
        /// (pas forcément le tireur) peut-il repérer cette cible ? Portée par type d'observateur,
        /// camouflage/mort = jamais repéré, exception "sniper de toit près d'un obstacle" (le
        /// parapet à moins de 2m devant lui ne compte pas comme bloquant).</summary>
        public static bool CanBeSpotted(TacticalWorldState state, TacticalUnit observer, TacticalUnit target)
        {
            if (target.isDead || target.isCamouflaged) return false;

            float maxSight = observer.zStrata == ZStrata.Toit ? 55f : (observer.isMortar ? 25f : 35f);
            if (GeometryMath.SqrDistance(observer.position, target.position) > maxSight * maxSight) return false;

            float fullDist = Vector2.Distance(observer.position, target.position);

            foreach (var wall in state.wallSegments)
            {
                if (!wall.BlocksSight(state)) continue;
                if (!SegmentHit(observer.position, target.position, wall.p1, wall.p2, out float hitDist)) continue;

                // Exception "parapet" : le mur du bâtiment sur lequel la cible est perchée ne la cache
                // pas — elle est en hauteur, au-dessus de ce mur.
                //
                // Le test portait sur la seule PROXIMITÉ de l'impact (moins de 2m de la cible), ce qui
                // ne fonctionnait que si l'unité se tenait quasiment contre le bord du toit. Posée au
                // centre du toit — exactement là que visent le menu contextuel et TacticalAIPlanner —
                // la ligne de vue croisait sa propre façade à une demi-largeur de bâtiment, donc bien
                // au-delà de 2m : l'unité devenait INVISIBLE pour l'adversaire, et par symétrie
                // (CanEngageTarget) invulnérable. Se poster sur un toit était une stratégie sans
                // risque, et le repérage dépendait invisiblement de l'endroit exact du toit tapé.
                if (target.zStrata == ZStrata.Toit && WallBelongsToOwnBuilding(wall, target, hitDist, fullDist)) continue;
                return false;
            }
            foreach (var barricade in state.barricades)
            {
                if (!barricade.BlocksSight) continue;
                if (GeometryMath.SegmentsIntersect(observer.position, target.position, barricade.p1, barricade.p2)) return false;
            }
            return true;
        }

        /// <summary>Ce mur est-il celui du bâtiment sur lequel l'unité perchée se tient — donc un mur
        /// qu'elle surplombe et qui ne la dissimule pas ? On se fie d'abord au rattachement explicite
        /// (currentBuildingId, posé par le checkpoint d'Escalade). Repli sur l'ancienne règle de
        /// proximité quand ce rattachement est absent (unité héritée d'un tour antérieur, ou toit sans
        /// bâtiment identifié) : mieux vaut la tolérance historique qu'une unité soudain invisible.</summary>
        private static bool WallBelongsToOwnBuilding(WallSegment wall, TacticalUnit perched, float hitDist, float fullDist)
        {
            if (perched.currentBuildingId >= 0) return wall.buildingId == perched.currentBuildingId;
            return hitDist > fullDist - 2.5f;
        }

        /// <summary>GetVisibleEnemy (UnitAI_Combat.cs:242-339) : LE TIREUR peut-il engager cette
        /// cible personnellement ? Distincte de CanBeSpotted (portées et exceptions différentes).
        /// Le mortier ne passe JAMAIS par ici (voir TacticalResolver.ResolveEngagements, un
        /// mortier n'engage qu'en zone via TirMortier).</summary>
        public static bool CanEngageTarget(TacticalWorldState state, TacticalUnit shooter, TacticalUnit target)
        {
            if (shooter.isDead || target.isDead || target.isCamouflaged) return false;

            float maxRange = shooter.zStrata == ZStrata.Toit ? shooter.engagementRange + 20f : shooter.engagementRange;
            if (GeometryMath.SqrDistance(shooter.position, target.position) > maxRange * maxRange) return false;

            // Cône de fenêtre (ligne 278-281) : ~140° centrés sur la normale de la fenêtre —
            // seuil exact -0.1 sur le produit scalaire, pas un angle.
            if (shooter.isGarrisoned && shooter.windowNormal.HasValue)
            {
                Vector2 dirToTarget = (target.position - shooter.position);
                if (dirToTarget.sqrMagnitude > 0.0001f)
                {
                    dirToTarget.Normalize();
                    float dot = shooter.windowNormal.Value.x * dirToTarget.x + shooter.windowNormal.Value.y * dirToTarget.y;
                    if (dot < -0.1f) return false;
                }
            }

            float fullDist = Vector2.Distance(shooter.position, target.position);

            foreach (var wall in state.wallSegments)
            {
                if (!wall.BlocksSight(state)) continue;
                if (!SegmentHit(shooter.position, target.position, wall.p1, wall.p2, out float hitDist)) continue;

                // CAS1 (ligne 297) : le tireur posté (garnison/toit/intérieur) peut tirer à travers
                // SON PROPRE mur — soit le mur appartient à son bâtiment, soit l'impact est à moins
                // de 2.5m de lui (l'embrasure).
                bool shooterPosted = shooter.isGarrisoned || shooter.zStrata == ZStrata.Toit || shooter.currentBuildingId >= 0;
                if (shooterPosted && (wall.buildingId == shooter.currentBuildingId || hitDist < 2.5f)) continue;

                // CAS2/CAS3 (lignes 300-322) : la cible postée (garnison/bâtiment/toit) n'est pas
                // protégée par SON PROPRE mur si l'impact tombe à moins de 2.5m d'elle (la balle
                // passe par l'ouverture).
                bool targetPosted = target.isGarrisoned || target.currentBuildingId >= 0 || target.zStrata == ZStrata.Toit;
                if (targetPosted && wall.buildingId == target.currentBuildingId && hitDist > fullDist - 2.5f) continue;
                // Symétrique de l'exception "parapet" de CanBeSpotted : une cible perchée n'est pas
                // protégée par le mur de SON propre bâtiment (elle est au-dessus). Sans cette symétrie,
                // une unité repérable restait malgré tout impossible à toucher.
                if (targetPosted && target.zStrata == ZStrata.Toit && WallBelongsToOwnBuilding(wall, target, hitDist, fullDist)) continue;

                return false;
            }
            foreach (var barricade in state.barricades)
            {
                if (!barricade.BlocksSight) continue;
                if (GeometryMath.SegmentsIntersect(shooter.position, target.position, barricade.p1, barricade.p2)) return false;
            }
            return true;
        }

        /// <summary>Comme GeometryMath.SegmentsIntersect, mais renvoie en plus la distance depuis
        /// "from" jusqu'au point d'intersection — nécessaire pour les exceptions "à moins de Xm de
        /// l'un des deux bouts" ci-dessus, sans jamais diviser (donc sans risque d'instabilité
        /// numérique près d'un segment quasi-parallèle) : on calcule la distance au point projeté
        /// une fois l'intersection déjà confirmée par produit en croix.</summary>
        private static bool SegmentHit(Vector2 from, Vector2 to, Vector2 wallA, Vector2 wallB, out float hitDistanceFromStart)
        {
            hitDistanceFromStart = 0f;
            if (!GeometryMath.SegmentsIntersect(from, to, wallA, wallB)) return false;

            // Intersection déjà confirmée : calcule le point exact (une seule division, sûre ici
            // puisque les segments sont garantis non-parallèles par le test ci-dessus).
            Vector2 d1 = to - from;
            Vector2 d2 = wallB - wallA;
            float denom = d1.x * d2.y - d1.y * d2.x;
            if (Mathf.Abs(denom) < 0.000001f) { hitDistanceFromStart = Vector2.Distance(from, to); return true; }

            Vector2 diff = wallA - from;
            float t = (diff.x * d2.y - diff.y * d2.x) / denom;
            Vector2 hitPoint = from + d1 * t;
            hitDistanceFromStart = Vector2.Distance(from, hitPoint);
            return true;
        }
    }
}
