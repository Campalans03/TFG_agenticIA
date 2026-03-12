using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Policies;

/// <summary>
/// The Listener moves through the 3D environment, detects buttons via raycasts
/// and presses the one it believes is correct based on the token received from the Speaker.
///
/// DISCRETE ACTIONS:
///   Branch 0 – Movement    (5 values)
///     0 = none
///     1 = move forward
///     2 = move backward
///     3 = rotate left
///     4 = rotate right
///
///   Branch 1 – Press?      (2 values)
///     0 = do not press
///     1 = press the closest button within pressDistance
///
/// OBSERVATIONS (total = 32 + vocabSize floats, all positions in env-local space):
///   Per button (×3): color one-hot(3) + shape one-hot(3) + detected(1)
///                    + agent-relative XZ direction(2) + normalized distance(1)
///                  = 10 floats  →  30 floats total
///   Normalized forward velocity(1) + normalized angular velocity(1) = 2 floats
///   Speaker token: one-hot(vocabSize)
///   Total: 32 + vocabSize
/// </summary>
public class ListenerAgent : Agent
{
    // ─────────────────────────────────────────────────────────────
    //  Inspector
    // ─────────────────────────────────────────────────────────────

    public EnvironmentManager env;

    [Header("Movement")]
    public float moveSpeed   = 3f;
    public float rotateSpeed = 120f;

    [Header("Press distance")]
    public float pressDistance = 0.5f;

    [Header("Raycast Settings")]
    public Transform raycastOrigin;
    public float     raycastDistance = 5f;
    public LayerMask buttonLayer;
    public string    buttonTag = "Button";

    [Tooltip("Horizontal sweep angle on each side (degrees).")]
    public float scanHalfAngle  = 90f;
    [Tooltip("Angular step between rays. Higher value = fewer rays = faster.")]
    public float horizontalStep = 20f;   

    [Header("Spawn (reset position)")]
    public Transform startPosition;

    // ─────────────────────────────────────────────────────────────
    //  Private state
    // ─────────────────────────────────────────────────────────────

    private struct ScannedButton
    {
        public int colorIndex;
        public int shapeIndex;
        public bool detected;
    }

    private ScannedButton[] _scanned = new ScannedButton[3];
    private Rigidbody _rb;
    private int _moveAction;
    private BehaviorParameters _behaviorParams;
    private readonly HashSet<ButtonController> _seenButtons = new HashSet<ButtonController>();

    // ─────────────────────────────────────────────────────────────
    //  Visual feedback — MaterialPropertyBlock (URP/Lit _BaseColor)
    // ─────────────────────────────────────────────────────────────

    [Header("Visual Feedback")]
    [Tooltip("Renderers to flash. Leave empty to auto-collect from this GameObject and children.")]
    public Renderer[] debugRenderers;
    [Tooltip("Seconds the flash colour stays on screen.")]
    public float flashDuration = 0.1f;

    private MaterialPropertyBlock _mpb;
    private static readonly int   BaseColorID = Shader.PropertyToID("_BaseColor");
    private Color _neutralColor;
    private Coroutine _flashRoutine;

    // ─────────────────────────────────────────────────────────────
    //  ML-Agents lifecycle
    // ─────────────────────────────────────────────────────────────

    public override void Initialize()
    {
        _rb             = GetComponent<Rigidbody>();
        _behaviorParams = GetComponent<BehaviorParameters>();

        // Auto-collect renderers if none assigned in the Inspector
        if (debugRenderers == null || debugRenderers.Length == 0)
            debugRenderers = GetComponentsInChildren<Renderer>();

        _mpb = new MaterialPropertyBlock();

        // Read the neutral colour from the first renderer's shared material (_BaseColor = URP/Lit)
        _neutralColor = Color.white;
        if (debugRenderers.Length > 0 && debugRenderers[0] != null
            && debugRenderers[0].sharedMaterial != null
            && debugRenderers[0].sharedMaterial.HasProperty(BaseColorID))
        {
            _neutralColor = debugRenderers[0].sharedMaterial.GetColor(BaseColorID);
        }
    }

