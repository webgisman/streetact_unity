using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

public class TacticalCamera : MonoBehaviour
{
    [Header("Mouvement & Vitesse")]
    public float panSpeed = 28f;
    public float touchPanSpeed = 0.04f;
    
    [Header("Zoom & Recul")]
    public float zoomSpeed = 25f;
    public float pinchZoomSensitivity = 0.065f;
    public float minHeight = 8f;
    public float maxHeight = 180f; // Recul majeur permettant de voir toute la ville

    [Header("Rotation")]
    public float rotationSensitivity = 0.2f;

    private Vector3 targetPosition;
    private Vector3 currentVelocity;
    
    private float yaw;
    private float pitch;
    private Vector3 rotationPivot;
    private float orbitDistance;
    private bool isRotating = false;

    private float shakeDuration = 0f;
    private float shakeIntensity = 0f;

    [Header("Juice & Feel")]
    public float smoothTime = 0.12f; // Inertie fluide

    void Start()
    {
        // 1. Forcer l'autorotation de l'écran sur mobile
        Screen.orientation = ScreenOrientation.AutoRotation;
        Screen.autorotateToPortrait = true;
        Screen.autorotateToPortraitUpsideDown = false;
        Screen.autorotateToLandscapeLeft = true;
        Screen.autorotateToLandscapeRight = true;

        // 2. Bel angle isométrique de vue d'ensemble avec recul confortable (Y = 65m)
        transform.rotation = Quaternion.Euler(52f, 45f, 0f);
        pitch = 52f;
        yaw = 45f;
        
        Vector3 pos = transform.position;
        if (pos.y < 35f) pos.y = 65f; // Vue haute initiale sur toute la ville
        pos.y = Mathf.Clamp(pos.y, minHeight, maxHeight);
        transform.position = pos;
        targetPosition = pos;
    }

    void Update()
    {
        HandleTouchGestures();
        HandleMouseAndKeyboard();
        
        // Mouvement fluide avec amortissement inertiel
        Vector3 finalPos = Vector3.SmoothDamp(transform.position, targetPosition, ref currentVelocity, smoothTime);
        
        if (shakeDuration > 0)
        {
            finalPos += Random.insideUnitSphere * shakeIntensity;
            shakeDuration -= Time.deltaTime;
        }
        
        transform.position = finalPos;
    }

    public void ShakeCamera(float intensity, float duration)
    {
        shakeIntensity = intensity;
        shakeDuration = duration;
    }

    /// <summary>
    /// Gestion tactile complète multi-touch pour téléphone Android :
    /// - 1 doigt : Glisser pour déplacer la carte (Pan)
    /// - 2 doigts : Pincer pour zoomer/dézoomer + Glisser pour Pan
    /// </summary>
    private void HandleTouchGestures()
    {
        int touchCount = Input.touchCount;
        
        // Fallback InputSystem Touches
        if (touchCount == 0 && Touchscreen.current != null)
        {
            touchCount = Touchscreen.current.touches.Count;
        }

        // -------------------------------------------------------------
        // CAS 1 : DEUX DOIGTS (PINCH TO ZOOM & DEZOOM AUX DOIGTS)
        // -------------------------------------------------------------
        if (touchCount >= 2)
        {
            Vector2 t0Pos, t1Pos;
            Vector2 t0Delta = Vector2.zero, t1Delta = Vector2.zero;

            if (Input.touchCount >= 2)
            {
                Touch t0 = Input.GetTouch(0);
                Touch t1 = Input.GetTouch(1);
                t0Pos = t0.position;
                t1Pos = t1.position;
                t0Delta = t0.deltaPosition;
                t1Delta = t1.deltaPosition;
            }
            else
            {
                var t0 = Touchscreen.current.touches[0];
                var t1 = Touchscreen.current.touches[1];
                t0Pos = t0.position.ReadValue();
                t1Pos = t1.position.ReadValue();
                t0Delta = t0.delta.ReadValue();
                t1Delta = t1.delta.ReadValue();
            }

            // A. Calcul du Zoom (Distance entre les 2 doigts)
            float currentDist = Vector2.Distance(t0Pos, t1Pos);
            Vector2 prevT0 = t0Pos - t0Delta;
            Vector2 prevT1 = t1Pos - t1Delta;
            float prevDist = Vector2.Distance(prevT0, prevT1);
            float distDelta = currentDist - prevDist;

            if (Mathf.Abs(distDelta) > 0.5f)
            {
                float zoomAmount = distDelta * pinchZoomSensitivity * (targetPosition.y / 30f);
                Vector3 zoomMove = transform.forward * zoomAmount;
                targetPosition += zoomMove;
                targetPosition.y = Mathf.Clamp(targetPosition.y, minHeight, maxHeight);
            }

            // B. Déplacement (Pan avec 2 doigts en parallèle)
            Vector2 averageDelta = (t0Delta + t1Delta) * 0.5f;
            if (averageDelta.sqrMagnitude > 1f)
            {
                Vector3 forward = transform.forward; forward.y = 0; forward.Normalize();
                Vector3 right = transform.right; right.y = 0; right.Normalize();
                Vector3 panMove = (-right * averageDelta.x - forward * averageDelta.y) * touchPanSpeed * (targetPosition.y / 25f);
                targetPosition += panMove;
            }

            return;
        }

        // -------------------------------------------------------------
        // CAS 2 : UN SEUL DOIGT (PAN DU TERRAIN)
        // -------------------------------------------------------------
        if (touchCount == 1)
        {
            // Vérifier que le doigt ne clique pas sur un bouton UI ou une unité en planification
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

            Vector2 delta = Vector2.zero;
            bool isMoved = false;

            if (Input.touchCount == 1)
            {
                Touch t = Input.GetTouch(0);
                if (t.phase == UnityEngine.TouchPhase.Moved)
                {
                    delta = t.deltaPosition;
                    isMoved = true;
                }
            }
            else if (Touchscreen.current != null)
            {
                var t = Touchscreen.current.touches[0];
                if (t.phase.ReadValue() == UnityEngine.InputSystem.TouchPhase.Moved)
                {
                    delta = t.delta.ReadValue();
                    isMoved = true;
                }
            }

            // Ne pas déplacer la caméra si le joueur trace un chemin avec une unité sélectionnée
            TacticalPathManager pathMgr = TacticalPathManager.Instance;
            if (pathMgr != null && pathMgr.uniteSelectionnee != null && pathMgr.phaseActuelle == TacticalPathManager.GamePhase.Planification)
            {
                // Si on a une unité active, le pan tactile se fait à 2 doigts pour ne pas gêner le tracé
                return;
            }

            if (isMoved && delta.sqrMagnitude > 0.5f)
            {
                Vector3 forward = transform.forward; forward.y = 0; forward.Normalize();
                Vector3 right = transform.right; right.y = 0; right.Normalize();
                
                float dynamicSpeed = touchPanSpeed * (targetPosition.y / 25f);
                Vector3 panMove = (-right * delta.x - forward * delta.y) * dynamicSpeed;
                targetPosition += panMove;
            }
        }
    }

