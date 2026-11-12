using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace EasyResources.Progression
{
    [DisallowMultipleComponent, AddComponentMenu("EasyResources/Progression")]
    public sealed class ProgressionComponent : MonoBehaviour
    {
        [SerializeField]
        private ProgressionDefinition definition;
        [SerializeField]
        private ResourceContainer wallet;
        [SerializeField]
        private UnityEvent onChanged = new UnityEvent();
        private ProgressionProfile profile;
        private bool restoring;
        public ProgressionProfile Profile
        {
            get
            {
                if (!Application.isPlaying)
                    throw new InvalidOperationException("Progression runtime state exists in Play Mode.");
                if (profile != null)
                    return profile;
                if (wallet == null)
                    wallet = GetComponent<ResourceContainer>();
                if (wallet == null || definition == null)
                    throw new InvalidOperationException("Assign a Progression Definition and a Resource Container wallet.");
                profile = new ProgressionProfile(definition, wallet.Store);
                profile.Changed += Notify;
                return profile;
            }
        }

        private void Awake()
        {
            _ = Profile;
        }

        public ProgressionState CaptureState() => Profile.CaptureState((long)(Time.unscaledTimeAsDouble * 1000));
        /// <summary>Restore the resource wallet first, then reconnect to its named research reservation.</summary>
        public void RestoreState(ProgressionState state, IEnumerable<ResourceDefinition> resources, long offlineMilliseconds = 0)
        {
            if (restoring)
                throw new InvalidOperationException("Progression is already being restored.");
            var previous = Profile;
            previous.ValidateReplacement();
            restoring = true;
            try
            {
                var replacement = ProgressionProfile.FromState(definition, wallet.Store, state, resources, (long)(Time.unscaledTimeAsDouble * 1000), offlineMilliseconds);
                previous.Changed -= Notify;
                profile = replacement;
                profile.Changed += Notify;
                previous.Dispose();
                try
                {
                    Notify();
                }
                catch (Exception exception)
                {
                    SafeSignal.Log(exception);
                }
            }
            finally
            {
                restoring = false;
            }
        }

        private void Update()
        {
            if (profile?.Researching != null)
                profile.TryCompleteResearch((long)(Time.unscaledTimeAsDouble * 1000), out _);
        }

        private void Notify() => onChanged.Invoke();
        private void OnDestroy()
        {
            profile?.RequestDispose();
            profile = null;
        }
    }
}
