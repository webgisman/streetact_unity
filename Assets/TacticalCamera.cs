using UnityEngine;
using UnityEngine.InputSystem;

public class TacticalCamera : MonoBehaviour
{
    [Header("Mouvement")]
    public float panSpeed = 20f;
    
    [Header("Zoom")]
    public float zoomSpeed = 20f; // Multiplié car scroll de la souris est souvent autour de 120
    public float minHeight = 5f;
    public float maxHeight = 50f;

    [Header("Rotation")]
    public float rotationSensitivity = 0.15f;

    private Vector3 targetPosition;
    private Vector3 currentVelocity;
    
    private float yaw;
    private float pitch;
    private Vector3 rotationPivot;
    private float orbitDistance;
    private bool isRotating = false;

    [Header("Juice & Feel")]
    public float smoothTime = 0.15f; // Temps de lissage (inertie)

    void Start()
    {
        // Forcer un bel angle isométrique au démarrage pour que tout soit beau !
        transform.rotation = Quaternion.Euler(50f, 45f, 0f);
        
        // Récupérer les angles initiaux pour la rotation
        pitch = transform.eulerAngles.x;
        yaw = transform.eulerAngles.y;
        
        // S'assurer qu'on est à une hauteur correcte
        Vector3 pos = transform.position;
        pos.y = Mathf.Clamp(pos.y, minHeight, maxHeight);
        transform.position = pos;
        targetPosition = pos;
    }

    void Update()
    {
        HandlePan();
        HandleZoom();
        HandleRotation();
        
        // Mouvement ultra-fluide avec inertie (Juiciness)
        transform.position = Vector3.SmoothDamp(transform.position, targetPosition, ref currentVelocity, smoothTime);
    }