    public override void OnEpisodeBegin()
    {
        env.ResetEpisode();
        ClearScan();

        if (startPosition != null)
        {
            transform.SetPositionAndRotation(startPosition.position, startPosition.rotation);
        }

        if (_rb != null)
        {
            _rb.linearVelocity  = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
        _moveAction = 0;

        // Stop any active flash and restore neutral colour
        if (_flashRoutine != null) { StopCoroutine(_flashRoutine); _flashRoutine = null; }
        SetDebugColor(_neutralColor);

        // Initial scan at the start of the episode
        ScanWithRaycast();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // ── Per button: 10 floats ─────────────────────────────────
        // All positions are in env local space so they are consistent across parallel instances, regardless of world offsets.
        Vector3 agentLocalPos = transform.localPosition;
        // Agent forward in env-local space 
        Vector3 agentLocalFwd = env.transform.InverseTransformDirection(transform.forward);

        for (int i = 0; i < 3; i++)
        {
            if (_scanned[i].detected)
            {
                AddOneHot(sensor, 3, _scanned[i].colorIndex);
                AddOneHot(sensor, 3, _scanned[i].shapeIndex);
                sensor.AddObservation(1f);

                // Button position already in local space
                Vector3 btnLocalPos = env.GetButtonLocalPosition(i);
                Vector3 toBtn       = btnLocalPos - agentLocalPos;
                float   dist        = toBtn.magnitude;

                // Project onto agent's local XZ plane
                Vector3 right   = Vector3.Cross(Vector3.up, agentLocalFwd).normalized;
                float   localX  = dist > 0.001f ? Vector3.Dot(toBtn / dist, right)        : 0f;
                float   localZ  = dist > 0.001f ? Vector3.Dot(toBtn / dist, agentLocalFwd): 0f;

                sensor.AddObservation(Mathf.Clamp(localX, -1f, 1f));
                sensor.AddObservation(Mathf.Clamp(localZ, -1f, 1f));
                sensor.AddObservation(Mathf.Clamp01(dist / raycastDistance));
            }
            else
            {
                for (int j = 0; j < 10; j++) sensor.AddObservation(0f);
            }
        }

        // ── Velocity (2 floats) ───────────────────────────────────
        if (_rb != null)
        {
            sensor.AddObservation(Vector3.Dot(_rb.linearVelocity, transform.forward) / moveSpeed);
            sensor.AddObservation(_rb.angularVelocity.y / rotateSpeed);
        }
        else
        {
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
        }

        // ── Speaker token (vocabSize floats) ──────────────────────
        AddOneHot(sensor, env.vocabSize, env.currentMessageToken);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        env.ApplyStepPenalty();

        _moveAction = actions.DiscreteActions[0];

        // Branch 1: press the closest button within range 
        if (actions.DiscreteActions[1] == 1)
            TryPressClosestButton();

        // Max steps of and episode to prevent infinite wandering: 300 steps = 60 seconds at default FixedUpdate (0.2s).
        if (StepCount >= 300)
        {
            env.ApplyOutOfTimePenalty();
            env.EndEpisodeAll();
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  FixedUpdate: movement + periodic scan
    // ─────────────────────────────────────────────────────────────

    private int _scanCounter;
    private const int ScanEveryNFrames = 5;   // raycast every 5 physics frames

    void FixedUpdate()
    {

        // ── Movement ──────────────────────────────────────────────
        var kb = Keyboard.current;
        if (kb != null && _behaviorParams?.BehaviorType == BehaviorType.HeuristicOnly)
        {
            int action = 0;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) action = 1;
            else if (kb.sKey.isPressed || kb.downArrowKey.isPressed) action = 2;
            else if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) action = 3;
            else if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) action = 4;
            ApplyMovement(action);
        }
        else
        {
            ApplyMovement(_moveAction);
        }

        // ── Periodic scan (not every frame) ──────────────────────
        _scanCounter++;
        if (_scanCounter >= ScanEveryNFrames)
        {
            _scanCounter = 0;
            ScanWithRaycast();
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var kb = Keyboard.current;
        var d  = actionsOut.DiscreteActions;
        if (kb == null) return;

        // Branch 0 – movement
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) d[0] = 1;
        else if (kb.sKey.isPressed || kb.downArrowKey.isPressed) d[0] = 2;
        else if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) d[0] = 3;
        else if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) d[0] = 4;
        else d[0] = 0;

        // Branch 1 – press? (Space = press closest button in range)
        d[1] = kb.spaceKey.isPressed ? 1 : 0;
    }

    // ─────────────────────────────────────────────────────────────
    //  Movement
    // ─────────────────────────────────────────────────────────────

    void ApplyMovement(int action)
    {
        float dt = Time.fixedDeltaTime;
        switch (action)
        {
            case 1:
                if (_rb != null) _rb.MovePosition(_rb.position + transform.forward * (moveSpeed * dt));
                else transform.position += transform.forward * (moveSpeed * dt);
                break;
            case 2:
                if (_rb != null) _rb.MovePosition(_rb.position - transform.forward * (moveSpeed * dt));
                else transform.position -= transform.forward * (moveSpeed * dt);
                break;
            case 3: transform.Rotate(0f, -(rotateSpeed * dt), 0f); break;
            case 4: transform.Rotate(0f,   rotateSpeed * dt,  0f); break;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Press logic
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Presses the button the agent is closest to AND most facing, within pressDistance.
    /// Positions are compared in env-local space so this works correctly across parallel training instances placed at different world offsets.
    /// Score = dot(forward, dirToButton) / distance  — higher is better.
    /// If no button is within range, applies a small penalty to discourage spamming.
    /// </summary>
    void TryPressClosestButton()
    {
        int   bestSlot  = -1;
        float bestScore = float.MinValue;

        // Agent position and forward in env-local space
        Vector3 agentLocal = env.transform.InverseTransformPoint(transform.position);
        Vector3 forwardLocal = env.transform.InverseTransformDirection(transform.forward);

        for (int i = 0; i < 3; i++)
        {
            Vector3 btnLocal = env.GetButtonLocalPosition(i);
            Vector3 toBtn = btnLocal - agentLocal;
            float dist = toBtn.magnitude;
            if (dist > pressDistance) continue; // out of reach — skip

            float dot = Vector3.Dot(forwardLocal, toBtn/dist); // -1..1
            if (dot <= 0f) continue; // behind the agent — skip

            // Combine: more facing + closer = higher score
            float score = dot/dist;
            if (score > bestScore)
            {
                bestScore = score;
                bestSlot  = i;
            }
        }

        if (bestSlot >= 0)
        {
            env.ListenerChoseButton(bestSlot); // EnvironmentManager handles visual feedback
        }
        else
        {
            env.RegisterEmptyPressAttempt(); // records the failed attempt in TensorBoard
            ShowWrongPress(); // pressing thin air = red feedback
            AddReward(-0.01f);
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Visual feedback — public API
    // ─────────────────────────────────────────────────────────────

    /// <summary>Applies a colour to the agent via MPB without instantiating materials.</summary>
    public void SetDebugColor(Color color)
    {
        if (debugRenderers == null) return;
        _mpb.SetColor(BaseColorID, color);
        foreach (Renderer r in debugRenderers)
            if (r != null) r.SetPropertyBlock(_mpb);
    }

    /// <summary>Flashes green for 0.1 s then returns to neutral. Call on a correct press.</summary>
    public void ShowCorrectPress(System.Action onComplete = null)
    {
        if (_flashRoutine != null) StopCoroutine(_flashRoutine);
        _flashRoutine = StartCoroutine(FlashRoutine(Color.green, onComplete));
    }

    /// <summary>Flashes red for 0.1 s then returns to neutral. Call on a wrong press.</summary>
    public void ShowWrongPress(System.Action onComplete = null)
    {
        if (_flashRoutine != null) StopCoroutine(_flashRoutine);
        _flashRoutine = StartCoroutine(FlashRoutine(Color.red, onComplete));
    }

    private IEnumerator FlashRoutine(Color flashColor, System.Action onComplete)
    {
        SetDebugColor(flashColor);
        yield return new WaitForSeconds(flashDuration);
        SetDebugColor(_neutralColor);
        _flashRoutine = null;
        onComplete?.Invoke();
    }

    // ─────────────────────────────────────────────────────────────
    //  Raycast scan — optimized
    // ─────────────────────────────────────────────────────────────

    void ScanWithRaycast()
    {
        ClearScan();
        Transform origin = raycastOrigin != null ? raycastOrigin : transform;

        // First try to detect with DIRECT rays to each known button 
        int found = TryDirectRays(origin);
        if (found >= 3) return; // all found, no need for fan sweep

        // Fan sweep only if any button was not detected with a direct ray
        FanScan(origin);
    }

    /// <summary>Fires 1 ray per button directly towards its position. O(3) raycasts.</summary>
    int TryDirectRays(Transform origin)
    {
        int found = 0;
        for (int i = 0; i < 3; i++)
        {
            if (_scanned[i].detected) { found++; continue; }

            Vector3 toBtn = env.GetButtonWorldPosition(i) - origin.position;
            if (toBtn.sqrMagnitude < 0.01f) continue;

            if (Physics.Raycast(origin.position, toBtn.normalized, out RaycastHit hit, raycastDistance, buttonLayer) 
                && hit.collider.CompareTag(buttonTag))
            {
                ButtonController btn = hit.collider.GetComponentInParent<ButtonController>() 
                                       ?? hit.collider.GetComponent<ButtonController>();
                if (btn != null && FindSlot(btn) == i)
                {
                    _scanned[i] = new ScannedButton
                    {
                        colorIndex = (int)btn.ButtonColorValue,
                        shapeIndex = (int)btn.ButtonShapeValue,
                        detected   = true
                    };
                    found++;
                }
            }
        }
        return found;
    }

    /// <summary>Reduced fan sweep for buttons not found with direct rays.</summary>
    void FanScan(Transform origin)
    {
        _seenButtons.Clear();

        for (float angle = -scanHalfAngle; angle <= scanHalfAngle; angle += horizontalStep)
        {
            Quaternion rot = Quaternion.AngleAxis(angle, Vector3.up);
            Ray ray = new Ray(origin.position, rot * origin.forward);

            if (!Physics.Raycast(ray, out RaycastHit hit, raycastDistance, buttonLayer)) continue;
            if (!hit.collider.CompareTag(buttonTag)) continue;

            ButtonController btn = hit.collider.GetComponentInParent<ButtonController>()
                                ?? hit.collider.GetComponent<ButtonController>();
            if (btn == null || _seenButtons.Contains(btn)) continue;

            int slot = FindSlot(btn);
            if (slot < 0 || _scanned[slot].detected) continue;

            _seenButtons.Add(btn);
            _scanned[slot] = new ScannedButton
            {
                colorIndex = (int)btn.ButtonColorValue,
                shapeIndex = (int)btn.ButtonShapeValue,
                detected   = true
            };
        }
    }

    int FindSlot(ButtonController btn)
    {
        for (int i = 0; i < env.buttonObjects.Length; i++)
            if (env.buttonObjects[i] == btn) return i;
        return -1;
    }

    void ClearScan()
    {
        for (int i = 0; i < 3; i++)
            _scanned[i] = new ScannedButton { detected = false };
    }

    // ─────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────

    void AddOneHot(VectorSensor sensor, int size, int index)
    {
        for (int i = 0; i < size; i++)
            sensor.AddObservation(i == index ? 1f : 0f);
    }

    void OnDrawGizmosSelected()
    {
        Transform origin = raycastOrigin != null ? raycastOrigin : transform;
        Gizmos.color = Color.cyan;
        for (float a = -scanHalfAngle; a <= scanHalfAngle; a += horizontalStep)
        {
            Quaternion rot = Quaternion.AngleAxis(a, Vector3.up);
            Gizmos.DrawRay(origin.position, (rot * origin.forward) * raycastDistance);
        }
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, pressDistance);
        for (int i = 0; i < 3; i++)
        {
            if (!_scanned[i].detected) continue;
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(env.GetButtonWorldPosition(i), 0.4f);
        }
    }
}