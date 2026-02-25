using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Policies;
using System.Collections.Generic;

/// <summary>
/// El Listener se mueve por el entorno 3D, detecta los botones mediante raycasts
/// y pulsa el que crea correcto según el token recibido del Speaker.
///
/// ACCIONES DISCRETAS:
///   Branch 0 – Movimiento  (5 valores)
///     0 = nada
///     1 = avanzar
///     2 = retroceder
///     3 = rotar izquierda
///     4 = rotar derecha
///
///   Branch 1 – Pulsar  (4 valores)
///     0 = no pulsar
///     1 = pulsar botón 0
///     2 = pulsar botón 1
///     3 = pulsar botón 2
///
/// OBSERVACIONES (total = 36 + vocabSize floats):
///   Por cada botón (×3): color one-hot(3) + shape one-hot(3) + detected(1)
///                        + dirección normalizada XZ(2) + distancia normalizada(1)
///                      = 10 floats  →  30 floats totales
///   Velocidad forward normalizada(1) + angular normalizada(1) = 2 floats
///   Token del Speaker: one-hot(vocabSize)
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
    public float pressDistance = 2f;

    [Header("Raycast Settings")]
    public Transform raycastOrigin;
    public float     raycastDistance = 20f;
    public LayerMask buttonLayer;
    public string    buttonTag = "Button";

    [Tooltip("Ángulo de barrido horizontal a cada lado (grados).")]
    public float scanHalfAngle  = 90f;
    [Tooltip("Paso angular entre rayos. Valor alto = menos rayos = más rápido.")]
    public float horizontalStep = 20f;   // era 10 → la mitad de rayos

    [Header("Spawn (reset position)")]
    public Transform startPosition;

    // ─────────────────────────────────────────────────────────────
    //  Private state
    // ─────────────────────────────────────────────────────────────

    private struct ScannedButton
    {
        public int     colorIndex;
        public int     shapeIndex;
        public bool    detected;
        public Vector3 worldPos;
    }

    private ScannedButton[]    _scanned        = new ScannedButton[3];
    private Rigidbody          _rb;
    private int                _moveAction;
    private BehaviorParameters _behaviorParams;
    private readonly HashSet<ButtonController> _seenButtons = new HashSet<ButtonController>(); // reutilizado

    // ─────────────────────────────────────────────────────────────
    //  ML-Agents lifecycle
    // ─────────────────────────────────────────────────────────────

    public override void Initialize()
    {
        _rb             = GetComponent<Rigidbody>();
        _behaviorParams = GetComponent<BehaviorParameters>();

        if (_rb != null)
        {
            _rb.constraints = RigidbodyConstraints.FreezeRotationX
                            | RigidbodyConstraints.FreezeRotationZ
                            | RigidbodyConstraints.FreezePositionY;
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

        // Escaneo inicial al comenzar el episodio
        ScanWithRaycast();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // ── Por cada botón: 10 floats ─────────────────────────────
        for (int i = 0; i < 3; i++)
        {
            if (_scanned[i].detected)
            {
                AddOneHot(sensor, 3, _scanned[i].colorIndex);
                AddOneHot(sensor, 3, _scanned[i].shapeIndex);
                sensor.AddObservation(1f);

                Vector3 toBtn    = _scanned[i].worldPos - transform.position;
                float   dist     = toBtn.magnitude;
                Vector3 localDir = transform.InverseTransformDirection(dist > 0.001f ? toBtn / dist : Vector3.zero);
                sensor.AddObservation(Mathf.Clamp(localDir.x, -1f, 1f));
                sensor.AddObservation(Mathf.Clamp(localDir.z, -1f, 1f));
                sensor.AddObservation(Mathf.Clamp01(dist / raycastDistance));
            }
            else
            {
                for (int j = 0; j < 10; j++) sensor.AddObservation(0f);
            }
        }

        // ── Velocidad (2 floats) ──────────────────────────────────
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

        // ── Token del Speaker (vocabSize floats) ──────────────────
        AddOneHot(sensor, env.vocabSize, env.currentMessageToken);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        env.ApplyStepPenalty();

        _moveAction = actions.DiscreteActions[0];

        int pressAction = actions.DiscreteActions[1];
        if (pressAction > 0)
            TryPressButton(pressAction - 1);

        // Max steps of and episode to prevent infinite wandering: 300 steps = 60 seconds at default FixedUpdate (0.2s).
        if (StepCount >= 300)
        {
            AddReward(-1f); 
            env.EndEpisodeAll();
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  FixedUpdate: movimiento + escaneo periódico
    // ─────────────────────────────────────────────────────────────

    private int _scanCounter;
    private const int ScanEveryNFrames = 5;   // raycast cada 5 physics frames

    void FixedUpdate()
    {
        // ── Movimiento ────────────────────────────────────────────
        var kb = Keyboard.current;
        if (kb != null && _behaviorParams?.BehaviorType == BehaviorType.HeuristicOnly)
        {
            int action = 0;
            if      (kb.wKey.isPressed || kb.upArrowKey.isPressed)    action = 1;
            else if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  action = 2;
            else if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  action = 3;
            else if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) action = 4;
            ApplyMovement(action);
        }
        else
        {
            ApplyMovement(_moveAction);
        }

        // ── Escaneo periódico (no cada frame) ────────────────────
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

        if      (kb.wKey.isPressed || kb.upArrowKey.isPressed)    d[0] = 1;
        else if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  d[0] = 2;
        else if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  d[0] = 3;
        else if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) d[0] = 4;
        else                                                       d[0] = 0;

        if      (kb.digit1Key.isPressed) d[1] = 1;
        else if (kb.digit2Key.isPressed) d[1] = 2;
        else if (kb.digit3Key.isPressed) d[1] = 3;
        else                             d[1] = 0;
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
                else             transform.position += transform.forward * (moveSpeed * dt);
                break;
            case 2:
                if (_rb != null) _rb.MovePosition(_rb.position - transform.forward * (moveSpeed * dt));
                else             transform.position -= transform.forward * (moveSpeed * dt);
                break;
            case 3: transform.Rotate(0f, -(rotateSpeed * dt), 0f); break;
            case 4: transform.Rotate(0f,   rotateSpeed * dt,  0f); break;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Press logic
    // ─────────────────────────────────────────────────────────────

    void TryPressButton(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex > 2) return;
        float dist = Vector3.Distance(transform.position, env.GetButtonWorldPosition(slotIndex));
        if (dist <= pressDistance)
            env.ListenerChoseButton(slotIndex);
        else
            AddReward(-0.01f);
    }

    // ─────────────────────────────────────────────────────────────
    //  Raycast scan — optimizado
    // ─────────────────────────────────────────────────────────────

    void ScanWithRaycast()
    {
        ClearScan();
        Transform origin = raycastOrigin != null ? raycastOrigin : transform;

        // Primero intenta detectar con rayos DIRECTOS a cada botón conocido (muy barato)
        int found = TryDirectRays(origin);
        if (found >= 3) return;   // todos encontrados, no hace falta el barrido

        // Barrido en abanico solo si algún botón no se detectó con rayo directo
        FanScan(origin);
    }

    /// <summary>Lanza 1 rayo por botón directamente hacia su posición. O(3) raycasts.</summary>
    int TryDirectRays(Transform origin)
    {
        int found = 0;
        for (int i = 0; i < 3; i++)
        {
            if (_scanned[i].detected) { found++; continue; }

            Vector3 toBtn = env.GetButtonWorldPosition(i) - origin.position;
            if (toBtn.sqrMagnitude < 0.01f) continue;

            if (Physics.Raycast(origin.position, toBtn.normalized, out RaycastHit hit,
                                raycastDistance, buttonLayer)
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
                        detected   = true,
                        worldPos   = btn.transform.position
                    };
                    found++;
                }
            }
        }
        return found;
    }

    /// <summary>Barrido en abanico reducido para botones no encontrados con rayo directo.</summary>
    void FanScan(Transform origin)
    {
        _seenButtons.Clear(); // reutiliza, no alloca

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
                detected   = true,
                worldPos   = btn.transform.position
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
            Gizmos.DrawWireSphere(_scanned[i].worldPos, 0.4f);
        }
    }
}

