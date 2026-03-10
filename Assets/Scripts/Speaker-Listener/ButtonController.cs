using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Attached to each Button GameObject in the scene.
/// Handles visual representation (color + shape) and exposes logical data
/// so that the Listener's raycast can read it directly from the hit object.
/// 
/// SETUP:
///  - Add this component to each of the 3 button GameObjects.
///  - Set the tag "Button" on each one.
///  - Assign the child mesh GameObjects for each shape variant.
///  - Place the objects on the layer defined in ListenerAgent.buttonLayer.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ButtonController : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────
    //  Inspector
    // ─────────────────────────────────────────────────────────────
    
    [Header("Shape GameObjects (one per shape variant)")]
    
    [Tooltip("Activate this child when shape = square")]
    public GameObject meshSquare;

    [Tooltip("Activate this child when shape = circle")]
    public GameObject meshCircle;

    [Tooltip("Activate this child when shape == triangle")]
    public GameObject meshTriangle;

    [Header("Color Palette")]
    public Color colorRed = Color.red;
    public Color colorGreen = Color.green;
    public Color colorBlue = Color.blue;

    // ─────────────────────────────────────────────────────────────
    //  Runtime data (read by ListenerAgent via raycast)
    // ─────────────────────────────────────────────────────────────

    /// <summary>Current logical color of this button.</summary>
    public ButtonColor ButtonColorValue { get; private set; }

    /// <summary>Current logical shape of this button.</summary>
    public ButtonShape ButtonShapeValue { get; private set; }

    /// <summary>Slot index (0-2) assigned by EnvironmentManager each episode.</summary>
    public int SlotIndex { get; private set; }

    /// Cached materials for efficient color updates.
    private Material[] _cachedMaterials;

    // ─────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by EnvironmentManager every episode reset.
    /// Updates the visual mesh and material colour to match the new logical data.
    /// </summary>
    public void Apply(ButtonColor newColor, ButtonShape newShape, int slotIndex = -1)
    {
        ButtonColorValue = newColor;
        ButtonShapeValue = newShape;
        if (slotIndex >= 0) SlotIndex = slotIndex;

        ApplyShape(newShape);
        ApplyColor(newColor);
    }

    // ─────────────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────────────

    void Awake()
    {
        // Creates and caches all the possible materials 
        var renderers = GetComponentsInChildren<Renderer>();
        _cachedMaterials = new Material[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
            _cachedMaterials[i] = renderers[i].material; 
    }

    void ApplyShape(ButtonShape shape)
    {
        if (meshSquare != null) meshSquare.SetActive(shape  == ButtonShape.Square);
        if (meshCircle != null) meshCircle.SetActive(shape   == ButtonShape.Circle);
        if (meshTriangle != null) meshTriangle.SetActive(shape == ButtonShape.Triangle);
    }

    void ApplyColor(ButtonColor btnColor)
    {
        Color unityColor = btnColor switch
        {
            ButtonColor.Red => colorRed,
            ButtonColor.Green => colorGreen,
            ButtonColor.Blue => colorBlue,
            _ => Color.white
        };

        // Use the materials in the cache
        if (_cachedMaterials == null) return;
        foreach (var mat in _cachedMaterials)
            if (mat != null) mat.color = unityColor;
    }

    // ─────────────────────────────────────────────────────────────
    //  Debug
    // ─────────────────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, 0.5f);

#if UNITY_EDITOR
        UnityEditor.Handles.Label(
            transform.position + Vector3.up * 0.8f,
            $"Slot:{SlotIndex}  {ButtonColorValue}  {ButtonShapeValue}");
#endif
    }
}
