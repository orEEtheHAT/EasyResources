using UnityEngine;

namespace EasyResources
{
    /// <summary>Optional reusable asset for a cost shared by several consumers.</summary>
    [CreateAssetMenu(menuName = "EasyResources/Resource Cost", fileName = "New Resource Cost")]
    public sealed class ResourceCostAsset : ScriptableObject
    {
        [SerializeField]
        private ResourceCost cost = new ResourceCost();
        public ResourceCost Cost => cost;

        private void OnValidate() => cost?.OnAfterDeserialize();
    }
}
