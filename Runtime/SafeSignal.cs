using System;
using UnityEngine;

namespace EasyResources
{
    internal sealed class SafeSignal
    {
        private Action handlers;
        private Delegate[] snapshot = Array.Empty<Delegate>();
        internal void Add(Action action)
        {
            handlers += action;
            snapshot = handlers?.GetInvocationList() ?? Array.Empty<Delegate>();
        }

        internal void Remove(Action action)
        {
            handlers -= action;
            snapshot = handlers?.GetInvocationList() ?? Array.Empty<Delegate>();
        }

        internal void Clear()
        {
            handlers = null;
            snapshot = Array.Empty<Delegate>();
        }

        internal void Emit()
        {
            var current = snapshot;
            foreach (var action in current)
                try
                {
                    ((Action)action)();
                }
                catch (Exception e)
                {
                    Log(e);
                }
        }

        internal static void Log(Exception exception)
        {
            try
            {
                Debug.LogException(exception);
            }
            catch
            { /* A logging listener must not invalidate a committed transaction. */
            }
        }
    }
}
