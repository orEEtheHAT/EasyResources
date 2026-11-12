using System;
using System.Collections.Generic;
using System.Threading;

namespace EasyResources.Networking
{
    public enum ResourceCommandKind
    {
        Spend,
        Transfer,
        Action
    }

    [Serializable]
    public sealed class ResourceCommand
    {
        public string SessionId;
        public long Sequence;
        public ResourceCommandKind Kind;
        public string Source, Destination, ResourceId, ActionId;
        public long Amount;
        public long ExpectedSourceRevision, ExpectedDestinationRevision = -1;
        internal ResourceCommand Clone() => (ResourceCommand)MemberwiseClone();
        internal bool Same(ResourceCommand other) => other != null && SessionId == other.SessionId && Sequence == other.Sequence && Kind == other.Kind && Source == other.Source && Destination == other.Destination && ResourceId == other.ResourceId && ActionId == other.ActionId && Amount == other.Amount && ExpectedSourceRevision == other.ExpectedSourceRevision && ExpectedDestinationRevision == other.ExpectedDestinationRevision;
    }

    [Serializable]
    public sealed class ResourceCommandResult
    {
        public long Sequence;
        public bool Success;
        public ResourceFailureCode Failure;
        public string Detail;
        internal ResourceCommandResult Clone() => (ResourceCommandResult)MemberwiseClone();
    }

    public readonly struct ResourceCommandContext
    {
        public string ActorId { get; }
        public ResourceStore Source { get; }
        public ResourceStore Destination { get; }
        public string ResourceId { get; }
        public long Amount { get; }

        internal ResourceCommandContext(string actor, ResourceStore source, ResourceStore destination, ResourceCommand command)
        {
            ActorId = actor;
            Source = source;
            Destination = destination;
            ResourceId = command.ResourceId;
            Amount = command.Amount;
        }
    }

    /// <summary>Server-owned rule: append trusted prices/rewards to the supplied plan; return a failure to reject it.</summary>
    public delegate ResourceFailure ResourceAction(ResourceCommandContext context, ResourceTransaction plan);
    public sealed class ResourceAuthority
    {
        private sealed class Registration
        {
            internal ResourceStore Store;
            internal string Owner;
            internal bool AcceptTransfers;
        }

        private sealed class Replay
        {
            internal ResourceCommand Command;
            internal ResourceCommandResult Result;
        }

        private sealed class Session
        {
            internal string Id, Actor;
            internal long LastSequence;
            internal Replay[] Cache;
            internal int Next;
            internal void Remember(ResourceCommand command, ResourceCommandResult result)
            {
                LastSequence = command.Sequence;
                Cache[Next] = new Replay
                {
                    Command = command,
                    Result = result
                };
                Next = (Next + 1) % Cache.Length;
            }
        }

        private sealed class Completion : IResourceParticipant
        {
            internal Session Session;
            internal ResourceCommand Command;
            internal ResourceCommandResult Result;
            public void Enter()
            {
            }

            public bool Prepare(out ResourceFailure failure)
            {
                failure = default;
                return true;
            }

            public void Apply() => Session.Remember(Command, Result);
            public void Publish()
            {
            }

            public void Exit()
            {
            }

            public void Finish()
            {
            }
        }

        private readonly Dictionary<string, Registration> stores = new Dictionary<string, Registration>(StringComparer.Ordinal);
        private Dictionary<string, Session> sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private readonly Dictionary<string, ResourceAction> actions = new Dictionary<string, ResourceAction>(StringComparer.Ordinal);
        private readonly DefinitionLookup<ResourceDefinition> definitions;
        private readonly ResourceDefinition[] resources;
        private readonly int replayCapacity, maximumSessions, thread = Thread.CurrentThread.ManagedThreadId;
        private readonly ResourceTransaction transaction = new ResourceTransaction();
        private readonly Completion completion = new Completion();
        private bool processing;
        public ResourceAuthority(IEnumerable<ResourceDefinition> definitions, int replayCapacity = 64, int maximumSessions = 128)
        {
            if (replayCapacity < 1 || maximumSessions < 1)
                throw new ArgumentOutOfRangeException("Authority capacities must be positive.");
            if (definitions == null)
                throw new ArgumentNullException(nameof(definitions));
            resources = new List<ResourceDefinition>(definitions).ToArray();
            this.definitions = new DefinitionLookup<ResourceDefinition>(resources);
            this.replayCapacity = replayCapacity;
            this.maximumSessions = maximumSessions;
        }

        private void Check()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Dispatch authority commands on its creating Unity thread.");
            if (processing)
                throw new InvalidOperationException("Queue commands until the authority finishes its current operation.");
        }

