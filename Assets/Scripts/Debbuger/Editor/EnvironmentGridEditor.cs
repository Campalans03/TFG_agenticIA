using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(EnvironmentGrid))]
public class EnvironmentGridEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(serializedObject.FindProperty("gridSizeX"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("gridSizeZ"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("spacing"));

        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("environments"), true);

        var grid = (EnvironmentGrid)target;
        var selectedProp = serializedObject.FindProperty("selectedEnvironment");

        if (grid.environments != null && grid.environments.Count > 0)
        {
            string[] names = new string[grid.environments.Count];
            for (int i = 0; i < grid.environments.Count; i++)
            {
                var entry = grid.environments[i];
                string label = (entry == null || string.IsNullOrEmpty(entry.name)) ? $"Entry {i}" : entry.name;
                names[i] = $"{i}: {label}";
            }

            int current = Mathf.Clamp(selectedProp.intValue, 0, names.Length - 1);
            int newIndex = EditorGUILayout.Popup("Selected Environment", current, names);
            selectedProp.intValue = newIndex;
        }
        else
        {
            EditorGUILayout.HelpBox(
                "Add at least one entry to 'Environments' to enable selection.",
                MessageType.Info);
        }

        serializedObject.ApplyModifiedProperties();
    }
}