    private void HandlePan()
    {
        Vector3 move = Vector3.zero;

        // Gestion du clavier (Supporte QWERTY et AZERTY + Flèches)
        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.zKey.isPressed || Keyboard.current.upArrowKey.isPressed) move += Vector3.forward;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) move += Vector3.back;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.qKey.isPressed || Keyboard.current.leftArrowKey.isPressed) move += Vector3.left;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) move += Vector3.right;
        }

        // Gestion du tactile (Glisser pour se déplacer avec 1 doigt)
        if (Touchscreen.current != null && Touchscreen.current.touches.Count == 1)
        {
            var touch = Touchscreen.current.touches[0];
            if (touch.phase.ReadValue() == UnityEngine.InputSystem.TouchPhase.Moved)
            {
                Vector2 delta = touch.delta.ReadValue();
                // Inverser le mouvement pour faire "glisser la carte"
                move += new Vector3(-delta.x, 0, -delta.y) * 0.05f; 
            }
        }

        // Gestion du glisser de souris (Clic molette ou clic droit s'il n'y a pas de rotation)
        if (Mouse.current != null && Mouse.current.middleButton.isPressed)
        {
            Vector2 delta = Mouse.current.delta.ReadValue();
            move += new Vector3(-delta.x, 0, -delta.y) * 0.05f;
        }

        if (move.sqrMagnitude > 0)
        {
            move.Normalize();
            
            // Le mouvement doit être relatif à l'angle de vue de la caméra
            Vector3 forward = transform.forward;
            forward.y = 0;
            forward.Normalize();

            Vector3 right = transform.right;
            right.y = 0;
            right.Normalize();

            Vector3 finalMove = (forward * move.z + right * move.x) * panSpeed * Time.deltaTime;
            // On modifie la cible, pas la position réelle !
            targetPosition += finalMove;
        }
    }

    private void HandleZoom()
    {
        float scrollDelta = 0f;

        if (Mouse.current != null)
        {
            float scroll = Mouse.current.scroll.ReadValue().y;
            if (scroll > 0) scrollDelta = 1f;
            else if (scroll < 0) scrollDelta = -1f;
        }
        
        // Fallback Clavier : Touches R (Zoom In) et F (Zoom Out)
        if (Keyboard.current != null)
        {
            if (Keyboard.current.rKey.isPressed) scrollDelta = 1f;
            if (Keyboard.current.fKey.isPressed) scrollDelta = -1f;
        }

        // Zoom Tactile (Pinch-to-zoom avec 2 doigts)
        if (Touchscreen.current != null && Touchscreen.current.touches.Count == 2)
        {
            var touch0 = Touchscreen.current.touches[0];
            var touch1 = Touchscreen.current.touches[1];

            Vector2 touch0PrevPos = touch0.position.ReadValue() - touch0.delta.ReadValue();
            Vector2 touch1PrevPos = touch1.position.ReadValue() - touch1.delta.ReadValue();

            float prevMagnitude = (touch0PrevPos - touch1PrevPos).magnitude;
            float currentMagnitude = (touch0.position.ReadValue() - touch1.position.ReadValue()).magnitude;

            float difference = currentMagnitude - prevMagnitude;
            scrollDelta = difference * 0.02f; // Ajuster la sensibilité du pincement
        }

        if (scrollDelta != 0)
        {
            Camera cam = GetComponent<Camera>();
            if (cam != null && cam.orthographic)
            {
                float newSize = cam.orthographicSize - scrollDelta * zoomSpeed * 0.5f;
                cam.orthographicSize = Mathf.Clamp(newSize, 5f, 100f);
            }
            else
            {
                Vector3 move = transform.forward * scrollDelta * zoomSpeed * 0.2f;
                // On modifie la cible !
                targetPosition += move;
                targetPosition.y = Mathf.Clamp(targetPosition.y, minHeight, 200f);
            }
        }
    }

    private void HandleRotation()
    {
        bool startRotation = false;
        bool isRotatingInput = false;
        Vector2 rotationDelta = Vector2.zero;

        // Détection Souris (Clic droit)
        if (Mouse.current != null)
        {
            if (Mouse.current.rightButton.wasPressedThisFrame) startRotation = true;
            if (Mouse.current.rightButton.isPressed)
            {
                isRotatingInput = true;
                rotationDelta = Mouse.current.delta.ReadValue();
            }
        }

        // Détection Tactile (Rotation à 3 doigts ou condition spécifique)
        if (Touchscreen.current != null && Touchscreen.current.touches.Count == 3)
        {
            var touch = Touchscreen.current.touches[0];
            if (touch.phase.ReadValue() == UnityEngine.InputSystem.TouchPhase.Began) startRotation = true;
            if (touch.phase.ReadValue() == UnityEngine.InputSystem.TouchPhase.Moved)
            {
                isRotatingInput = true;
                rotationDelta = touch.delta.ReadValue();
            }
        }

        // Quand on commence la rotation, on définit le point pivot au sol
        if (startRotation)
        {
            isRotating = true;
            
            // Lancer un rayon virtuel vers le sol (Y = 0) pour trouver le pivot de rotation
            Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
            Ray ray = new Ray(transform.position, transform.forward);
            if (groundPlane.Raycast(ray, out float enterDistance))
            {
                rotationPivot = ray.GetPoint(enterDistance);
            }
            else
            {
                rotationPivot = transform.position + transform.forward * 20f;
            }
            orbitDistance = Vector3.Distance(transform.position, rotationPivot);
        }

        // Pendant qu'on maintient l'input, on tourne autour du pivot
        if (isRotatingInput && isRotating)
        {
            yaw += rotationDelta.x * rotationSensitivity;
            pitch -= rotationDelta.y * rotationSensitivity;
            
            // Clamper l'inclinaison verticale (pitch) pour ne pas retourner la caméra ou passer sous la carte
            pitch = Mathf.Clamp(pitch, 15f, 85f);

            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.rotation = rotation;

            // Repositionner la cible de la caméra pour conserver la même distance par rapport au pivot
            targetPosition = rotationPivot - (rotation * Vector3.forward * orbitDistance);
        }
        else if (!isRotatingInput)
        {
            isRotating = false;
        }
    }
}