        private static bool Key(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
        public void RegisterStore(string id, ResourceStore store, string ownerId, bool acceptIncomingTransfers = false)
        {
            Check();
            if (!Key(id) || !Key(ownerId) || store == null || store.IsReadOnly)
                throw new ArgumentException("Register an authoritative store, stable key and owner ID.");
            foreach (var registration in stores.Values)
                if (ReferenceEquals(registration.Store, store))
                    throw new ArgumentException("A store is already registered.");
            stores.Add(id, new Registration { Store = store, Owner = ownerId, AcceptTransfers = acceptIncomingTransfers });
        }

        public void RegisterAction(string id, ResourceAction action)
        {
            Check();
            if (!Key(id) || action == null)
                throw new ArgumentException("Supply an action ID and server-owned rule.");
            actions.Add(id, action);
        }

        /// <summary>Call only after transport authentication. Reopening an actor invalidates their earlier session.</summary>
        public string OpenSession(string authenticatedActor)
        {
            Check();
            if (!Key(authenticatedActor))
                throw new ArgumentException("Supply the authenticated actor ID.");
            if (!sessions.ContainsKey(authenticatedActor) && sessions.Count >= maximumSessions)
                throw new InvalidOperationException("Authority session capacity reached. Close disconnected sessions.");
            var session = new Session
            {
                Actor = authenticatedActor,
                Id = Guid.NewGuid().ToString("N"),
                Cache = new Replay[replayCapacity]
            };
            sessions[authenticatedActor] = session;
            return session.Id;
        }

        public bool CloseSession(string authenticatedActor)
        {
            Check();
            return sessions.Remove(authenticatedActor);
        }

        public ResourceCommandResult Execute(string authenticatedActor, ResourceCommand input)
        {
            Check();
            if (!Key(authenticatedActor) || input == null || !sessions.TryGetValue(authenticatedActor, out var session) || input.SessionId != session.Id)
                return Result(input?.Sequence ?? 0, ResourceFailureCode.Unauthorized);
            if (input.Sequence <= session.LastSequence)
            {
                foreach (var replay in session.Cache)
                    if (replay != null && replay.Command.Sequence == input.Sequence)
                        return replay.Command.Same(input) ? replay.Result.Clone() : Result(input.Sequence, ResourceFailureCode.InvalidCommand, "Sequence was already used by a different command.");
                return Result(input.Sequence, ResourceFailureCode.ExpiredCommand, "This command is older than retained replies; it will not be applied again.");
            }

            if (input.Sequence <= 0 || session.LastSequence == long.MaxValue || input.Sequence != session.LastSequence + 1)
                return Result(input.Sequence, ResourceFailureCode.InvalidCommand, "Send commands in sequence without gaps.");
            var command = input.Clone();
            processing = true;
            try
            {
                ResourceCommandResult Reject(ResourceFailureCode code, string detail = null)
                {
                    var result = Result(command.Sequence, code, detail);
                    session.Remember(command, result);
                    return result.Clone();
                }

                if (!Enum.IsDefined(typeof(ResourceCommandKind), command.Kind) || !Key(command.Source) || command.Amount <= 0 || command.ExpectedSourceRevision < 0 || command.ExpectedDestinationRevision < -1 || command.ResourceId != null && !Key(command.ResourceId) || command.ActionId != null && !Key(command.ActionId) || command.Destination != null && !Key(command.Destination))
                    return Reject(ResourceFailureCode.InvalidCommand);
                if (!stores.TryGetValue(command.Source, out var source) || source.Owner != authenticatedActor)
                    return Reject(ResourceFailureCode.Unauthorized);
                Registration destination = null;
                if (command.Destination != null && (!stores.TryGetValue(command.Destination, out destination) || destination.Owner != authenticatedActor && !destination.AcceptTransfers))
                    return Reject(ResourceFailureCode.Unauthorized);
                if (command.ExpectedSourceRevision != source.Store.Revision || destination != null && command.ExpectedDestinationRevision != destination.Store.Revision)
                    return Reject(ResourceFailureCode.StaleRevision);
                transaction.Clear();
                if (command.Kind == ResourceCommandKind.Action)
                {
                    if (command.ActionId == null || !actions.TryGetValue(command.ActionId, out var action))
                        return Reject(ResourceFailureCode.InvalidCommand);
                    var denied = action(new ResourceCommandContext(authenticatedActor, source.Store, destination?.Store, command), transaction);
                    if (denied.HasFailure)
                        return Reject(denied.Code, denied.Detail);
                }
                else
                {
                    if (command.ResourceId == null || !definitions.TryGet(command.ResourceId, out var resource))
                        return Reject(ResourceFailureCode.InvalidCommand);
                    if (command.Kind == ResourceCommandKind.Transfer && destination == null)
                        return Reject(ResourceFailureCode.InvalidCommand);
                    transaction.Spend(source.Store, resource, command.Amount);
                    if (command.Kind == ResourceCommandKind.Transfer)
                        transaction.Add(destination.Store, resource, command.Amount);
                }

                var success = Result(command.Sequence, ResourceFailureCode.None);
                completion.Session = session;
                completion.Command = command;
                completion.Result = success;
                transaction.Join(completion);
                if (!transaction.TryExecute(out var failure, new ResourceOperationContext("network-command", authenticatedActor)))
                    return Reject(failure.Code, failure.Detail);
                return success.Clone();
            }
            catch (OverflowException)
            {
                var rejected = Result(command.Sequence, ResourceFailureCode.AmountOverflow);
                session.Remember(command, rejected);
                return rejected.Clone();
            }
            catch (ArgumentException e)
            {
                var rejected = Result(command.Sequence, ResourceFailureCode.InvalidCommand, e.Message);
                session.Remember(command, rejected);
                return rejected.Clone();
            }
            finally
            {
                processing = false;
            }
        }

        private static ResourceCommandResult Result(long sequence, ResourceFailureCode failure, string detail = null) => new ResourceCommandResult
        {
            Sequence = sequence,
            Success = failure == ResourceFailureCode.None,
            Failure = failure,
            Detail = detail
        };
        /// <summary>Persist this single snapshot atomically. It contains balances and replay watermarks from the same world state.</summary>
        public ResourceAuthorityState CaptureState(long nowMilliseconds = 0)
        {
            if (Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Capture on the authority thread.");
            var state = new ResourceAuthorityState();
            foreach (var pair in stores)
                state.Stores.Add(new AuthorityStoreState { StoreId = pair.Key, State = pair.Value.Store.CaptureState(true, nowMilliseconds) });
            foreach (var session in sessions.Values)
                state.Sessions.Add(new AuthoritySessionState { ActorId = session.Actor, SessionId = session.Id, LastSequence = session.LastSequence });
            return state;
        }

        public void RestoreState(ResourceAuthorityState state, long nowMilliseconds = 0)
        {
            Check();
            if (state == null || state.Version != 1 || state.Stores == null || state.Sessions == null || state.Stores.Count != stores.Count || state.Sessions.Count > maximumSessions)
                throw new ArgumentException("Invalid authority snapshot.");
            var nextSessions = new Dictionary<string, Session>(StringComparer.Ordinal);
            var sessionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var saved in state.Sessions)
            {
                if (saved == null || !Key(saved.ActorId) || !Key(saved.SessionId) || saved.LastSequence < 0 || !sessionIds.Add(saved.SessionId))
                    throw new ArgumentException("Invalid authority session.");
                nextSessions.Add(saved.ActorId, new Session { Actor = saved.ActorId, Id = saved.SessionId, LastSequence = saved.LastSequence, Cache = new Replay[replayCapacity] });
            }

            var targets = new ResourceStore[state.Stores.Count];
            var prepared = new ResourceStore.PreparedState[targets.Length];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < targets.Length; i++)
            {
                var saved = state.Stores[i];
                if (saved == null || saved.StoreId == null || !seen.Add(saved.StoreId) || !stores.TryGetValue(saved.StoreId, out var registration))
                    throw new ArgumentException("Unknown or duplicate saved store.");
                targets[i] = registration.Store;
                prepared[i] = targets[i].PrepareState(saved.State, definitions, new ResourceRestoreOptions(resumeReservations: true, replaceActiveReservations: true, nowMilliseconds: nowMilliseconds));
            }

            processing = true;
            foreach (var store in targets)
                store.EnterTransaction();
            try
            {
                sessions = nextSessions;
                for (var i = 0; i < targets.Length; i++)
                    targets[i].ApplyState(prepared[i]);
                foreach (var store in targets)
                    store.Publish();
            }
            finally
            {
                foreach (var store in targets)
                    store.ExitTransaction();
                foreach (var store in targets)
                    store.FinishDeferredDispose();
                processing = false;
            }
        }
    }

    [Serializable]
    public sealed class ResourceAuthorityState
    {
        public int Version = 1;
        public List<AuthorityStoreState> Stores = new List<AuthorityStoreState>();
        public List<AuthoritySessionState> Sessions = new List<AuthoritySessionState>();
    }

    [Serializable]
    public sealed class AuthorityStoreState
    {
        public string StoreId;
        public ResourceContainerState State;
    }

    [Serializable]
    public sealed class AuthoritySessionState
    {
        public string ActorId, SessionId;
        public long LastSequence;
    }
}
