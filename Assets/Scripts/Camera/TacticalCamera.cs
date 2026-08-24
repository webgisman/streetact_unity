using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

/// <summary>
/// Caméra Tactique Haute Performance (2D & 3D).
/// - 2D : Vision panoramique haute définition, zoom orthographique fluide (40f à 180f), recul optimal.
/// - 3D : Vue isométrique ample et immersive (recul 65m, rotation 360° fluide).
/// - LateUpdate() : Élimine toute micro-saccade et conflit de frame rate.
/// </summary>
public class TacticalCamera : MonoBehaviour
{
    public static TacticalCamera Instance { get; private set; }

    [Header("Position & Cible")]
    public Vector3 focusPosition;
    public float minDistance = 25f;
    public float maxDistance = 180f;
    public float currentDistance = 65f;
    private float targetDistance = 65f;

    [Header("Zoom 2D (Orthographique)")]
    public float minOrthoSize = 40f;
    public float maxOrthoSize = 180f;
    public float currentOrthoSize = 95f;
    private float targetOrthoSize = 95f;

    [Header("Orientation (Angles)")]
    public float minPitch = 25f;
    public float maxPitch = 75f;
    public float currentPitch = 50f;
    private float targetPitch = 50f;
    public float currentYaw = 45f;
    private float smoothYaw = 45f;

    [Header("Sensibilités")]
    public float panSpeed = 45f;
    public float touchPanSensitivity = 0.045f;
    public float pinchZoomSensitivity = 0.08f;
    public float twistRotationSensitivity = 1.0f;
    public float pitchSensitivity = 0.08f;
    public float panFriction = 7.0f;

    private Vector3 panVelocity = Vector3.zero;
    private bool isTouching = false;
    private float shakeDuration = 0f;
    private float shakeIntensity = 0f;

    private Camera camComponent;

    void Awake()
    {
        Instance = this;
        camComponent = GetComponent<Camera>() ?? Camera.main;
        EnhancedTouchSupport.Enable();
    }

    void OnEnable()
    {
        EnhancedTouchSupport.Enable();
    }

    void OnDisable()
    {
        EnhancedTouchSupport.Disable();
    }

    void Start()
    {
        Screen.orientation = ScreenOrientation.AutoRotation;
        Screen.autorotateToPortrait = true;
        Screen.autorotateToPortraitUpsideDown = false;
        Screen.autorotateToLandscapeLeft = true;
        Screen.autorotateToLandscapeRight = true;

        if (CameraStateManager.Instance != null)
        {
            currentOrthoSize = CameraStateManager.Instance.commandOrthographicSize;
            targetOrthoSize = currentOrthoSize;
            currentDistance = CameraStateManager.Instance.actionDistance;
            targetDistance = currentDistance;
        }

        // Trouver le centre de la carte
        Plane ground = new Plane(Vector3.up, Vector3.zero);
        Ray ray = new Ray(transform.position, transform.forward);
        if (ground.Raycast(ray, out float enter))
        {
            focusPosition = ray.GetPoint(enter);
        }
        else
        {
            focusPosition = Vector3.zero;
        }

        targetPitch = currentPitch;
        smoothYaw = currentYaw;

        if (gameObject.GetComponent<CameraStateManager>() == null)
        {
            gameObject.AddComponent<CameraStateManager>();
        }
    }

    void Update()
    {
        HandleTouchInput();
        HandleKeyboardAndMouse();

        // Application de l'inertie de glissement (Pan)
        if (!isTouching && panVelocity.sqrMagnitude > 0.01f)
        {
            focusPosition += panVelocity * Time.unscaledDeltaTime;
            panVelocity = Vector3.Lerp(panVelocity, Vector3.zero, Time.unscaledDeltaTime * panFriction);
        }
    }

