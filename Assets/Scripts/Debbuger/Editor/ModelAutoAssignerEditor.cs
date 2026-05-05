using System.IO;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ModelAutoAssigner))]
public class ModelAutoAssignerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var assigner = (ModelAutoAssigner)target;

        EditorGUILayout.PropertyField(serializedObject.FindProperty("folderPath"));

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Pick Folder..."))
            {
                string start = !string.IsNullOrEmpty(assigner.folderPath) && Directory.Exists(assigner.folderPath)
                    ? assigner.folderPath
                    : "Assets";
                string picked = EditorUtility.OpenFolderPanel("Select model folder", start, "");
                if (!string.IsNullOrEmpty(picked))
                {
                    string projectRoot = Path.GetFullPath(Application.dataPath + "/..").Replace('\\', '/');
                    string normalized = picked.Replace('\\', '/');
                    string finalPath = normalized.StartsWith(projectRoot)
                        ? normalized.Substring(projectRoot.Length).TrimStart('/')
                        : normalized;
                    serializedObject.FindProperty("folderPath").stringValue = finalPath;
                }
            }

            if (GUILayout.Button("Reveal"))
            {
                if (Directory.Exists(assigner.folderPath))
                    EditorUtility.RevealInFinder(assigner.folderPath);
            }
        }

        EditorGUILayout.PropertyField(serializedObject.FindProperty("recursive"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("strategy"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("speakerPrefix"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("listenerPrefix"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("speakerBehaviorParameters"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("listenerBehaviorParameters"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("assignOnAwake"));

        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(!Directory.Exists(assigner.folderPath)))
        {
            if (GUILayout.Button("Apply Latest Models", GUILayout.Height(28)))
            {
                assigner.ApplyLatestModels(verbose: true);
            }
        }

        if (!Directory.Exists(assigner.folderPath))
        {
            EditorGUILayout.HelpBox("Folder does not exist. Pick a valid folder above.", MessageType.Warning);
        }
        else
        {
            string speakerPath = ModelAutoAssigner.FindLatestOnnx(
                assigner.folderPath, assigner.speakerPrefix, assigner.recursive, assigner.strategy);
            string listenerPath = ModelAutoAssigner.FindLatestOnnx(
                assigner.folderPath, assigner.listenerPrefix, assigner.recursive, assigner.strategy);

            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Speaker →", string.IsNullOrEmpty(speakerPath) ? "(none found)" : speakerPath);
            EditorGUILayout.LabelField("Listener →",
                string.IsNullOrEmpty(listenerPath) ? "(none found)" : listenerPath);
        }
    }
}