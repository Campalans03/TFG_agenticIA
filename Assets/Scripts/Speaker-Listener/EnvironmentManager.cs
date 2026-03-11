using UnityEngine;

public enum ButtonColor { Red, Green, Blue }
public enum ButtonShape { Square, Circle, Triangle }

[System.Serializable]
public struct ButtonData
{
    public ButtonColor color;
    public ButtonShape shape;
}

public class EnvironmentManager : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────
    //  Inspector
    // ─────────────────────────────────────────────────────────────

    [Header("Episode Settings")]
    public int maxResampleTries = 50;

    [Header("Shared Reward")]
    public float correctReward   =  1.0f;
    public float wrongReward     = -1.0f;
    public float stepPenalty     = -0.001f;

    [Header("Communication")]
    [Tooltip("Vocabulary size. Must match the Speaker's Discrete Branch size.")]
    public int vocabSize    = 9;
    public int silenceToken = 0;

    [Header("Buttons (exactly 3)")]
    [Tooltip("Assign the 3 ButtonController GameObjects here.")]
    public ButtonController[] buttonObjects = new ButtonController[3];

    [Header("Spawn Area")]
    [Tooltip("Centre of the zone where buttons can be randomly placed.")]
    public Transform spawnAreaCenter;
    [Tooltip("Half-extents for button placement (XZ plane).")]
    public Vector2 spawnHalfExtents = new Vector2(3f, 3f);
    [Tooltip("Minimum distance between any two buttons.")]
    public float minButtonSeparation = 1.5f;
    [Tooltip("Y position for all buttons.")]
    public float buttonY = 0.5f;

    [Header("Agent Refs")]
    public SpeakerAgent  speaker;
    public ListenerAgent listener;

    // ─────────────────────────────────────────────────────────────
    //  Runtime data (read by agents)
    // ─────────────────────────────────────────────────────────────

    /// <summary>Logical button data (color + shape) indexed by slot 0-2.</summary>
    public ButtonData[] buttons { get; private set; } = new ButtonData[3];

    /// <summary>Current rule: which color/shape is correct.</summary>
    public ButtonColor targetColor  { get; private set; }
    public ButtonShape targetShape  { get; private set; }

    /// <summary>Last token emitted by the Speaker.</summary>
    public int currentMessageToken { get; private set; } = 0;

    // ─────────────────────────────────────────────────────────────
    //  Public API
    // ───────────────────────────────────────────────────────────── 

    public void ResetEpisode()
    {
        // ── 1. Randomise logical button properties ──
        bool ok = false;
        for (int attempt = 0; attempt < maxResampleTries && !ok; attempt++)
        {
            for (int i = 0; i < 3; i++)
            {
                buttons[i] = new ButtonData
                {
                    color = (ButtonColor)Random.Range(0, 3),
                    shape = (ButtonShape)Random.Range(0, 3)
                };
            }

            targetColor  = (ButtonColor)Random.Range(0, 3);
            targetShape  = (ButtonShape)Random.Range(0, 3);

            ok = AllButtonsUnique() && EpisodeHasValidSolution();
        }

        // ── 2. Randomise physical positions ──
        Vector3[] positions = GenerateScatteredPositions(3);

        // ── 3. Apply to ButtonController GameObjects ──
        for (int i = 0; i < 3; i++)
        {
            if (buttonObjects[i] == null) continue;
            buttonObjects[i].transform.localPosition = positions[i];
            buttonObjects[i].Apply(buttons[i].color, buttons[i].shape, i);
        }

        // ── 4. Reset communication channel ──
        currentMessageToken = silenceToken;

        // ── 5. Ask the Speaker to emit its token exactly once for this episode ──
        speaker.RequestDecision();
    }

    public void SetMessageToken(int token)
    {
        currentMessageToken = Mathf.Clamp(token, 0, vocabSize - 1);
        Debug.Log($"[Speaker] emitted token {currentMessageToken}.");
    }

    public void ApplyStepPenalty()
    {
        speaker.AddReward(stepPenalty);
        listener.AddReward(stepPenalty);
    }
    
    public void ApplyOutOfTimePenalty()
    {
        speaker.AddReward(wrongReward);
        listener.AddReward(wrongReward);
    }

    public void ListenerChoseButton(int chosenIndex)
    {
        int correct = GetCorrectButtonIndex();

        if (chosenIndex == correct)
        {
            speaker.AddReward(correctReward);
            listener.AddReward(correctReward);
        }
        else
        {
            speaker.AddReward(wrongReward);
            listener.AddReward(wrongReward);
        }

        speaker.EndEpisode();
        listener.EndEpisode();
    }

    /// <summary>Ends the episode for all the Agents.</summary>
    public void EndEpisodeAll()
    {
        speaker.EndEpisode();
        listener.EndEpisode();
    }

    /// <summary>Returns the world position of button slot i (used for physics/placement).</summary>
    public Vector3 GetButtonWorldPosition(int slotIndex)
    {
        if (buttonObjects[slotIndex] != null)
            return buttonObjects[slotIndex].transform.position;
        return transform.position;
    }

    /// <summary>
    /// Returns the position of button slot i in the environment's local space.
    /// Use this for all agent observations, so they are environment-agnostic
    /// and identical across parallel training instances.
    /// </summary>
    public Vector3 GetButtonLocalPosition(int slotIndex)
    {
        if (buttonObjects[slotIndex] != null)
            return buttonObjects[slotIndex].transform.localPosition;
        return Vector3.zero;
    }

    // ─────────────────────────────────────────────────────────────
    //  Rule helpers
    // ─────────────────────────────────────────────────────────────

    public int GetCorrectButtonIndex()
    {
        for (int i = 0; i < 3; i++)
            if (buttons[i].color == targetColor && buttons[i].shape == targetShape)
                return i;

        return -1;
    }

    bool AllButtonsUnique()
    {
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++)
                if (buttons[i].color == buttons[j].color && buttons[i].shape == buttons[j].shape)
                    return false;
        return true;
    }

    bool EpisodeHasValidSolution()
    {
        int idx = GetCorrectButtonIndex();
        return idx >= 0 && idx <= 2;
    }

    // ─────────────────────────────────────────────────────────────
    //  Scatter positions
    // ─────────────────────────────────────────────────────────────

    // Pre-allocated to avoid GC every episode
    private readonly Vector3[] _spawnPositions = new Vector3[3];

    Vector3[] GenerateScatteredPositions(int count)
    {
        // Everything in local space. Buttons are children of this transform,
        // so buttonObjects[i].transform.localPosition = candidate is all that's needed.
        Vector3 centre = spawnAreaCenter != null
            ? spawnAreaCenter.localPosition
            : Vector3.zero;

        int filled      = 0;
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
                    { tooClose = true; break; }

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