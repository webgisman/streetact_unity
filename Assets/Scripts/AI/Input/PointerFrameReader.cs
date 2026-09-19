using UnityEngine;
using UnityEngine.InputSystem;

namespace Novgov.Gestures
{
    /// <summary>Un instantané du pointeur pour la frame courante — position + transitions
    /// appui/relâchement, indépendamment du périphérique physique qui l'a produit.</summary>
    public struct PointerFrame
    {
        public Vector2 position;
        public bool isActive;
        public bool wasPressed;
        public bool wasReleased;
    }

    /// <summary>Lecture universelle du pointeur (Tactile Mobile & Souris PC), extraite de
    /// TacticalPathManager_Input.HandlePointerInput (2026-09-19) pour isoler ce point unique de
    /// priorité entre périphériques : tactile d'abord (mobile réel), puis souris (PC/Éditeur), puis
    /// tout autre Pointer générique en dernier repli. Ne décide de rien d'autre — ni tap, ni
    /// sélection — pour rester réutilisable partout où le jeu a besoin de "où est le doigt/curseur
    /// maintenant".</summary>
    public static class PointerFrameReader
    {
        public static PointerFrame Read()
        {
            if (Touchscreen.current != null && Touchscreen.current.touches.Count > 0)
            {
                var touch = Touchscreen.current.touches[0];
                return new PointerFrame
                {
                    position = touch.position.ReadValue(),
                    isActive = true,
                    wasPressed = touch.press.wasPressedThisFrame,
                    wasReleased = touch.press.wasReleasedThisFrame
                };
            }

            if (Mouse.current != null)
            {
                return new PointerFrame
                {
                    position = Mouse.current.position.ReadValue(),
                    isActive = true,
                    wasPressed = Mouse.current.leftButton.wasPressedThisFrame,
                    wasReleased = Mouse.current.leftButton.wasReleasedThisFrame
                };
            }

            if (Pointer.current != null)
            {
                return new PointerFrame
                {
                    position = Pointer.current.position.ReadValue(),
                    isActive = true,
                    wasPressed = Pointer.current.press.wasPressedThisFrame,
                    wasReleased = Pointer.current.press.wasReleasedThisFrame
                };
            }

            return default;
        }
    }
}
