using System;
using UnityEngine;

namespace EasyResources
{
    /// <summary>Shared authoring identity. Mutable game state belongs to instances.</summary>
    public abstract class DefinitionAsset : ScriptableObject
    {
        [SerializeField, HideInInspector]
        private string id;
        [SerializeField]
        private string displayName = "";
        [SerializeField]
        private Sprite icon;
        public string Id => id;
        public string DisplayName => string.IsNullOrEmpty(displayName) ? name : displayName;
        public Sprite Icon => icon;

        /// <summary>Validate authored data without creating game state. Inspectors show failures before Play.</summary>
        public virtual void ValidateDefinition()
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Definition ID is blank.");
        }

        protected virtual void OnEnable() => EnsureId();
        protected virtual void OnValidate() => EnsureId();
        private void EnsureId()
        {
            if (string.IsNullOrWhiteSpace(id))
                id = Guid.NewGuid().ToString("N");
        }

        protected void Initialize(string label, string stableId = null, Sprite image = null)
        {
            if (stableId != null && string.IsNullOrWhiteSpace(stableId))
                throw new ArgumentException("ID cannot be blank.", nameof(stableId));
            displayName = label ?? "";
            name = displayName;
            icon = image;
            if (stableId != null)
                id = stableId;
            EnsureId();
        }

        protected static T New<T>(string label, string stableId = null, Sprite image = null)
            where T : DefinitionAsset
        {
            if (stableId != null && string.IsNullOrWhiteSpace(stableId))
                throw new ArgumentException("ID cannot be blank.", nameof(stableId));
            var result = CreateInstance<T>();
            result.Initialize(label ?? typeof(T).Name, stableId, image);
            return result;
        }
    }
}
