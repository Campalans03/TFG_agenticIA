using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns a grid of environments for parallel training.
/// Holds a catalog of environment prefabs and lets you pick which one to spawn
/// from a dropdown in the inspector.
/// </summary>
public class EnvironmentGrid : MonoBehaviour
{
    [Serializable]
    public class EnvironmentEntry
    {
        public string name = "Environment";
        public GameObject prefab;
    }

    [Header("Grid Settings")] [Tooltip("Number of environments along the X-axis.")]
    public int gridSizeX = 2;

    [Tooltip("Number of environments along the Z-axis.")]
    public int gridSizeZ = 2;

    [Tooltip("Spacing between environments.")]
    public float spacing = 10f;

    [Header("Environment Catalog")]
    [Tooltip("List of available environment prefabs. Add as many as you want and pick one below.")]
    public List<EnvironmentEntry> environments = new List<EnvironmentEntry>();

    [Tooltip("Index of the entry from 'environments' that will be spawned in the grid. " +
             "Edited via the dropdown in the custom inspector.")]
    public int selectedEnvironment = 0;

    void Start()
    {
        if (environments == null || environments.Count == 0)
        {
            Debug.LogWarning("EnvironmentGrid: no environment prefabs configured.", this);
            return;
        }

        int idx = Mathf.Clamp(selectedEnvironment, 0, environments.Count - 1);
        GameObject prefab = environments[idx].prefab;

        if (prefab == null)
        {
            Debug.LogWarning($"EnvironmentGrid: selected entry '{environments[idx].name}' has no prefab assigned.",
                this);
            return;
        }

        for (int x = 0; x < gridSizeX; x++)
        {
            for (int z = 0; z < gridSizeZ; z++)
            {
                Vector3 position = new Vector3(x * spacing, 0, z * spacing);
                Instantiate(prefab, position, Quaternion.identity, transform);
            }
        }
    }
}