    /// <summary>
    /// Positionnement dans LateUpdate pour garantir zéro saccade avec les animations et la physique.
    /// </summary>
    void LateUpdate()
    {
        float dt = Time.unscaledDeltaTime;
        float lerpFactor = Mathf.Clamp01(dt * 14f);

        currentDistance = Mathf.Lerp(currentDistance, targetDistance, lerpFactor);
        currentPitch = Mathf.Lerp(currentPitch, targetPitch, lerpFactor);
        smoothYaw = Mathf.LerpAngle(smoothYaw, currentYaw, lerpFactor);
        currentOrthoSize = Mathf.Lerp(currentOrthoSize, targetOrthoSize, lerpFactor);

        Quaternion targetRot = Quaternion.Euler(currentPitch, smoothYaw, 0f);
        Vector3 camPos = focusPosition - (targetRot * Vector3.forward * currentDistance);

        if (shakeDuration > 0)
        {
            camPos += Random.insideUnitSphere * shakeIntensity;
            shakeDuration -= dt;
        }

        transform.rotation = targetRot;
        transform.position = camPos;

        if (camComponent != null && camComponent.orthographic)
        {
            camComponent.orthographicSize = currentOrthoSize;
        }
    }

    public void ShakeCamera(float intensity, float duration)
    {
        shakeIntensity = intensity;
        shakeDuration = duration;
    }

    public void ZoomIn(float amount = 15f)
    {
        if (camComponent != null && camComponent.orthographic)
        {
            targetOrthoSize = Mathf.Clamp(targetOrthoSize - amount, minOrthoSize, maxOrthoSize);
        }
        else
        {
            targetDistance = Mathf.Clamp(targetDistance - amount, minDistance, maxDistance);
        }
    }

    public void ZoomOut(float amount = 15f)
    {
        if (camComponent != null && camComponent.orthographic)
        {
            targetOrthoSize = Mathf.Clamp(targetOrthoSize + amount, minOrthoSize, maxOrthoSize);
        }
        else
        {
            targetDistance = Mathf.Clamp(targetDistance + amount, minDistance, maxDistance);
        }
    }

    public void SetInstantView(float distance, float pitch)
    {
        targetDistance = Mathf.Clamp(distance, minDistance, maxDistance);
        currentDistance = targetDistance;
        targetPitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        currentPitch = targetPitch;

        if (CameraStateManager.Instance != null && CameraStateManager.Instance.CurrentState == CameraStateManager.CameraState.Command)
        {
            targetOrthoSize = CameraStateManager.Instance.commandOrthographicSize;
            currentOrthoSize = targetOrthoSize;
        }
    }

    public void Rotate45Deg()
    {
        currentYaw = (currentYaw + 45f) % 360f;
    }

    public void TogglePitchMode()
    {
        if (targetPitch > 50f) targetPitch = 30f;
        else targetPitch = 65f;
    }

    public void CenterOnPlayerUnits()
    {
        Vector3 center = Vector3.zero;
        int count = 0;
        for (int i = 0; i < UnitAI.AllLivingUnits.Count; i++)
        {
            var u = UnitAI.AllLivingUnits[i];
            if (u != null && u.isPlayerControlled && !u.isDead)
            {
                center += u.transform.position;
                count++;
            }
        }
        if (count > 0)
        {
            focusPosition = center / count;
        }
    }

