using UnityEngine;

namespace Novgov.Gestures
{
    /// <summary>Arbitre quelle unité alliée un tap doit sélectionner. Un raycast DIRECT sur une unité gagne
    /// toujours ; la recherche tolérante par distance-écran n'intervient que si le raycast n'a touché
    /// aucune unité du joueur (tap légèrement à côté). L'inverse faisait voler la sélection d'un
    /// fantassin par un char voisin (2026-09-19).</summary>
    public static class UnitSelectionResolver
    {
        // Bonus de tolérance (px, avant mise à l'échelle DPI) réservé aux unités NON blindées
        // (fantassin) — retour joueur du 2026-09-19 : une fois qu'un char/canon est sélectionné, le
        // rayon tolérant se resserre à 60px (voir HandlePointerInput, isGivingOrderToSelectedUnit)
        // pour éviter qu'un tap approximatif ne vole un ordre à une autre unité par erreur ; mais un
        // fantassin, structurellement plus petit à l'écran qu'un char/canon (voir la doc de cette
        // classe), est aussi structurellement plus dur à toucher dans ce rayon resserré — un tap qui
        // le "rate" de peu tombe alors sur le sol/rue juste à côté et redonne un ordre à l'unité déjà
        // sélectionnée au lieu de rien faire, ce qui se vit comme "le fantassin ne se sélectionne
        // jamais". Ce bonus ne s'applique qu'aux unités NON blindées : un char/canon a déjà un grand
        // collider qui gagne presque toujours via le raycast direct ci-dessus, aucune raison de lui
        // donner plus de tolérance ici.
        private const float InfantryTouchToleranceBonusPx = 25f;

        public static UnitAI Resolve(Vector2 pointerPosition, Camera camera, UnitAI raycastUnit, float maxTouchRadiusPx)
        {
            // Vérité de terrain : ce raycast a physiquement touché le collider de cette unité. Rien
            // ne doit pouvoir le supplanter — voir la doc de la classe pour l'historique du bug que
            // cela corrige à la racine.
            if (raycastUnit != null) return raycastUnit;

            UnitAI closestUnit = null;
            float closestScreenDist = float.MaxValue;

            for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
            {
                UnitAI unit = UnitAI.AllLivingUnits[i];
                if (unit == null || !unit.isPlayerControlled || unit.isDead) continue;

                Vector3 screenPoint = camera.WorldToScreenPoint(unit.SelectionAnchorWorldPos);
                if (screenPoint.z <= 0) continue; // Derrière la caméra.

                float dist = Vector2.Distance(pointerPosition, new Vector2(screenPoint.x, screenPoint.y));
                float tolerance = maxTouchRadiusPx + (unit.isTank ? 0f : InfantryTouchToleranceBonusPx * GestureScale.TouchDpiScale);
                if (dist <= tolerance && dist < closestScreenDist)
                {
                    closestScreenDist = dist;
                    closestUnit = unit;
                }
            }

            return closestUnit;
        }
    }
}
