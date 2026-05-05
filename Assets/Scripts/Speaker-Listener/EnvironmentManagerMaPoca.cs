using UnityEngine;
using Unity.MLAgents;

/// <summary>
/// MA-POCA variant of EnvironmentManager.
///
/// Functionally identical to <see cref="EnvironmentManager"/> for episode setup,
/// observation plumbing and stats logging. The structural change is reward
/// distribution: shared task rewards (correct press, wrong press, out-of-time)
/// are routed through a <see cref="SimpleMultiAgentGroup"/> so the centralized
/// POCA critic sees them as team rewards. Individual shaping (Listener step
/// penalty, proximity, empty-press) stays per-agent because those are not
/// team contributions.
///
/// Episode termination uses <see cref="SimpleMultiAgentGroup.EndGroupEpisode"/>
/// so both agents are terminated together with proper bootstrap = 0 semantics.
/// </summary>
public class EnvironmentManagerMaPoca : MonoBehaviour
{
    [Header("Episode Settings")] public int maxResampleTries = 50;

    [Header("Shared Reward")] public float correctReward = 3.0f;
    public float wrongReward = -1.0f;
    public float stepPenalty = -0.005f;

    [Header("Communication")] [Tooltip("Vocabulary size. Must match the Speaker's Discrete Branch size.")]
    public int vocabSize = 9;

    public int silenceToken = 0;

    [Header("Buttons (exactly 3)")] [Tooltip("Assign the 3 ButtonController GameObjects here.")]
    public ButtonController[] buttonObjects = new ButtonController[3];

    [Header("Spawn Area")] [Tooltip("Centre of the zone where buttons can be randomly placed.")]
    public Transform spawnAreaCenter;

    [Tooltip("Half-extents for button placement (XZ plane).")]
    public Vector2 spawnHalfExtents = new Vector2(3f, 3f);

    [Tooltip("Minimum distance between any two buttons.")]
    public float minButtonSeparation = 1.5f;

    [Tooltip("Y position for all buttons.")]
    public float buttonY = 0.5f;

    [Header("Agent Refs")] public SpeakerAgentMaPoca speaker;
    public ListenerAgentMaPoca listener;

    public ButtonData[] buttons { get; private set; } = new ButtonData[3];
    public ButtonColor targetColor { get; private set; }
    public ButtonShape targetShape { get; private set; }
    public int currentMessageToken { get; private set; } = 0;
    public int activeButtonCount { get; private set; } = 3;

    private int _episodePressAttempts = 0;
    private int _episodeCorrectPresses = 0;
    private int _episodeWrongPresses = 0;
    private bool _episodeEnding = false;

    private SimpleMultiAgentGroup _group;
    private bool _groupReady = false;

    void Awake()
    {
        _group = new SimpleMultiAgentGroup();
    }

    void Start()
    {
        // Register agents lazily — Awake order isn't guaranteed and the group
        // requires the agents' Initialize to have run.
        TryRegisterAgents();
    }

    void TryRegisterAgents()
    {
        if (_groupReady) return;
        if (speaker == null || listener == null) return;
        _group.RegisterAgent(speaker);
        _group.RegisterAgent(listener);
        _groupReady = true;
    }

    public void ResetEpisode()
    {
        // In case Start ran before agent refs were wired (rare with prefabs)
        TryRegisterAgents();

        _episodePressAttempts = 0;
        _episodeCorrectPresses = 0;
        _episodeWrongPresses = 0;
        _episodeEnding = false;

        float requested = Academy.Instance.EnvironmentParameters
            .GetWithDefault("active_buttons", 3f);
        activeButtonCount = Mathf.Clamp(Mathf.RoundToInt(requested), 1, 3);
        Academy.Instance.StatsRecorder.Add("Curriculum/ActiveButtons", activeButtonCount);

        for (int i = 0; i < 3; i++)
        {
            if (buttonObjects[i] == null) continue;
            buttonObjects[i].gameObject.SetActive(i < activeButtonCount);
        }

        bool ok = false;
        for (int attempt = 0; attempt < maxResampleTries && !ok; attempt++)
        {
            for (int i = 0; i < activeButtonCount; i++)
            {
                buttons[i] = new ButtonData
                {
                    color = (ButtonColor)Random.Range(0, 3),
                    shape = (ButtonShape)Random.Range(0, 3)
                };
            }

            int targetSlot = Random.Range(0, activeButtonCount);
            targetColor = buttons[targetSlot].color;
            targetShape = buttons[targetSlot].shape;

            ok = AllButtonsUnique() && EpisodeHasValidSolution();
        }

        Vector3[] positions = GenerateScatteredPositions(activeButtonCount);

        for (int i = 0; i < activeButtonCount; i++)
        {
            if (buttonObjects[i] == null) continue;
            buttonObjects[i].transform.localPosition = positions[i];
            buttonObjects[i].Apply(buttons[i].color, buttons[i].shape, i);
        }

        currentMessageToken = silenceToken;

        speaker.RequestDecision();
    }

    public void SetMessageToken(int token)
    {
        currentMessageToken = Mathf.Clamp(token, 0, vocabSize - 1);
        Debug.Log($"[SpeakerMaPoca] emitted token {currentMessageToken}.");

        var stats = Academy.Instance.StatsRecorder;
        stats.Add("Speaker/EmittedToken", currentMessageToken);
    }