    private void HandleTouchInput()
    {
        var activeTouches = Touch.activeTouches;
        int count = activeTouches.Count;

        // CAS 1 : DEUX DOIGTS (PINCH ZOOM, ROTATION 360°, PAN)
        if (count >= 2)
        {
            isTouching = true;
            panVelocity = Vector3.zero;

            var t0 = activeTouches[0];
            var t1 = activeTouches[1];

            Vector2 p0 = t0.screenPosition;
            Vector2 p1 = t1.screenPosition;

            Vector2 prev0 = p0 - t0.delta;
            Vector2 prev1 = p1 - t1.delta;

            // 1. PINCH ZOOM
            float prevDist = Vector2.Distance(prev0, prev1);
            float curDist = Vector2.Distance(p0, p1);
            float pinchDelta = (curDist - prevDist);

            float dpiScale = (Screen.dpi > 0) ? (Screen.dpi / 160f) : 1f;
            float pinchThreshold = 1.2f * dpiScale;

            if (Mathf.Abs(pinchDelta) > pinchThreshold)
            {
                if (camComponent != null && camComponent.orthographic)
                {
                    targetOrthoSize = Mathf.Clamp(targetOrthoSize - pinchDelta * pinchZoomSensitivity, minOrthoSize, maxOrthoSize);
                }
                else
                {
                    targetDistance = Mathf.Clamp(targetDistance - pinchDelta * pinchZoomSensitivity, minDistance, maxDistance);
                }
            }

            // 2. ROTATION 360° INTUITIVE
            Vector2 curVec = p1 - p0;
            Vector2 prevVec = prev1 - prev0;
            float rotThresholdSq = 400f * dpiScale * dpiScale;
            
            if (curVec.sqrMagnitude > rotThresholdSq && prevVec.sqrMagnitude > rotThresholdSq)
            {
                float angleDelta = Vector2.SignedAngle(prevVec, curVec);
                if (Mathf.Abs(angleDelta) > 0.08f)
                {
                    currentYaw += angleDelta * twistRotationSensitivity;
                }
            }

            // 3. PAN COMBINÉ
            Vector2 avgDelta = (t0.delta + t1.delta) * 0.5f;
            if (avgDelta.sqrMagnitude > 0.6f)
            {
                Vector3 forward = transform.forward; forward.y = 0; forward.Normalize();
                Vector3 right = transform.right; right.y = 0; right.Normalize();
                
                float scale = (camComponent != null && camComponent.orthographic) ? (currentOrthoSize / 50f) : (targetDistance / 50f);
                float moveScale = scale * touchPanSensitivity * 1.1f;
                focusPosition += (-right * avgDelta.x - forward * avgDelta.y) * moveScale;
            }

            return;
        }

        // CAS 2 : UN SEUL DOIGT (DÉPLACEMENT DE CARTE FLUIDE)
        if (count == 1)
        {
            var t0 = activeTouches[0];
            Vector2 pos = t0.screenPosition;
            Vector2 delta = t0.delta;

            if (UnitSpawnerUI.Instance != null && UnitSpawnerUI.Instance.IsPointerOverOnGUI(pos))
            {
                isTouching = false;
                return;
            }

            isTouching = true;

            if (delta.sqrMagnitude > 0.1f)
            {
                Vector3 forward = transform.forward; forward.y = 0; forward.Normalize();
                Vector3 right = transform.right; right.y = 0; right.Normalize();

                float scale = (camComponent != null && camComponent.orthographic) ? (currentOrthoSize / 50f) : (targetDistance / 50f);
                float moveScale = scale * touchPanSensitivity;
                Vector3 move = (-right * delta.x - forward * delta.y) * moveScale;
                focusPosition += move;

                if (Time.unscaledDeltaTime > 0.0001f)
                {
                    float dt = Mathf.Max(Time.unscaledDeltaTime, 0.005f);
                    panVelocity = move / dt;
                    panVelocity = Vector3.ClampMagnitude(panVelocity, 140f);
                }
            }
            return;
        }

        isTouching = false;
    }

    private void HandleKeyboardAndMouse()
    {
        Vector3 move = Vector3.zero;
        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) move += Vector3.forward;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) move += Vector3.back;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) move += Vector3.left;
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
            float scale = (camComponent != null && camComponent.orthographic) ? (currentOrthoSize / 50f) : (targetDistance / 50f);
            focusPosition += (forward * move.z + right * move.x) * panSpeed * scale * Time.unscaledDeltaTime;
        }

        // Molette Zoom
        if (Mouse.current != null)
        {
            float scroll = Mouse.current.scroll.ReadValue().y;
            if (scroll > 0) ZoomIn(8f);
            else if (scroll < 0) ZoomOut(8f);
        }

        if (Keyboard.current != null)
        {
            if (Keyboard.current.rKey.isPressed) ZoomIn(40f * Time.unscaledDeltaTime);
            if (Keyboard.current.fKey.isPressed) ZoomOut(40f * Time.unscaledDeltaTime);
            if (Keyboard.current.qKey.isPressed) currentYaw -= 90f * Time.unscaledDeltaTime;
            if (Keyboard.current.eKey.isPressed) currentYaw += 90f * Time.unscaledDeltaTime;
        }

        if (Mouse.current != null && Mouse.current.rightButton.isPressed)
        {
            Vector2 delta = Mouse.current.delta.ReadValue();
            currentYaw -= delta.x * 0.25f;
            targetPitch = Mathf.Clamp(targetPitch - delta.y * 0.2f, minPitch, maxPitch);
        }
    }
}
