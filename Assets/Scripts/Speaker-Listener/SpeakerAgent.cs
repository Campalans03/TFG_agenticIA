using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// The Speaker reads the rule book (targetColor, targetShape),
/// emits ONE discrete token per episode and then stays silent.
/// RequestDecision() is called exactly once by EnvironmentManager.ResetEpisode().
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
        // Reset is driven by EnvironmentManager. Nothing to do here.
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // ── Rule book observations (visible only to the Speaker) ──
        // targetColor → one-hot (3 floats)
        AddOneHot(sensor, 3, (int)env.targetColor);

        // targetShape → one-hot (3 floats)
        AddOneHot(sensor, 3, (int)env.targetShape);

        // Total: 6 floats
        // (No previous-token feedback: the Speaker speaks exactly once per episode)
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // Emit the chosen token once and freeze until next episode.
        int token = actions.DiscreteActions[0];
        env.SetMessageToken(token);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // Deterministic heuristic: colorIndex * 3 + shapeIndex
        var d = actionsOut.DiscreteActions;
        int code = (int)env.targetColor * 3 + (int)env.targetShape;
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