    /// <summary>
    /// Contrôles PC (Souris & Clavier)
    /// </summary>
    private void HandleMouseAndKeyboard()
    {
        // 1. Clavier Pan
        Vector3 move = Vector3.zero;
        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.zKey.isPressed || Keyboard.current.upArrowKey.isPressed) move += Vector3.forward;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) move += Vector3.back;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.qKey.isPressed || Keyboard.current.leftArrowKey.isPressed) move += Vector3.left;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) move += Vector3.right;
        }

        if (Mouse.current != null && Mouse.current.middleButton.isPressed)
        {
            Vector2 delta = Mouse.current.delta.ReadValue();
            move += new Vector3(-delta.x, 0, -delta.y) * 0.05f;
        }

        if (move.sqrMagnitude > 0)
        {
            move.Normalize();
            Vector3 forward = transform.forward; forward.y = 0; forward.Normalize();
            Vector3 right = transform.right; right.y = 0; right.Normalize();
            targetPosition += (forward * move.z + right * move.x) * panSpeed * (targetPosition.y / 30f) * Time.deltaTime;
        }

        // 2. Molette Zoom PC
        float scrollDelta = 0f;
        if (Mouse.current != null)
        {
            float scroll = Mouse.current.scroll.ReadValue().y;
            if (scroll > 0) scrollDelta = 1f;
            else if (scroll < 0) scrollDelta = -1f;
        }
        if (Keyboard.current != null)
        {
            if (Keyboard.current.rKey.isPressed) scrollDelta = 1f;
            if (Keyboard.current.fKey.isPressed) scrollDelta = -1f;
        }

        if (scrollDelta != 0)
        {
            Vector3 zoomMove = transform.forward * scrollDelta * zoomSpeed * 0.25f;
            targetPosition += zoomMove;
            targetPosition.y = Mathf.Clamp(targetPosition.y, minHeight, maxHeight);
        }

        // 3. Clic Droit Rotation PC
        if (Mouse.current != null)
        {
            if (Mouse.current.rightButton.wasPressedThisFrame)
            {
                isRotating = true;
                Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
                Ray ray = new Ray(transform.position, transform.forward);
                if (groundPlane.Raycast(ray, out float enterDistance))
                {
                    rotationPivot = ray.GetPoint(enterDistance);
                }
                else
                {
                    rotationPivot = transform.position + transform.forward * 25f;
                }
                orbitDistance = Vector3.Distance(transform.position, rotationPivot);
            }

            if (Mouse.current.rightButton.isPressed && isRotating)
            {
                Vector2 delta = Mouse.current.delta.ReadValue();
                yaw += delta.x * rotationSensitivity;
                pitch -= delta.y * rotationSensitivity;
                pitch = Mathf.Clamp(pitch, 15f, 85f);

                Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
                transform.rotation = rotation;
                targetPosition = rotationPivot - (rotation * Vector3.forward * orbitDistance);
            }
            else if (Mouse.current.rightButton.wasReleasedThisFrame)
            {
                isRotating = false;
            }
        }
    }
}
