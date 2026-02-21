using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// The Speaker reads the rule book (targetColor, targetShape, requireNoRed)
/// but CANNOT see the buttons. It encodes the rule into a discrete token
/// and broadcasts it so the Listener can pick the correct button.
/// </summary>
public class SpeakerAgent : Agent
{
    // ─────────────────────────────────────────────────────────────
    //  Inspector
    // ─────────────────────────────────────────────────────────────

    public EnvironmentManager env;

    // ─────────────────────────────────────────────────────────────
    //  ML-Agents lifecycle
    // ─────────────────────────────────────────────────────────────

    public override void OnEpisodeBegin()
    {
        // Episode is reset by the Listener; nothing to do here.
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // ── Rule book observations (visible only to the Speaker) ──
        // targetColor  → one-hot (3 floats)
        AddOneHot(sensor, 3, (int)env.targetColor);

        // targetShape  → one-hot (3 floats)
        AddOneHot(sensor, 3, (int)env.targetShape);

        // requireNoRed → binary flag (1 float)
        sensor.AddObservation(env.requireNoRed ? 1f : 0f);

        // Previous token the Speaker emitted (feedback for recurrent policy)
        AddOneHot(sensor, env.vocabSize, env.currentMessageToken);

        // Total: 3 + 3 + 1 + vocabSize floats
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // Choose a token and broadcast it through the environment channel
        int token = actions.DiscreteActions[0];
        env.SetMessageToken(token);

        // The episode does NOT end here; the Listener decides when it ends.
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // Simple deterministic heuristic for debugging:
        // Encode rule as: colorIndex * 6 + shapeIndex * 2 + (requireNoRed ? 1 : 0)
        // Clamped to vocabSize to avoid out-of-range tokens.
        var d = actionsOut.DiscreteActions;
        int code = (int)env.targetColor * 6
                 + (int)env.targetShape * 2
                 + (env.requireNoRed ? 1 : 0);
        d[0] = code % env.vocabSize;
    }

    // ─────────────────────────────────────────────────────────────
    //  Helper
    // ─────────────────────────────────────────────────────────────

    void AddOneHot(VectorSensor sensor, int size, int index)
    {
        for (int i = 0; i < size; i++)
            sensor.AddObservation(i == index ? 1f : 0f);
    }
}