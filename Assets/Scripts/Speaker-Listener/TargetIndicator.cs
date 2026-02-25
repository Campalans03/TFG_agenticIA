using UnityEngine;

/// <summary>
/// Adds a 3D developer-only visual marker above the correct button of the current episode.
/// This object is NOT visible to agents (no Collider, no agent layer,
/// no impact on observations). Purely cosmetic.
/// </summary>
public class TargetIndicator : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────
    //  Inspector
    // ─────────────────────────────────────────────────────────────

    [Header("References")]
    public EnvironmentManager env;

    [Header("Visual Settings")]
    [Tooltip("Marker color when the target is valid.")]
    public Color targetColor = new Color(1f, 0.92f, 0.016f, 0.85f); // yellow

    [Tooltip("Height above the correct button.")]
    public float heightOffset = 1f;

    [Tooltip("Bob amplitude (up/down).")]
    public float bobAmplitude = 0.15f;

    [Tooltip("Bob frequency (Hz).")]
    public float bobFrequency = 1.5f;

    [Header("Arrow Geometry")]
    [Tooltip("Arrow total height (shaft + head).")]
    public float arrowHeight = 0.9f;

    [Tooltip("Arrow shaft radius.")]
    public float shaftRadius = 0.06f;

    [Tooltip("Arrow head height.")]
    public float headHeight = 0.35f;

    [Tooltip("Arrow head radius (base).")]
    public float headRadius = 0.16f;

    [Tooltip("How many radial segments for the cone mesh.")]
    [Range(8, 64)]
    public int coneSegments = 24;

    // ─────────────────────────────────────────────────────────────
    //  Private state
    // ─────────────────────────────────────────────────────────────

    private GameObject _markerRoot;
    private Renderer _shaftRenderer;
    private Renderer _headRenderer;

    private Vector3 _basePosition;
    private float _bobTimer;

    // ─────────────────────────────────────────────────────────────
    //  Unity lifecycle
    // ─────────────────────────────────────────────────────────────

    void Start()
    {
        BuildMarker();
    }

    void Update()
    {
        if (env == null || _markerRoot == null) return;

        int correctIdx = env.GetCorrectButtonIndex();
        bool valid = correctIdx >= 0 && correctIdx <= 2;

        if (!valid)
        {
            _markerRoot.SetActive(false);
            return;
        }

        _markerRoot.SetActive(true);
        _basePosition = env.GetButtonWorldPosition(correctIdx) + Vector3.up * heightOffset;

        // Bob
        _bobTimer += Time.deltaTime;
        float bob = Mathf.Sin(_bobTimer * bobFrequency * 2f * Mathf.PI) * bobAmplitude;
        _markerRoot.transform.position = _basePosition + Vector3.up * bob;

        // Update material color
        UpdateColor(targetColor);
    }

    // ─────────────────────────────────────────────────────────────
    //  Build the arrow marker at runtime (no assets needed)
    // ─────────────────────────────────────────────────────────────

    void BuildMarker()
    {
        // Empty root that floats and rotates
        string envName = env != null ? env.name : "NoEnv";
        _markerRoot = new GameObject($"TargetIndicator_{envName}");
        _markerRoot.transform.SetParent(null);

        // Create a simple emissive material (URP Lit -> Standard -> Diffuse fallback)
        Shader litShader = Shader.Find("Universal Render Pipeline/Lit")
                        ?? Shader.Find("Standard")
                        ?? Shader.Find("Diffuse");
        var mat = new Material(litShader);
        ApplyEmission(mat, targetColor);

        // ─── Shaft (Cylinder) ───
        var shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        shaft.name = "ArrowShaft";
        shaft.transform.SetParent(_markerRoot.transform, false);

        // Cylinder height in Unity is 2 units by default, so Y scale controls half-height.
        float shaftHeight = Mathf.Max(0.01f, arrowHeight - headHeight);
        shaft.transform.localScale = new Vector3(shaftRadius * 2f, shaftHeight * 0.5f, shaftRadius * 2f);

        // Place shaft so the arrow points DOWN: shaft center above head.
        // We'll build arrow centered around local origin, pointing down along -Y.
        // Shaft center is at -(headHeight + shaftHeight/2)
        shaft.transform.localPosition = new Vector3(0f, -(headHeight - shaftHeight * 0.5f), 0f);

        Destroy(shaft.GetComponent<Collider>());
        _shaftRenderer = shaft.GetComponent<Renderer>();
        _shaftRenderer.material = mat;
        _shaftRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _shaftRenderer.receiveShadows = false;

        // ─── Head (Procedural Cone Mesh) ───
        var head = new GameObject("ArrowHead");
        head.transform.SetParent(_markerRoot.transform, false);
        head.transform.localPosition = new Vector3(0f, -headHeight * 0.5f, 0f); // head occupies [0 .. -headHeight]
        head.transform.localRotation = Quaternion.identity;

        var mf = head.AddComponent<MeshFilter>();
        var mr = head.AddComponent<MeshRenderer>();
        mf.sharedMesh = BuildConeMesh(headRadius, headHeight, Mathf.Max(8, coneSegments));
        mr.material = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        _headRenderer = mr;

        // Start hidden
        _markerRoot.SetActive(false);
    }

    Mesh BuildConeMesh(float radius, float height, int segments)
    {
        // Cone pointing DOWN along -Y:
        // Tip at (0, -height, 0)
        // Base center at (0, 0, 0)
        var mesh = new Mesh();
        mesh.name = "ProceduralCone";

        int vertCount = 1 /*tip*/ + segments /*base ring*/ + 1 /*base center*/;
        var verts = new Vector3[vertCount];
        var uvs = new Vector2[vertCount];

        int tipIndex = 0;
        int ringStart = 1;
        int baseCenterIndex = ringStart + segments;

        // Tip
        verts[tipIndex] = new Vector3(0f, -height, 0f);
        uvs[tipIndex] = new Vector2(0.5f, 0f);

        // Base ring
        for (int i = 0; i < segments; i++)
        {
            float t = i / (float)segments;
            float ang = t * Mathf.PI * 2f;
            float x = Mathf.Cos(ang) * radius;
            float z = Mathf.Sin(ang) * radius;
            verts[ringStart + i] = new Vector3(x, 0f, z);
            uvs[ringStart + i] = new Vector2(t, 1f);
        }

        // Base center
        verts[baseCenterIndex] = Vector3.zero;
        uvs[baseCenterIndex] = new Vector2(0.5f, 1f);

        // Triangles: sides + base
        // Sides: tip -> ring[i] -> ring[i+1]
        // Base: center -> ring[i+1] -> ring[i]
        int triCount = segments * 2; // side + base per segment
        var tris = new int[triCount * 3];
        int ti = 0;

        for (int i = 0; i < segments; i++)
        {
            int i0 = ringStart + i;
            int i1 = ringStart + ((i + 1) % segments);

            // Side (ensure winding is correct for outward normals)
            tris[ti++] = tipIndex;
            tris[ti++] = i1;
            tris[ti++] = i0;

            // Base (winding for downward-facing base; adjust if you want it visible from above)
            tris[ti++] = baseCenterIndex;
            tris[ti++] = i0;
            tris[ti++] = i1;
        }

        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    void UpdateColor(Color c)
    {
        if (_shaftRenderer != null)
        {
            var mat = _shaftRenderer.material;
            mat.color = c;
            if (mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", c * 2f);
        }

        if (_headRenderer != null)
        {
            var mat = _headRenderer.material;
            mat.color = c;
            if (mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", c * 2f);
        }
    }

    void ApplyEmission(Material mat, Color c)
    {
        if (mat == null) return;

        mat.color = c;

        // Try to enable emission across common shaders
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", c * 2f);
        }
    }

    void OnDestroy()
    {
        if (_markerRoot != null)
            Destroy(_markerRoot);
    }
}