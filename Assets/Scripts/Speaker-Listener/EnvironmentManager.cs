using UnityEngine;

public enum ButtonColor { Rojo, Verde, Azul }
public enum ButtonShape { Cuadrado, Circulo, Triangulo }

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
    public int   vocabSize       = 8;
    public int   silenceToken    = 0;
    public float speakPenalty    = -0.0005f;

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

    /// <summary>Current rule: which color/shape is correct and optional constraint.</summary>
    public ButtonColor targetColor  { get; private set; }
    public ButtonShape targetShape  { get; private set; }
    public bool        requireNoRed { get; private set; }

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
            requireNoRed = Random.value < 0.5f;

            ok = EpisodeHasValidSolution();
        }

        // ── 2. Randomise physical positions ──
        Vector3[] positions = GenerateScatteredPositions(3);

        // ── 3. Apply to ButtonController GameObjects ──
        for (int i = 0; i < 3; i++)
        {
            if (buttonObjects[i] == null) continue;
            buttonObjects[i].transform.position = positions[i];
            buttonObjects[i].Apply(buttons[i].color, buttons[i].shape, i);
        }

        // ── 4. Reset communication channel ──
        currentMessageToken = silenceToken;
    }

    public void SetMessageToken(int token)
    {
        token = Mathf.Clamp(token, 0, vocabSize - 1);
        currentMessageToken = token;

        if (token != silenceToken)
        {
            speaker.AddReward(speakPenalty);
            listener.AddReward(speakPenalty);
        }
    }

    public void ApplyStepPenalty()
    {
        speaker.AddReward(stepPenalty);
        listener.AddReward(stepPenalty);
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

    /// <summary>Returns the world position of button slot i (runtime).</summary>
    public Vector3 GetButtonWorldPosition(int slotIndex)
    {
        if (buttonObjects[slotIndex] != null)
            return buttonObjects[slotIndex].transform.position;
        return Vector3.zero;
    }

    // ─────────────────────────────────────────────────────────────
    //  Rule helpers
    // ─────────────────────────────────────────────────────────────

    public int GetCorrectButtonIndex()
    {
        if (requireNoRed && AnyRedPresent()) return -1;

        for (int i = 0; i < 3; i++)
            if (buttons[i].color == targetColor && buttons[i].shape == targetShape)
                return i;

        return -1;
    }

    bool AnyRedPresent()
    {
        for (int i = 0; i < 3; i++)
            if (buttons[i].color == ButtonColor.Rojo) return true;
        return false;
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
        Vector3 centre = spawnAreaCenter != null
            ? spawnAreaCenter.position
            : transform.position;

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