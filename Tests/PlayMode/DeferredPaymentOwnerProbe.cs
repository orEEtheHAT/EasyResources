using UnityEngine;

namespace EasyResources.Tests
{
    // Generic lifetime probe, not an EasyCombat adapter. Holds/scopes are assigned before execution.
    public sealed class DeferredPaymentOwnerProbe : MonoBehaviour
    {
        public ResourceReservation Payment;
        public ResourceNotificationScope Notifications;
        private void OnDisable()
        {
            try { Payment.Cancel(); }
            finally { Notifications.Dispose(); }
        }
    }
}
