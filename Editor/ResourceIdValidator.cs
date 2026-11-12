using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace EasyResources.Editor
{
    [InitializeOnLoad]
    public static class ResourceIdValidator
    {
        private static ResourceDefinition[] definitions;
        private static DefinitionAsset[] allDefinitions;
        static ResourceIdValidator()
        {
            EditorApplication.projectChanged += Invalidate;
        }

        public static void Invalidate()
        {
            definitions = null;
            allDefinitions = null;
        }

        public static DefinitionAsset[] GetAllDefinitions()
        {
            if (allDefinitions != null)
                return allDefinitions;
            var result = new List<DefinitionAsset>();
            foreach (var guid in AssetDatabase.FindAssets("t:DefinitionAsset"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<DefinitionAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null)
                    result.Add(asset);
            }

            return allDefinitions = result.ToArray();
        }

        public static ResourceDefinition[] GetDefinitions()
        {
            if (definitions != null)
                return definitions;
            var result = new List<ResourceDefinition>();
            foreach (var guid in AssetDatabase.FindAssets("t:ResourceDefinition"))
            {
                var resource = AssetDatabase.LoadAssetAtPath<ResourceDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (resource != null)
                    result.Add(resource);
            }

            definitions = result.ToArray();
            return definitions;
        }

        public static string FindConflict(DefinitionAsset resource)
        {
            foreach (var other in GetAllDefinitions())
                if (other != null && other != resource && other.Id == resource.Id)
                    return AssetDatabase.GetAssetPath(other);
            return null;
        }

        public static string[] ValidateAll()
        {
            Invalidate();
            var errors = new List<string>();
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var resource in GetAllDefinitions())
            {
                var path = AssetDatabase.GetAssetPath(resource);
                if (string.IsNullOrWhiteSpace(resource.Id))
                {
                    errors.Add($"Empty resource ID: {path}");
                    continue;
                }

                if (ids.TryGetValue(resource.Id, out var first))
                    errors.Add($"Duplicate resource ID '{resource.Id}': {first} and {path}. Select the new copy and use Give This Copy a New ID.");
                else
                    ids.Add(resource.Id, path);
            }

            return errors.ToArray();
        }

        [MenuItem("Tools/EasyResources/Validate Resource IDs")]
        private static void ValidateMenu()
        {
            var errors = ValidateAll();
            foreach (var error in errors)
                Debug.LogError(error);
            if (errors.Length == 0)
                Debug.Log($"EasyResources: {GetAllDefinitions().Length} definition IDs are valid and unique.");
        }
    }

    internal sealed class ResourceIdBuildValidator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var errors = ResourceIdValidator.ValidateAll();
            if (errors.Length != 0)
                throw new BuildFailedException(string.Join("\n", errors));
        }
    }
}
