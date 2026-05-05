using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>
/// Editor helper that scans a folder for .onnx models and assigns the most recent
/// Speaker/Listener models to their BehaviorParameters, so you don't have to drag them
/// in every time before pressing Play.
/// </summary>
public class ModelAutoAssigner : MonoBehaviour
{
    public enum LatestStrategy
    {
        /// <summary>Pick the .onnx with the most recent file modification time.</summary>
        FileModificationTime,

        /// <summary>Pick the .onnx with the highest step number parsed from filename
        /// (e.g. Speaker-12345.onnx → 12345). The unsuffixed Speaker.onnx is treated as
        /// the maximum step (final exported model).</summary>
        HighestStepInFilename
    }

    [Header("Folder to scan (relative to project, e.g. Assets/.../results/Test1)")]
    [Tooltip("Folder where the .onnx models live. Set with the 'Pick Folder...' button " +
             "in the inspector or paste a path manually.")]
    public string folderPath = "Assets/Scripts/Speaker-Listener/";

    [Tooltip("Search subfolders recursively (recommended — checkpoints live in Speaker/ and Listener/ subfolders).")]
    public bool recursive = true;

    [Tooltip("How to choose the 'latest' .onnx among matches.")]
    public LatestStrategy strategy = LatestStrategy.HighestStepInFilename;

    [Header("Filename matching")]
    [Tooltip(
        "Filename prefix that identifies the Speaker model (e.g. 'Speaker' matches Speaker.onnx and Speaker-187.onnx).")]
    public string speakerPrefix = "Speaker";

    [Tooltip("Filename prefix that identifies the Listener model.")]
    public string listenerPrefix = "Listener";

    [Header("Targets")] public BehaviorParameters speakerBehaviorParameters;
    public BehaviorParameters listenerBehaviorParameters;

    [Header("Runtime")]
    [Tooltip("If true, re-run the assignment automatically in Awake. Editor-only " +
             "(uses AssetDatabase, so it won't work in standalone builds).")]
    public bool assignOnAwake = false;

    public ModelAsset LastAssignedSpeakerModel { get; private set; }
    public ModelAsset LastAssignedListenerModel { get; private set; }

#if UNITY_EDITOR
    void Awake()
    {
        if (assignOnAwake)
        {
            ApplyLatestModels(verbose: true);
        }
    }

    /// <summary>
    /// Scans <see cref="folderPath"/> for .onnx files matching the speaker/listener prefixes
    /// and assigns the latest one to each BehaviorParameters component.
    /// Editor-only (uses AssetDatabase to load ModelAsset).
    /// </summary>
    public void ApplyLatestModels(bool verbose = false)
    {
        if (string.IsNullOrEmpty(folderPath))
        {
            Debug.LogWarning("ModelAutoAssigner: folderPath is empty.", this);
            return;
        }

        if (!System.IO.Directory.Exists(folderPath))
        {
            Debug.LogWarning($"ModelAutoAssigner: folder does not exist: {folderPath}", this);
            return;
        }

        ApplyLatestFor(speakerPrefix, speakerBehaviorParameters, "Speaker", verbose,
            asset => LastAssignedSpeakerModel = asset);
        ApplyLatestFor(listenerPrefix, listenerBehaviorParameters, "Listener", verbose,
            asset => LastAssignedListenerModel = asset);
    }

    void ApplyLatestFor(string prefix, BehaviorParameters bp, string label, bool verbose,
        System.Action<ModelAsset> recordAssigned)
    {
        if (bp == null)
        {
            Debug.LogWarning($"ModelAutoAssigner: {label} BehaviorParameters not assigned.", this);
            return;
        }

        string assetPath = FindLatestOnnx(folderPath, prefix, recursive, strategy);
        if (string.IsNullOrEmpty(assetPath))
        {
            Debug.LogWarning($"ModelAutoAssigner: no '{prefix}*.onnx' found under {folderPath}.", this);
            return;
        }

        var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<ModelAsset>(assetPath);
        if (asset == null)
        {
            Debug.LogWarning($"ModelAutoAssigner: failed to load ModelAsset at {assetPath}.", this);
            return;
        }

        UnityEditor.Undo.RecordObject(bp, $"Assign {label} model");
        bp.Model = asset;
        UnityEditor.EditorUtility.SetDirty(bp);
        recordAssigned?.Invoke(asset);

        if (verbose)
            Debug.Log($"ModelAutoAssigner: assigned {label} model → {assetPath}", bp);
    }

    /// <summary>
    /// Returns the project-relative path (e.g. "Assets/.../Speaker-187.onnx") of the latest
    /// .onnx in <paramref name="folder"/> matching <paramref name="prefix"/>, or null if none found.
    /// </summary>
    public static string FindLatestOnnx(string folder, string prefix, bool recursive, LatestStrategy strategy)
    {
        var searchOption = recursive
            ? System.IO.SearchOption.AllDirectories
            : System.IO.SearchOption.TopDirectoryOnly;

        string[] candidates;
        try
        {
            candidates = System.IO.Directory.GetFiles(folder, prefix + "*.onnx", searchOption);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"ModelAutoAssigner: error scanning '{folder}': {e.Message}");
            return null;
        }

        if (candidates == null || candidates.Length == 0) return null;

        string best = null;

        if (strategy == LatestStrategy.FileModificationTime)
        {
            System.DateTime bestTime = System.DateTime.MinValue;
            foreach (var c in candidates)
            {
                var t = System.IO.File.GetLastWriteTimeUtc(c);
                if (t > bestTime)
                {
                    bestTime = t;
                    best = c;
                }
            }
        }
        else // HighestStepInFilename
        {
            long bestStep = long.MinValue;
            foreach (var c in candidates)
            {
                long step = ExtractStep(System.IO.Path.GetFileNameWithoutExtension(c), prefix);
                if (step > bestStep)
                {
                    bestStep = step;
                    best = c;
                }
            }
        }

        if (best == null) return null;

        // Normalize to project-relative path with forward slashes (AssetDatabase requirement).
        string full = System.IO.Path.GetFullPath(best).Replace('\\', '/');
        string projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..").Replace('\\', '/');
        if (full.StartsWith(projectRoot))
            return full.Substring(projectRoot.Length).TrimStart('/');
        return best.Replace('\\', '/');
    }

    /// <summary>
    /// Extracts the step number from a filename like "Speaker-12345" → 12345.
    /// The unsuffixed name (e.g. "Speaker") returns long.MaxValue so the final exported
    /// model is preferred over checkpoints.
    /// </summary>
    static long ExtractStep(string fileNameNoExt, string prefix)
    {
        if (string.Equals(fileNameNoExt, prefix, System.StringComparison.Ordinal))
            return long.MaxValue;

        if (!fileNameNoExt.StartsWith(prefix, System.StringComparison.Ordinal))
            return long.MinValue;

        int dash = fileNameNoExt.IndexOf('-', prefix.Length);
        if (dash < 0) return long.MinValue;

        string tail = fileNameNoExt.Substring(dash + 1);
        return long.TryParse(tail, out long step) ? step : long.MinValue;
    }
#endif
}