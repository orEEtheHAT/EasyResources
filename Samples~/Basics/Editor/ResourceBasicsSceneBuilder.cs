using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EasyResources.Samples.Editor
{
    public static class ResourceBasicsSceneBuilder
    {
        [MenuItem("Tools/EasyResources/Create Basics Scene")]
        public static void CreateFromMenu()
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Exit Play Mode before creating a scene.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var openScene = SceneManager.GetSceneAt(i);
                if (string.IsNullOrEmpty(openScene.path) && !EditorSceneManager.SaveScene(openScene))
                    return;
            }

            var folder = AssetDatabase.GenerateUniqueAssetPath("Assets/EasyResourcesBasics");
            var path = CreateAt(folder);
            EditorSceneManager.OpenScene(path);
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
        }

        /// <summary>Creates the same public example as the menu. Existing assets are never overwritten.</summary>
        public static string CreateAt(string folder)
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Exit Play Mode first.");
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal) || folder.Contains(".."))
                throw new ArgumentException("Choose a folder under Assets.", nameof(folder));
            for (var i = 0; i < SceneManager.sceneCount; i++)
                if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                    throw new InvalidOperationException("Save the current untitled scene before creating a new example, or open the supplied ResourceBasics.unity scene.");
            EnsureFolder(folder);
            var scenePath = folder + "/ResourceBasics.unity";
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) != null)
                throw new InvalidOperationException("This example already exists. Open " + scenePath);
            var wood = CreateResource(folder, "Wood");
            var stone = CreateResource(folder, "Stone");
            var cost = ScriptableObject.CreateInstance<ResourceCostAsset>();
            AssetDatabase.CreateAsset(cost, AssetDatabase.GenerateUniqueAssetPath(folder + "/Building Cost.asset"));
            SetAmounts(new SerializedObject(cost), "cost", wood, 4, stone, 2);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var root = new GameObject("EasyResources Basics");
                SceneManager.MoveGameObjectToScene(root, scene);
                var cameraObject = new GameObject("Camera");
                cameraObject.transform.SetParent(root.transform);
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.035f, 0.065f, 0.1f);
                var a = CreateContainer("Container A", root.transform, wood, 10, stone, 5);
                var b = CreateContainer("Container B", root.transform, wood, 3, stone, 1);
                var demo = root.AddComponent<ResourceBasicsDemo>();
                var serialized = new SerializedObject(demo);
                serialized.FindProperty("containerA").objectReferenceValue = a;
                serialized.FindProperty("containerB").objectReferenceValue = b;
                serialized.FindProperty("wood").objectReferenceValue = wood;
                serialized.FindProperty("stone").objectReferenceValue = stone;
                serialized.FindProperty("buildingCost").objectReferenceValue = cost;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.SaveScene(scene, scenePath);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }

            AssetDatabase.SaveAssets();
            return scenePath;
        }

        private static ResourceDefinition CreateResource(string folder, string name)
        {
            var resource = ResourceDefinition.Create(name);
            AssetDatabase.CreateAsset(resource, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + ".asset"));
            return resource;
        }

        private static ResourceContainer CreateContainer(string name, Transform root, ResourceDefinition wood, int woodAmount, ResourceDefinition stone, int stoneAmount)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root);
            var container = go.AddComponent<ResourceContainer>();
            SetAmounts(new SerializedObject(container), "initialContents", wood, woodAmount, stone, stoneAmount);
            return container;
        }

        private static void SetAmounts(SerializedObject target, string field, ResourceDefinition wood, int woodAmount, ResourceDefinition stone, int stoneAmount)
        {
            var rows = target.FindProperty(field).FindPropertyRelative("items");
            rows.arraySize = 2;
            rows.GetArrayElementAtIndex(0).FindPropertyRelative("resource").objectReferenceValue = wood;
            rows.GetArrayElementAtIndex(0).FindPropertyRelative("amount").intValue = woodAmount;
            rows.GetArrayElementAtIndex(1).FindPropertyRelative("resource").objectReferenceValue = stone;
            rows.GetArrayElementAtIndex(1).FindPropertyRelative("amount").intValue = stoneAmount;
            target.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;
            var index = folder.LastIndexOf('/');
            EnsureFolder(folder.Substring(0, index));
            AssetDatabase.CreateFolder(folder.Substring(0, index), folder.Substring(index + 1));
        }
    }
}
