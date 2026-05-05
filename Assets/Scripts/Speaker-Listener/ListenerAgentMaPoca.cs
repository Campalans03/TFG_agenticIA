using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Policies;

/// <summary>
/// MA-POCA variant of ListenerAgent.
/// Identical movement/observation/action logic to <see cref="ListenerAgent"/>; the
/// only structural change is that it references <see cref="EnvironmentManagerMaPoca"/>
/// so shared task rewards (correct/wrong/timeout) are routed through the group's
/// centralized critic. Individual shaping (step penalty, proximity, empty-press) is
/// kept as agent-local AddReward — those are not team contributions.
/// </summary>
public class ListenerAgentMaPoca : Agent
{
    public EnvironmentManagerMaPoca env;

    [Header("Movement")] public float moveSpeed = 3f;
    public float rotateSpeed = 120f;

    [Header("Press distance")] public float pressDistance = 1f;

    [Header("Raycast Settings")] public Transform raycastOrigin;
    public float raycastDistance = 5f;
    public LayerMask buttonLayer;
    public string buttonTag = "Button";

    [Tooltip("Horizontal sweep angle on each side (degrees).")]
    public float scanHalfAngle = 90f;

    [Tooltip("Angular step between rays. Higher value = fewer rays = faster.")]
    public float horizontalStep = 20f;

    [Header("Spawn (reset position)")] public Transform startPosition;

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

    private float _previousDistanceToTarget;
    private bool _pressedLastStep;

    [Header("Visual Feedback")]
    [Tooltip("Renderers to flash. Leave empty to auto-collect from this GameObject and children.")]
    public Renderer[] debugRenderers;

    [Tooltip("Seconds the flash colour stays on screen.")]
    public float flashDuration = 0.1f;

    private MaterialPropertyBlock _mpb;
    private static readonly int BaseColorID = Shader.PropertyToID("_BaseColor");
    private Color _neutralColor;
    private Coroutine _flashRoutine;

    public override void Initialize()
    {
        _rb = GetComponent<Rigidbody>();
        _behaviorParams = GetComponent<BehaviorParameters>();

        if (debugRenderers == null || debugRenderers.Length == 0)
            debugRenderers = GetComponentsInChildren<Renderer>();

        _mpb = new MaterialPropertyBlock();

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
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }

        _moveAction = 0;
        _pressedLastStep = false;

        if (_flashRoutine != null)
        {
            StopCoroutine(_flashRoutine);
            _flashRoutine = null;
        }

        SetDebugColor(_neutralColor);

        ScanWithRaycast();

        int correctIndex = env.GetCorrectButtonIndex();
        if (correctIndex >= 0)
        {
            _previousDistanceToTarget = Vector3.Distance(transform.position, env.GetButtonWorldPosition(correctIndex));
        }
        else
        {
            _previousDistanceToTarget = 0f;
        }
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        Vector3 agentLocalPos = transform.localPosition;
        Vector3 agentLocalFwd = transform.forward;

        for (int i = 0; i < 3; i++)
        {
            if (_scanned[i].detected)
            {
                AddOneHot(sensor, 3, _scanned[i].colorIndex);
                AddOneHot(sensor, 3, _scanned[i].shapeIndex);
                sensor.AddObservation(1f);

                Vector3 btnLocalPos = env.GetButtonLocalPosition(i);
                Vector3 toBtn = btnLocalPos - agentLocalPos;
                float dist = toBtn.magnitude;

                Vector3 right = Vector3.Cross(Vector3.up, agentLocalFwd).normalized;
                float localX = dist > 0.001f ? Vector3.Dot(toBtn / dist, right) : 0f;
                float localZ = dist > 0.001f ? Vector3.Dot(toBtn / dist, agentLocalFwd) : 0f;

                sensor.AddObservation(Mathf.Clamp(localX, -1f, 1f));
                sensor.AddObservation(Mathf.Clamp(localZ, -1f, 1f));
                sensor.AddObservation(Mathf.Clamp01(dist / raycastDistance));
            }
            else
            {
                for (int j = 0; j < 10; j++) sensor.AddObservation(0f);
            }
        }

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

        AddOneHot(sensor, env.vocabSize, env.currentMessageToken);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        env.ApplyStepPenalty();

        _moveAction = actions.DiscreteActions[0];

        bool pressNow = actions.DiscreteActions[1] == 1;
        if (pressNow && !_pressedLastStep)
            TryPressClosestButton();
        _pressedLastStep = pressNow;

        if (StepCount >= 300)
        {
            env.ApplyOutOfTimePenalty();
            env.EndEpisodeAll();
        }