    public void ApplyStepPenalty()
    {
        // Individual: only the Listener pays for episode length.
        listener.AddReward(stepPenalty);
    }

    public void ApplyOutOfTimePenalty()
    {
        // Group: timeout is a shared team failure.
        _group.AddGroupReward(wrongReward);
        RecordEpisodeOutcome(false);
    }

    public void ListenerChoseButton(int chosenIndex)
    {
        if (_episodeEnding) return;
        _episodeEnding = true;

        int correct = GetCorrectButtonIndex();

        _episodePressAttempts++;
        var stats = Academy.Instance.StatsRecorder;
        stats.Add("Listener/PressAttempts", _episodePressAttempts);

        if (chosenIndex == correct)
        {
            _episodeCorrectPresses++;
            stats.Add("Listener/CorrectPresses", _episodeCorrectPresses);
            stats.Add("Listener/AccuracyRate", (float)_episodeCorrectPresses / _episodePressAttempts);

            RecordEpisodeOutcome(true);

            // Group: correct press is the team objective.
            _group.AddGroupReward(correctReward);
            listener.ShowCorrectPress(onComplete: EndGroupEpisodeSafe);
        }
        else
        {
            _episodeWrongPresses++;
            stats.Add("Listener/WrongPresses", _episodeWrongPresses);
            stats.Add("Listener/AccuracyRate", (float)_episodeCorrectPresses / _episodePressAttempts);

            RecordEpisodeOutcome(false);

            // Group: wrong press is a shared failure.
            _group.AddGroupReward(wrongReward);
            listener.ShowWrongPress(onComplete: EndGroupEpisodeSafe);
        }
    }

    /// <summary>Ends the episode for all agents in the group (terminal, bootstrap=0).</summary>
    public void EndEpisodeAll()
    {
        EndGroupEpisodeSafe();
    }

    void EndGroupEpisodeSafe()
    {
        if (_groupReady) _group.EndGroupEpisode();
        else
        {
            speaker.EndEpisode();
            listener.EndEpisode();
        }
    }

    private void RecordEpisodeOutcome(bool success)
    {
        var stats = Academy.Instance.StatsRecorder;
        float score = success ? 1f : 0f;
        string rule = $"{targetColor}_{targetShape}";

        stats.Add($"Speaker/Token_{currentMessageToken}/SuccessRate", score);
        stats.Add($"Rule/{rule}/SuccessRate", score);
        stats.Add($"Mapping/{rule}/Token", currentMessageToken);
    }

    public void RegisterEmptyPressAttempt()
    {
        _episodePressAttempts++;
        _episodeWrongPresses++;
        var stats = Academy.Instance.StatsRecorder;
        stats.Add("Listener/PressAttempts", _episodePressAttempts);
        stats.Add("Listener/WrongPresses", _episodeWrongPresses);

        if (_episodePressAttempts > 0)
            stats.Add("Listener/AccuracyRate", (float)_episodeCorrectPresses / _episodePressAttempts);
    }

    public Vector3 GetButtonWorldPosition(int slotIndex)
    {
        if (buttonObjects[slotIndex] != null)
            return buttonObjects[slotIndex].transform.position;
        return transform.position;
    }

    public Vector3 GetButtonLocalPosition(int slotIndex)
    {
        if (buttonObjects[slotIndex] != null)
            return buttonObjects[slotIndex].transform.localPosition;
        return Vector3.zero;
    }

    public int GetCorrectButtonIndex()
    {
        for (int i = 0; i < activeButtonCount; i++)
            if (buttons[i].color == targetColor && buttons[i].shape == targetShape)
                return i;

        return -1;
    }

    bool AllButtonsUnique()
    {
        for (int i = 0; i < activeButtonCount; i++)
        for (int j = i + 1; j < activeButtonCount; j++)
            if (buttons[i].color == buttons[j].color && buttons[i].shape == buttons[j].shape)
                return false;
        return true;
    }

    bool EpisodeHasValidSolution()
    {
        int idx = GetCorrectButtonIndex();
        return idx >= 0 && idx < activeButtonCount;
    }

    public float GetDistanceToCorrectButton(Vector3 listenerPosition)
    {
        int correctIndex = GetCorrectButtonIndex();
        if (correctIndex < 0) return 0f;

        return Vector3.Distance(listenerPosition, GetButtonLocalPosition(correctIndex));
    }

    private readonly Vector3[] _spawnPositions = new Vector3[3];

    Vector3[] GenerateScatteredPositions(int count)
    {
        Vector3 centre = spawnAreaCenter != null
            ? spawnAreaCenter.localPosition
            : Vector3.zero;

        int filled = 0;
        int maxAttempts = 200;

        for (int i = 0; i < count; i++)
        {
            bool placed = false;

            for (int a = 0; a < maxAttempts && !placed; a++)
            {
                float x = Random.Range(-spawnHalfExtents.x, spawnHalfExtents.x);
                float z = Random.Range(-spawnHalfExtents.y, spawnHalfExtents.y);

                Vector3 candidate = new Vector3(centre.x + x, buttonY, centre.z + z);

                bool tooClose = false;
                for (int p = 0; p < filled; p++)
                    if (Vector3.Distance(candidate, _spawnPositions[p]) < minButtonSeparation)
                    {
                        tooClose = true;
                        break;
                    }

                if (!tooClose)
                {
                    _spawnPositions[i] = candidate;
                    filled++;
                    placed = true;
                }
            }
        }

        return _spawnPositions;
    }
}
