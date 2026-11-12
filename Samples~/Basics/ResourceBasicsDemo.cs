using System;
using System.Text;
using UnityEngine;

namespace EasyResources.Samples
{
    [AddComponentMenu("EasyResources/Samples/Resource Basics Demo")]
    public sealed class ResourceBasicsDemo : MonoBehaviour
    {
        [SerializeField]
        private ResourceContainer containerA;
        [SerializeField]
        private ResourceContainer containerB;
        [SerializeField]
        private ResourceDefinition wood;
        [SerializeField]
        private ResourceDefinition stone;
        [SerializeField]
        private ResourceCostAsset buildingCost;
        private ResourceContainerState initialA;
        private ResourceContainerState initialB;
        private string report = "Press Play to run the checks.";
        private string balanceA = "";
        private string balanceB = "";
        private GUIStyle titleStyle;
        private GUIStyle textStyle;
        public bool Passed { get; private set; }
        public string Report => report;

        private void Start() => RunChecks();
        private void OnEnable()
        {
            if (!Application.isPlaying)
                return;
            if (containerA != null)
                containerA.Changed += RefreshBalances;
            if (containerB != null)
                containerB.Changed += RefreshBalances;
            RefreshBalances();
        }

        private void OnDisable()
        {
            if (containerA != null)
                containerA.Changed -= RefreshBalances;
            if (containerB != null)
                containerB.Changed -= RefreshBalances;
        }

        private void RefreshBalances()
        {
            if (wood == null || stone == null || containerA == null || containerB == null)
                return;
            balanceA = BalanceLine("A", containerA);
            balanceB = BalanceLine("B", containerB);
        }

        [ContextMenu("Run Checks")]
        public void RunChecks()
        {
            if (!Application.isPlaying)
            {
                Debug.Log("Enter Play Mode first.", this);
                return;
            }

            try
            {
                if (containerA == null || containerB == null || wood == null || stone == null || buildingCost == null)
                    throw new InvalidOperationException("Assign both containers, Wood, Stone and Building Cost. Or create the ready scene via Tools > EasyResources > Create Basics Scene.");
                initialA = initialA ?? containerA.CaptureState();
                initialB = initialB ?? containerB.CaptureState();
                var definitions = new[]
                {
                    wood,
                    stone
                };
                containerA.RestoreState(initialA, definitions);
                containerB.RestoreState(initialB, definitions);
                var output = new StringBuilder();
                void Check(bool condition, string message)
                {
                    if (!condition)
                        throw new InvalidOperationException(message);
                    output.AppendLine("PASS  " + message);
                }

                var cost = buildingCost.Cost;
                Check(containerA.TrySpend(cost), "Pay 4 Wood + 2 Stone");
                Check(containerA.GetTotal(wood) == 6 && containerA.GetTotal(stone) == 3, "A has 6 Wood / 3 Stone");
                Check(containerB.GetTotal(wood) == 3 && containerB.GetTotal(stone) == 1, "B stays independent: 3 Wood / 1 Stone");
                var tooExpensive = new ResourceCost(new ResourceAmount(wood, 1), new ResourceAmount(stone, 99));
                Check(!containerA.TrySpend(tooExpensive) && containerA.GetTotal(wood) == 6, "Missing Stone leaves Wood untouched");
                Check(containerA.TryReserve(cost, out var first), "First consumer reserves the cost");
                Check(!containerA.TryReserve(cost, out _), "Competing reservation is refused");
                Check(containerA.GetTotal(wood) == 6 && containerA.GetReserved(wood) == 4 && containerA.GetAvailable(wood) == 2, "Total 6 / Reserved 4 / Available 2 Wood");
                var captureRefused = false;
                try
                {
                    containerA.CaptureState();
                }
                catch (InvalidOperationException)
                {
                    captureRefused = true;
                }

                Check(captureRefused, "Saving an active reservation is refused explicitly");
                Check(first.Cancel() && !first.Cancel() && containerA.GetAvailable(wood) == 6, "Cancel restores availability; second cancel is safe");
                Check(containerA.TryReserve(cost, out var second) && second.Commit(), "A new reservation commits once");
                Check(!second.Commit() && !second.Cancel() && containerA.GetTotal(wood) == 2, "Repeated confirmation cannot spend twice");
                containerA.Add(wood, 4);
                containerA.Add(stone, 2);
                var saved = containerA.CaptureState();
                Check(containerA.TrySpend(cost), "Spend after capturing a snapshot");
                containerA.RestoreState(saved, definitions);
                Check(containerA.GetTotal(wood) == 6 && containerA.GetTotal(stone) == 3, "Restore returns A to 6 Wood / 3 Stone");
                Passed = true;
                report = output.ToString();
                Debug.Log("EasyResources Basics: all 13 checks passed.\n" + report, this);
            }
            catch (Exception exception)
            {
                Passed = false;
                report = "FAIL  " + exception.Message;
                Debug.LogException(exception, this);
            }
        }

        private void OnGUI()
        {
            var old = GUI.matrix;
            var scale = Math.Min(Screen.width / 960f, Screen.height / 640f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            GUI.Box(new Rect(25, 25, 910, 590), GUIContent.none);
            if (titleStyle == null)
                titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 28,
                    fontStyle = FontStyle.Bold
                };
            if (textStyle == null)
                textStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 17,
                    wordWrap = true
                };
            GUI.Label(new Rect(48, 43, 860, 45), "EasyResources  /  Two independent containers", titleStyle);
            GUI.Label(new Rect(48, 98, 860, 32), "Cost: 4 Wood + 2 Stone     |     Total / Reserved / Available", textStyle);
            GUI.Label(new Rect(48, 142, 850, 35), balanceA, textStyle);
            GUI.Label(new Rect(48, 180, 850, 35), balanceB, textStyle);
            GUI.Label(new Rect(48, 233, 850, 330), report, textStyle);
            GUI.Label(new Rect(48, 575, 850, 27), "Checks run automatically. Inspect Container A and Container B to see their setup.", textStyle);
            GUI.matrix = old;
        }

        private string BalanceLine(string label, ResourceContainer container) => $"Container {label}     Wood: {container.GetTotal(wood)} / {container.GetReserved(wood)} / {container.GetAvailable(wood)}" + $"     Stone: {container.GetTotal(stone)} / {container.GetReserved(stone)} / {container.GetAvailable(stone)}";
    }
}
