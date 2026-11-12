using System;
using System.Linq;
using EasyResources.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace EasyResources.Tests
{
    public sealed class ResourceIdentityTests
    {
        [Test]
        public void AssetRenameMoveAndReloadPreserveIdAndCopyReportsDuplicate()
        {
            const string root = "Assets/PackageTests";
            if (!AssetDatabase.IsValidFolder(root))
                AssetDatabase.CreateFolder("Assets", "PackageTests");
            if (!AssetDatabase.IsValidFolder(root + "/EasyResources"))
                AssetDatabase.CreateFolder(root, "EasyResources");
            var folder = root + "/EasyResources/IdentityTest-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(root + "/EasyResources", folder.Substring(folder.LastIndexOf('/') + 1));
            try
            {
                var definition = ScriptableObject.CreateInstance<ResourceDefinition>();
                var path = folder + "/Wood.asset";
                AssetDatabase.CreateAsset(definition, path);
                AssetDatabase.SaveAssets();
                var original = definition.Id;
                Assert.That(original, Is.Not.Empty);
                Assert.That(AssetDatabase.RenameAsset(path, "Timber"), Is.Empty);
                AssetDatabase.CreateFolder(folder, "Moved");
                var movedPath = folder + "/Moved/Timber.asset";
                Assert.That(AssetDatabase.MoveAsset(folder + "/Timber.asset", movedPath), Is.Empty);
                AssetDatabase.ImportAsset(movedPath, ImportAssetOptions.ForceUpdate);
                var moved = AssetDatabase.LoadAssetAtPath<ResourceDefinition>(movedPath);
                Assert.That(moved.Id, Is.EqualTo(original));
                Assert.That(AssetDatabase.CopyAsset(movedPath, folder + "/Copy.asset"), Is.True);
                AssetDatabase.Refresh();
                Assert.That(ResourceIdValidator.ValidateAll().Any(error => error.Contains(original)), Is.True);
                var copy = AssetDatabase.LoadAssetAtPath<ResourceDefinition>(folder + "/Copy.asset");
                var serialized = new SerializedObject(copy);
                serialized.FindProperty("id").stringValue = Guid.NewGuid().ToString("N");
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                Assert.That(ResourceIdValidator.ValidateAll().Any(error => error.Contains(original)), Is.False);
                Assert.That(moved.Id, Is.EqualTo(original));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
                ResourceIdValidator.Invalidate();
            }
        }
    }
}
