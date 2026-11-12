using System.Collections;
using EasyResources.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace EasyResources.Tests
{
    public sealed class ResourcePanelTests
    {
        [UnityTest]
        public IEnumerator PanelCreatesItsDocumentUpdatesByEventAndDoesNotDuplicateOnEnable()
        {
            var gold = ResourceDefinition.Create("Gold");
            var go = new GameObject("Resource UI");
            using var store = new ResourceStore();
            var panel = go.AddComponent<ResourcePanel>();
            panel.Bind(store);
            store.Add(gold, 10);
            yield return null;
            var document = go.GetComponent<UIDocument>();
            Assert.That(document.panelSettings, Is.Not.Null);
            Assert.That(document.rootVisualElement.Q("easy-resources"), Is.Not.Null);
            var text = string.Empty;
            document.rootVisualElement.Query<Label>().ForEach(label => text += label.text + "|");
            Assert.That(text, Does.Contain("10"));
            panel.enabled = false;
            store.Add(gold, 1);
            panel.enabled = true;
            yield return null;
            Assert.That(document.rootVisualElement.Query<VisualElement>("easy-resources").ToList().Count, Is.EqualTo(1));
            text = string.Empty;
            document.rootVisualElement.Query<Label>().ForEach(label => text += label.text + "|");
            Assert.That(text, Does.Contain("11"));
            var silver = ResourceDefinition.Create("Silver");
            using var replacement = new ResourceStore();
            replacement.Add(silver, 7);
            panel.Bind(replacement);
            yield return null;
            text = string.Empty;
            document.rootVisualElement.Query<Label>().ForEach(label => text += label.text + "|");
            Assert.That(text, Does.Contain("Silver").And.Not.Contain("Gold"));
            UnityEngine.Object.Destroy(go);
            UnityEngine.Object.Destroy(gold);
            UnityEngine.Object.Destroy(silver);
            yield return null;
            LogAssert.NoUnexpectedReceived();
        }
    }
}
