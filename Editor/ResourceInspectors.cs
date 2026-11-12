using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace EasyResources.Editor
{
    [CustomPropertyDrawer(typeof(ResourceAmount))]
    public sealed class ResourceAmountDrawer : PropertyDrawer
    {
        private string error;
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => EditorGUIUtility.singleLineHeight + (error == null ? 0 : 36);
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var resource = property.FindPropertyRelative("resource");
            var amount = property.FindPropertyRelative("amount");
            var width = Math.Min(145f, position.width * .44f);
            var resourceRect = new Rect(position.x, position.y, position.width - width - 8, EditorGUIUtility.singleLineHeight);
            var amountRect = new Rect(resourceRect.xMax + 8, position.y, width, resourceRect.height);
            EditorGUI.PropertyField(resourceRect, resource, GUIContent.none);
            var definition = resource.objectReferenceValue as ResourceDefinition;
            var previous = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 48;
            EditorGUI.showMixedValue = amount.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            if (definition == null || definition.DecimalPlaces == 0)
            {
                var next = EditorGUI.LongField(amountRect, new GUIContent("Amount", "Exact whole units; supports the full Int64 range."), amount.longValue);
                if (EditorGUI.EndChangeCheck())
                {
                    amount.longValue = next;
                    error = null;
                }
            }
            else
            {
                var display = definition.FromUnits(amount.longValue).ToString(CultureInfo.CurrentCulture);
                var next = EditorGUI.DelayedTextField(amountRect, new GUIContent("Amount", $"Quantity in whole {definition.DisplayName} units, up to {definition.DecimalPlaces} decimal places. Stored as exact integer smallest units."), display);
                if (EditorGUI.EndChangeCheck())
                {
                    try
                    {
                        if (!decimal.TryParse(next, NumberStyles.Number, CultureInfo.CurrentCulture, out var quantity))
                            throw new ArgumentException("Enter a decimal quantity using your system decimal separator.");
                        amount.longValue = definition.ToUnits(quantity);
                        error = null;
                    }
                    catch (Exception exception)when (exception is ArgumentException || exception is OverflowException)
                    {
                        error = exception.Message;
                    }
                }
            }

            EditorGUI.showMixedValue = false;
            EditorGUIUtility.labelWidth = previous;
            if (error != null)
                EditorGUI.HelpBox(new Rect(position.x, position.y + EditorGUIUtility.singleLineHeight + 2, position.width, 34), error, MessageType.Error);
            EditorGUI.EndProperty();
        }
    }

    [CustomPropertyDrawer(typeof(ResourceLimit))]
    public sealed class ResourceLimitDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var resource = property.FindPropertyRelative("resource");
            var maximum = property.FindPropertyRelative("maximum");
            var width = Math.Min(180f, position.width * .48f);
            var left = new Rect(position.x, position.y, position.width - width - 8, position.height);
            EditorGUI.PropertyField(left, resource, GUIContent.none);
            var prior = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 80;
            maximum.longValue = EditorGUI.LongField(new Rect(left.xMax + 8, position.y, width, position.height), new GUIContent("Max units", "Maximum in integer smallest units. 0 accepts none; omitted resources are unlimited."), maximum.longValue);
            EditorGUIUtility.labelWidth = prior;
            EditorGUI.EndProperty();
        }
    }

    [CustomPropertyDrawer(typeof(ResourceCost))]
    public sealed class ResourceCostDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => EditorGUI.GetPropertyHeight(property.FindPropertyRelative("items"), label, true);
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) => EditorGUI.PropertyField(position, property.FindPropertyRelative("items"), label, true);
    }

    internal static class ResourceInspectorValidation
    {
        internal static void Draw(SerializedProperty cost)
        {
            var items = cost.FindPropertyRelative("items");
            var amounts = new ResourceAmount[items.arraySize];
            try
            {
                for (var i = 0; i < amounts.Length; i++)
                {
                    var row = items.GetArrayElementAtIndex(i);
                    amounts[i] = new ResourceAmount((ResourceDefinition)row.FindPropertyRelative("resource").objectReferenceValue, row.FindPropertyRelative("amount").longValue);
                }

                new ResourceCost(amounts).Prepare();
            }
            catch (Exception exception)
            {
                EditorGUILayout.HelpBox(exception.Message, MessageType.Error);
            }
        }

        internal static void Identity(SerializedObject serializedObject, DefinitionAsset definition, ref bool expanded)
        {
            var conflict = ResourceIdValidator.FindConflict(definition);
            if (conflict != null)
            {
                EditorGUILayout.HelpBox($"This asset shares a stable ID with {conflict}. Select the NEW duplicate and give it a new ID. Preserve the original ID for saved games.", MessageType.Error);
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                    if (GUILayout.Button("Give This Copy a New ID"))
                    {
                        serializedObject.FindProperty("id").stringValue = Guid.NewGuid().ToString("N");
                        ResourceIdValidator.Invalidate();
                    }
            }

            expanded = EditorGUILayout.Foldout(expanded, "Saved Identity", true);
            if (expanded)
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("id"), new GUIContent("Stable ID"));
                EditorGUILayout.HelpBox("Keep this ID and the .meta file in version control. Rename and move the asset freely. Migrate saved IDs explicitly when replacing definitions.", MessageType.Info);
            }
        }
    }

    [CustomEditor(typeof(ResourceContainer))]
    public sealed class ResourceContainerEditor : UnityEditor.Editor
    {
        private bool showEvents, showStorage, showDiagnostics;
        private readonly List<ResourceBalance> balances = new List<ResourceBalance>();
        private readonly List<ResourceReservationInfo> holds = new List<ResourceReservationInfo>();
        private readonly List<ResourceChange> history = new List<ResourceChange>();
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var contents = serializedObject.FindProperty("initialContents");
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                EditorGUILayout.PropertyField(contents, new GUIContent("Initial Contents"), true);
                ResourceInspectorValidation.Draw(contents);
                showStorage = EditorGUILayout.Foldout(showStorage, "Storage Limits (optional)", true);
                if (showStorage)
                {
                    var storage = serializedObject.FindProperty("storage");
                    EditorGUILayout.PropertyField(storage.FindPropertyRelative("resourceLimits"), true);
                    ToggleField(storage, "restrictResources", "acceptedResources");
                    ToggleField(storage, "limitWeight", "maximumWeight");
                    ToggleField(storage, "limitVolume", "maximumVolume");
                    EditorGUILayout.HelpBox("Limits include reserved resources. Weight and volume use the same units as the Resource assets. Leave limits disabled for a simple wallet.", MessageType.Info);
                }

                showDiagnostics = EditorGUILayout.Foldout(showDiagnostics, "Diagnostics (optional)", true);
                if (showDiagnostics)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("historyCapacity"), new GUIContent("History Capacity", "0 disables history; otherwise retain this many resource changes in a bounded ring."));
            }

            showEvents = EditorGUILayout.Foldout(showEvents, "Events", true);
            if (showEvents)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("onChanged"));
            serializedObject.ApplyModifiedProperties();
            if (!Application.isPlaying)
                return;
            try
            {
                var store = ((ResourceContainer)target).Store;
                store.CopyBalancesTo(balances);
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Runtime: Available / Reserved / Total", EditorStyles.boldLabel);
                foreach (var row in balances)
                    EditorGUILayout.LabelField(row.Resource.DisplayName, $"{row.Resource.Format(row.Available)} / {row.Resource.Format(row.Reserved)} / {row.Resource.Format(row.Total)}");
                EditorGUILayout.LabelField("Revision", store.Revision.ToString());
                EditorGUILayout.LabelField("Active Reservations", store.ActiveReservationCount.ToString());
                if (store.Limits.MaximumWeight.HasValue)
                    EditorGUILayout.LabelField("Weight", $"{store.UsedWeight} / {store.Limits.MaximumWeight}");
                if (store.Limits.MaximumVolume.HasValue)
                    EditorGUILayout.LabelField("Volume", $"{store.UsedVolume} / {store.Limits.MaximumVolume}");
                if (showDiagnostics)
                {
                    store.CopyReservationsTo(holds);
                    foreach (var hold in holds)
                        EditorGUILayout.LabelField($"Hold {hold.Reservation.Id}", string.IsNullOrEmpty(hold.OwnerId) ? "Anonymous" : hold.OwnerId);
                    store.CopyHistoryTo(history);
                    for (var i = Math.Max(0, history.Count - 12); i < history.Count; i++)
                    {
                        var change = history[i];
                        EditorGUILayout.LabelField($"#{change.Revision} {change.Kind}", change.Resource == null ? change.Context.Reason : $"{change.Resource.DisplayName}: {change.TotalDelta:+0;-0;0}");
                    }
                }
            }
            catch (Exception exception)
            {
                EditorGUILayout.HelpBox(exception.Message, MessageType.Error);
            }
        }

        private static void ToggleField(SerializedProperty storage, string toggle, string value)
        {
            var enabled = storage.FindPropertyRelative(toggle);
            EditorGUILayout.PropertyField(enabled);
            if (enabled.boolValue)
                EditorGUILayout.PropertyField(storage.FindPropertyRelative(value), true);
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;
    }

    [CustomEditor(typeof(ResourceCostAsset))]
    public sealed class ResourceCostAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                var cost = serializedObject.FindProperty("cost");
                EditorGUILayout.PropertyField(cost, true);
                ResourceInspectorValidation.Draw(cost);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }

    [CustomEditor(typeof(DefinitionAsset), true)]
    public class DefinitionAssetEditor : UnityEditor.Editor
    {
        private bool identity;
        private string validationError;
        private bool validated;
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("displayName"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("icon"));
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                DrawPropertiesExcluding(serializedObject, "m_Script", "id", "displayName", "icon");
            ResourceInspectorValidation.Identity(serializedObject, (DefinitionAsset)target, ref identity);
            if (serializedObject.ApplyModifiedProperties() || !validated)
            {
                validated = true;
                try
                {
                    ((DefinitionAsset)target).ValidateDefinition();
                    validationError = null;
                }
                catch (Exception exception)
                {
                    validationError = exception.Message;
                }
            }

            if (validationError != null)
                EditorGUILayout.HelpBox(validationError, MessageType.Error);
        }
    }

    [CustomEditor(typeof(ResourceDefinition))]
    public sealed class ResourceDefinitionEditor : UnityEditor.Editor
    {
        private bool units, physical, identity;
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("displayName"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("icon"));
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                units = EditorGUILayout.Foldout(units, "Units (optional)", true);
                if (units)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("decimalPlaces"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("unit"));
                    EditorGUILayout.HelpBox("0 decimal places means whole items. For fuel, 3 means 1 unit = 0.001 litres. Changing precision requires migrating existing saves.", MessageType.Info);
                }

                physical = EditorGUILayout.Foldout(physical, "Weight and Volume (optional)", true);
                if (physical)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("weightPerUnit"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("volumePerUnit"));
                }
            }

            ResourceInspectorValidation.Identity(serializedObject, (ResourceDefinition)target, ref identity);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
