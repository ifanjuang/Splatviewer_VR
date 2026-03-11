using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class BuildSetup
{
    static BuildSetup()
    {
        var scenes = EditorBuildSettings.scenes;
        string scenePath = "Assets/GSTestScene.unity";

        foreach (var s in scenes)
        {
            if (s.path == scenePath)
                return; // already added
        }

        var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes);
        list.Add(new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
        Debug.Log($"[BuildSetup] Added '{scenePath}' to Build Settings scenes.");
    }
}
