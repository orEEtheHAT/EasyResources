using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace EasyResources.UI
{
    /// <summary>Event-driven UI Toolkit balances, held amounts, cost preview and capacity. No polling or input dependency.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(UIDocument)), AddComponentMenu("EasyResources/Resource Panel")]
    public sealed class ResourcePanel : MonoBehaviour
    {
        private sealed class Row
        {
            internal ResourceDefinition Resource;
            internal VisualElement Root;
            internal Label Amount, Cost;
        }

        [SerializeField]
        private ResourceContainer source;
        [Tooltip("Empty: show every known balance and every resource in the preview cost.")]
        [SerializeField]
        private ResourceDefinition[] resources = Array.Empty<ResourceDefinition>();
        [SerializeField]
        private ResourceCostAsset previewCost;
        [SerializeField]
        private bool showReserved = true;
        [SerializeField]
        private string title = "Resources";
        private readonly List<ResourceBalance> balances = new List<ResourceBalance>();
        private readonly List<Row> rows = new List<Row>();
        private ResourceStore bound;
        private ResourceCost cost;
        private UIDocument document;
        private VisualElement panel, body;
        private Label status, capacity;
        private PanelSettings ownedSettings;
        private bool subscribed, dirty;
        public void Bind(ResourceStore store)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            Unsubscribe();
            bound = store;
            body?.Clear();
            rows.Clear();
            if (isActiveAndEnabled)
            {
                EnsureView();
                Subscribe();
                Refresh();
            }
        }

        public void PreviewCost(ResourceCost value)
        {
            cost = value;
            if (isActiveAndEnabled && panel != null)
                Refresh();
        }

        private void OnEnable()
        {
            EnsureView();
            if (cost == null && previewCost != null)
                cost = previewCost.Cost;
            if (bound == null)
            {
                if (source == null)
                    source = GetComponent<ResourceContainer>();
                if (source != null)
                    bound = source.Store;
            }

            if (bound != null)
            {
                Subscribe();
                Refresh();
            }
            else
                status.text = "Assign a Resource Container or call Bind(store).";
        }

        private void OnDisable()
        {
            Unsubscribe();
            panel?.RemoveFromHierarchy();
            panel = null;
            rows.Clear();
        }

        private void Subscribe()
        {
            if (subscribed)
                return;
            bound.Changed += MarkDirty;
            subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!subscribed)
                return;
            bound.Changed -= MarkDirty;
            subscribed = false;
        }

        private void EnsureView()
        {
            if (panel != null)
                return;
            document = GetComponent<UIDocument>();
            if (document.panelSettings == null)
            {
                ownedSettings = ScriptableObject.CreateInstance<PanelSettings>();
                ownedSettings.name = "EasyResources Runtime Panel";
                ownedSettings.themeStyleSheet = UnityEngine.Resources.Load<ThemeStyleSheet>("EasyResources/DefaultTheme");
                if (ownedSettings.themeStyleSheet == null)
                    throw new InvalidOperationException("EasyResources UI theme is missing. Reimport the package's Runtime/UI/Resources folder.");
                ownedSettings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                ownedSettings.referenceResolution = new Vector2Int(1280, 720);
                document.panelSettings = ownedSettings;
            }

            panel = new VisualElement
            {
                name = "easy-resources"
            };
            panel.AddToClassList("easy-resources");
            panel.style.position = Position.Absolute;
            panel.style.left = 20;
            panel.style.top = 20;
            panel.style.width = 380;
            panel.style.backgroundColor = new Color(.075f, .09f, .13f, .96f);
            panel.style.color = new Color(.91f, .94f, 1);
            panel.style.paddingLeft = 18;
            panel.style.paddingRight = 18;
            panel.style.paddingTop = 14;
            panel.style.paddingBottom = 14;
            panel.style.unityFont = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            panel.style.fontSize = 16;
            var heading = new Label(title);
            heading.AddToClassList("easy-resources-title");
            heading.style.fontSize = 22;
            heading.style.marginBottom = 10;
            panel.Add(heading);
            body = new VisualElement();
            panel.Add(body);
            capacity = new Label();
            capacity.AddToClassList("easy-resources-capacity");
            capacity.style.marginTop = 8;
            panel.Add(capacity);
            status = new Label();
            status.AddToClassList("easy-resources-status");
            status.style.whiteSpace = WhiteSpace.Normal;
            status.style.marginTop = 8;
            panel.Add(status);
            document.rootVisualElement.Add(panel);
        }

        private void AddRow(ResourceDefinition resource)
        {
            if (resource == null)
                return;
            foreach (var row in rows)
                if (ReferenceEquals(row.Resource, resource))
                    return;
            var root = new VisualElement();
            root.AddToClassList("easy-resources-row");
            root.style.flexDirection = FlexDirection.Row;
            root.style.alignItems = Align.Center;
            root.style.minHeight = 32;
            if (resource.Icon != null)
            {
                var icon = new Image
                {
                    sprite = resource.Icon
                };
                icon.style.width = 24;
                icon.style.height = 24;
                icon.style.marginRight = 8;
                root.Add(icon);
            }

            var label = new Label(resource.DisplayName);
            label.style.flexGrow = 1;
            root.Add(label);
            var amount = new Label();
            root.Add(amount);
            var required = new Label();
            required.style.marginLeft = 12;
            root.Add(required);
            body.Add(root);
            rows.Add(new Row { Resource = resource, Root = root, Amount = amount, Cost = required });
        }

        private void Refresh()
        {
            dirty = false;
            if (bound == null || panel == null)
                return;
            try
            {
                if (resources.Length > 0)
                    foreach (var resource in resources)
                        AddRow(resource);
                else
                {
                    bound.CopyBalancesTo(balances, true);
                    foreach (var balance in balances)
                        AddRow(balance.Resource);
                    if (cost != null)
                        foreach (var item in cost.Items)
                            AddRow(item.Resource);
                }

                foreach (var row in rows)
                {
                    var balance = bound.GetBalance(row.Resource);
                    row.Amount.text = row.Resource.Format(balance.Available, true);
                    if (showReserved && balance.Reserved > 0)
                        row.Amount.text += " (" + row.Resource.Format(balance.Reserved) + " held)";
                    row.Root.tooltip = "Total: " + row.Resource.Format(balance.Total) + " | Available: " + row.Resource.Format(balance.Available);
                    var required = cost?.GetAmount(row.Resource) ?? 0;
                    row.Cost.text = required > 0 ? "/ " + row.Resource.Format(required) : string.Empty;
                    row.Cost.style.color = balance.Available >= required ? new Color(.45f, .9f, .7f) : new Color(1, .55f, .5f);
                    var maximum = bound.Limits.GetMaximum(row.Resource);
                    if (maximum != long.MaxValue)
                        row.Root.tooltip += " | Capacity: " + row.Resource.Format(maximum);
                }

                capacity.text = string.Empty;
                if (bound.Limits.MaximumWeight.HasValue)
                    capacity.text = "Weight: " + bound.UsedWeight + " / " + bound.Limits.MaximumWeight;
                if (bound.Limits.MaximumVolume.HasValue)
                    capacity.text += (capacity.text.Length > 0 ? "   " : "") + "Volume: " + bound.UsedVolume + " / " + bound.Limits.MaximumVolume;
                status.text = cost == null ? string.Empty : bound.CanAfford(cost, out var failure) ? "Cost available" : failure.ToString();
            }
            catch (ObjectDisposedException)
            {
                status.text = "Container is no longer available.";
                Unsubscribe();
            }
        }

        private void MarkDirty() => dirty = true;
        private void LateUpdate()
        {
            if (dirty)
                Refresh();
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (ownedSettings != null)
                Destroy(ownedSettings);
        }
    }
}
