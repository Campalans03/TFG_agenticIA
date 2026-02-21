using UnityEngine;

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
    [Tooltip("Activate this child when shape == Cuadrado")]
    public GameObject meshCuadrado;

    [Tooltip("Activate this child when shape == Circulo")]
    public GameObject meshCirculo;

    [Tooltip("Activate this child when shape == Triangulo")]
    public GameObject meshTriangulo;

    [Header("Color Palette")]
    public Color colorRojo  = Color.red;
    public Color colorVerde = Color.green;
    public Color colorAzul  = Color.blue;

    // ─────────────────────────────────────────────────────────────
    //  Runtime data (read by ListenerAgent via raycast)
    // ─────────────────────────────────────────────────────────────

    /// <summary>Current logical color of this button.</summary>
    public ButtonColor ButtonColorValue { get; private set; }

    /// <summary>Current logical shape of this button.</summary>
    public ButtonShape ButtonShapeValue { get; private set; }

    /// <summary>Slot index (0-2) assigned by EnvironmentManager each episode.</summary>
    public int SlotIndex { get; private set; }

    // Materiales cacheados para evitar instanciar uno nuevo en cada Apply()
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
        // Instancia los materiales UNA sola vez y los guarda
        var renderers = GetComponentsInChildren<Renderer>();
        _cachedMaterials = new Material[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
            _cachedMaterials[i] = renderers[i].material; // instancia aquí, una vez
    }

    void ApplyShape(ButtonShape shape)
    {
        if (meshCuadrado  != null) meshCuadrado.SetActive(shape  == ButtonShape.Cuadrado);
        if (meshCirculo   != null) meshCirculo.SetActive(shape   == ButtonShape.Circulo);
        if (meshTriangulo != null) meshTriangulo.SetActive(shape == ButtonShape.Triangulo);
    }

    void ApplyColor(ButtonColor btnColor)
    {
        Color unityColor = btnColor switch
        {
            ButtonColor.Rojo  => colorRojo,
            ButtonColor.Verde => colorVerde,
            ButtonColor.Azul  => colorAzul,
            _                 => Color.white
        };

        // Usa los materiales cacheados, NO rend.material (que crea instancias nuevas)
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