        int correctIndex = env.GetCorrectButtonIndex();
        if (correctIndex >= 0)
        {
            float currentDistance = env.GetDistanceToCorrectButton(transform.localPosition);
            float delta = _previousDistanceToTarget - currentDistance;

            // Proximity shaping stays an INDIVIDUAL reward (not group): it concerns
            // only the Listener's navigation, not the team objective.
            AddReward(delta * 0.01f);

            _previousDistanceToTarget = currentDistance;
        }
    }

    private int _scanCounter;
    private const int ScanEveryNFrames = 5;

    void FixedUpdate()
    {
        var kb = Keyboard.current;
        if (kb != null && _behaviorParams?.BehaviorType == BehaviorType.HeuristicOnly)
        {
            int action = 0;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) action = 1;
            else if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) action = 2;
            else if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) action = 3;
            ApplyMovement(action);
        }
        else
        {
            ApplyMovement(_moveAction);
        }

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
        var d = actionsOut.DiscreteActions;
        if (kb == null) return;

        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) d[0] = 1;
        else if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) d[0] = 2;
        else if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) d[0] = 3;
        else d[0] = 0;

        d[1] = kb.spaceKey.isPressed ? 1 : 0;
    }

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
                transform.Rotate(0f, -(rotateSpeed * dt), 0f);
                break;
            case 3:
                transform.Rotate(0f, rotateSpeed * dt, 0f);
                break;
        }
    }

    void TryPressClosestButton()
    {
        int bestSlot = -1;
        float bestScore = float.MinValue;

        Vector3 agentLocal = env.transform.InverseTransformPoint(transform.position);
        Vector3 forwardLocal = env.transform.InverseTransformDirection(transform.forward);

        for (int i = 0; i < 3; i++)
        {
            if (env.buttonObjects[i] == null || !env.buttonObjects[i].gameObject.activeSelf) continue;

            Vector3 btnLocal = env.GetButtonLocalPosition(i);
            Vector3 toBtn = btnLocal - agentLocal;
            float dist = toBtn.magnitude;
            if (dist > pressDistance) continue;

            float dot = Vector3.Dot(forwardLocal, toBtn / dist);
            if (dot <= 0f) continue;

            float score = dot / dist;
            if (score > bestScore)
            {
                bestScore = score;
                bestSlot = i;
            }
        }

        if (bestSlot >= 0)
        {
            env.ListenerChoseButton(bestSlot);
        }
        else
        {
            env.RegisterEmptyPressAttempt();
            ShowWrongPress();
            // Empty-press penalty stays individual: only the Listener controls presses.
            AddReward(-0.05f);
        }
    }

    public void SetDebugColor(Color color)
    {
        if (debugRenderers == null) return;
        _mpb.SetColor(BaseColorID, color);
        foreach (Renderer r in debugRenderers)
        {
            if (r != null) r.SetPropertyBlock(_mpb);
        }
    }

    public void ShowCorrectPress(System.Action onComplete = null)
    {
        if (_flashRoutine != null) StopCoroutine(_flashRoutine);
        _flashRoutine = StartCoroutine(FlashRoutine(Color.green, onComplete));
    }

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

    void ScanWithRaycast()
    {
        ClearScan();
        Transform origin = raycastOrigin != null ? raycastOrigin : transform;

        int found = TryDirectRays(origin);
        if (found >= 3) return;

        FanScan(origin);
    }

    int TryDirectRays(Transform origin)
    {
        int found = 0;
        for (int i = 0; i < 3; i++)
        {
            if (_scanned[i].detected)
            {
                found++;
                continue;
            }

            if (env.buttonObjects[i] == null || !env.buttonObjects[i].gameObject.activeSelf) continue;

            Vector3 toBtn = env.GetButtonWorldPosition(i) - origin.position;
            if (toBtn.sqrMagnitude < 0.01f) continue;

            if (Physics.Raycast(origin.position, toBtn.normalized, out RaycastHit hit, raycastDistance, buttonLayer) &&
                hit.collider.CompareTag(buttonTag))
            {
                ButtonController btn = hit.collider.GetComponentInParent<ButtonController>() ??
                                       hit.collider.GetComponent<ButtonController>();

                if (btn != null && FindSlot(btn) == i)
                {
                    _scanned[i] = new ScannedButton
                    {
                        colorIndex = (int)btn.ButtonColorValue,
                        shapeIndex = (int)btn.ButtonShapeValue,
                        detected = true
                    };
                    found++;
                }
            }
        }

        return found;
    }

    void FanScan(Transform origin)
    {
        _seenButtons.Clear();

        for (float angle = -scanHalfAngle; angle <= scanHalfAngle; angle += horizontalStep)
        {
            Quaternion rot = Quaternion.AngleAxis(angle, Vector3.up);
            Ray ray = new Ray(origin.position, rot * origin.forward);

            if (!Physics.Raycast(ray, out RaycastHit hit, raycastDistance, buttonLayer)) continue;
            if (!hit.collider.CompareTag(buttonTag)) continue;

            ButtonController btn = hit.collider.GetComponentInParent<ButtonController>() ??
                                   hit.collider.GetComponent<ButtonController>();
            if (btn == null || _seenButtons.Contains(btn)) continue;

            int slot = FindSlot(btn);
            if (slot < 0 || _scanned[slot].detected) continue;

            _seenButtons.Add(btn);
            _scanned[slot] = new ScannedButton
            {
                colorIndex = (int)btn.ButtonColorValue,
                shapeIndex = (int)btn.ButtonShapeValue,
                detected = true
            };
        }
    }

    int FindSlot(ButtonController btn)
    {
        for (int i = 0; i < env.buttonObjects.Length; i++)
        {
            if (env.buttonObjects[i] == btn) return i;
        }

        return -1;
    }

    void ClearScan()
    {
        for (int i = 0; i < 3; i++)
        {
            _scanned[i] = new ScannedButton { detected = false };
        }
    }

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
