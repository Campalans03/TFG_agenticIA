using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// MA-POCA variant of SpeakerAgent.
/// Identical observation/action interface to <see cref="SpeakerAgent"/>; the only
/// difference is that it references <see cref="EnvironmentManagerMaPoca"/> so the
/// reward distribution flows through a SimpleMultiAgentGroup with a centralized
/// critic instead of independent PPO reward streams.
/// </summary>
public class SpeakerAgentMaPoca : Agent
{
    public EnvironmentManagerMaPoca env;

    public override void OnEpisodeBegin()
    {
        // Reset is driven by EnvironmentManagerMaPoca.
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        AddOneHot(sensor, 3, (int)env.targetColor);
        AddOneHot(sensor, 3, (int)env.targetShape);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        int token = actions.DiscreteActions[0];
        env.SetMessageToken(token);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var d = actionsOut.DiscreteActions;
        int code = (int)env.targetColor * 3 + (int)env.targetShape;
        d[0] = code % env.vocabSize;
    }

    void AddOneHot(VectorSensor sensor, int size, int index)
    {
        for (int i = 0; i < size; i++)
            sensor.AddObservation(i == index ? 1f : 0f);
    }
}
