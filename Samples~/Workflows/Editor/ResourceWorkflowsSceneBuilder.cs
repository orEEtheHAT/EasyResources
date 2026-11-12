using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EasyResources.Samples.Editor
{
    public static class ResourceWorkflowsSceneBuilder
    {
        [MenuItem("Tools/EasyResources/Create Workflows Scene")]
        public static void Create()
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Exit Play Mode before creating a scene.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            var folder = AssetDatabase.GenerateUniqueAssetPath("Assets/EasyResourcesWorkflows");
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            var path = CreateAt(folder);
            EditorSceneManager.OpenScene(path);
        }

        public static string CreateAt(string folder)
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Exit Play Mode before creating a scene.");
            if (string.IsNullOrWhiteSpace(folder) || !folder.StartsWith("Assets/", StringComparison.Ordinal) || folder.Contains(".."))
                throw new ArgumentException("Choose a folder under Assets.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(folder + "/ResourceWorkflows.unity") != null)
                throw new InvalidOperationException("A workflow scene already exists in this folder.");
            var active = SceneManager.GetActiveScene();
            var replaceEmpty = SceneManager.sceneCount == 1 && string.IsNullOrEmpty(active.path) && !active.isDirty;
            if (!replaceEmpty)
                for (var i = 0; i < SceneManager.sceneCount; i++)
                    if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                        throw new InvalidOperationException("Save the untitled scene before creating a workflow scene.");
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, replaceEmpty ? NewSceneMode.Single : NewSceneMode.Additive);
            try
            {
                var go = new GameObject("EasyResources Workflows");
                SceneManager.MoveGameObjectToScene(go, scene);
                go.AddComponent<ResourceWorkflowsDemo>();
                var camera = new GameObject("Camera");
                SceneManager.MoveGameObjectToScene(camera, scene);
                var view = camera.AddComponent<Camera>();
                view.clearFlags = CameraClearFlags.SolidColor;
                view.backgroundColor = new Color(.035f, .045f, .065f);
                var path = folder + "/ResourceWorkflows.unity";
                EditorSceneManager.SaveScene(scene, path);
                return path;
            }
            finally
            {
                if (!replaceEmpty)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
