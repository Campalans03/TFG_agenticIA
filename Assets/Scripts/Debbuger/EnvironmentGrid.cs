using UnityEngine;

/// <summary>
/// A simple script to create multiple environments in a grid layout for parallel training.
/// </summary>
public class EnvironmentGrid : MonoBehaviour
{
    [Header("Grid Settings")]
    [Tooltip("Number of environments along the X-axis.")]
    public int gridSizeX = 2;

    [Tooltip("Number of environments along the Z-axis.")]
    public int gridSizeZ = 2;

    [Tooltip("Spacing between environments.")]
    public float spacing = 10f;

    [Header("Environment Prefab")]
    [Tooltip("Prefab of the environment to instantiate in the grid.")]
    public GameObject environmentPrefab;

    void Start()
    {
        for (int x = 0; x < gridSizeX; x++)
        {
            for (int z = 0; z < gridSizeZ; z++)
            {
                Vector3 position = new Vector3(x * spacing, 0, z * spacing);
                Instantiate(environmentPrefab, position, Quaternion.identity, transform);
            }
        }
    }
    
}
