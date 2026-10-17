using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Immersive.Composition
{
    public enum CompositionResultStatus
    {
        Committed = 0,
        NoChange = 1,
        Rejected = 2,
        Stale = 3,
        Cancelled = 4,
        Faulted = 5,
        Degraded = 6
    }

    public enum CompositionIssueCode
    {
        None = 0,
        ComposerClosed = 1,
        OwnerClosed = 2,
        OwnerAlreadyExists = 3,
        OwnerDoesNotContributeKey = 4,
        EqualPriorityConflict = 5,
        MissingDependency = 6,
        DependencyCycle = 7,
        PreparationFailed = 8,
        ApplyFailed = 9,
        DetachFailed = 10,
        ReleasePending = 11,
        ReleaseFailed = 12,
        StaleOperation = 13,
        Cancelled = 14,
        ReentrantMutation = 15,
        Busy = 16,
        NotificationFailed = 17,
        DuplicateContentKey = 18,
        EmptyContributionUpdate = 19,
        CancellationCallbackFailed = 20
    }

    public sealed class CompositionIssue
    {
        public CompositionIssueCode Code { get; }
        public ContentKey Key { get; }
        public string Message { get; }

        internal CompositionIssue(CompositionIssueCode code, ContentKey key, string message)
        {
            Code = code;
            Key = key;
            Message = message ?? string.Empty;
        }

        public override string ToString()
        {
            return Key.IsValid ? Code + " [" + Key.Value + "]: " + Message : Code + ": " + Message;
        }
    }

    public sealed class CompositionResult
    {
        private readonly ReadOnlyCollection<ContentKey> _changedKeys;
        private readonly ReadOnlyCollection<CompositionIssue> _issues;

        public CompositionResultStatus Status { get; }
        public CompositionContextId ContextId { get; }
        public CompositionOperationId OperationId { get; }
        public long Revision { get; }
        public bool EffectsMayHaveApplied { get; }
        public IReadOnlyList<ContentKey> ChangedKeys => _changedKeys;
        public IReadOnlyList<CompositionIssue> Issues => _issues;

        internal CompositionResult(
            CompositionResultStatus status,
            CompositionContextId contextId,
            CompositionOperationId operationId,
            long revision,
            bool effectsMayHaveApplied,
            IEnumerable<ContentKey> changedKeys,
            IEnumerable<CompositionIssue> issues)
        {
            Status = status;
            ContextId = contextId;
            OperationId = operationId;
            Revision = revision;
            EffectsMayHaveApplied = effectsMayHaveApplied;
            _changedKeys = Array.AsReadOnly(changedKeys == null
                ? Array.Empty<ContentKey>()
                : new List<ContentKey>(changedKeys).ToArray());
            _issues = Array.AsReadOnly(issues == null
                ? Array.Empty<CompositionIssue>()
                : new List<CompositionIssue>(issues).ToArray());
        }
    }

    public sealed class CompositionDelta
    {
        private readonly ReadOnlyCollection<ContentKey> _changedKeys;

        public CompositionContextId ContextId { get; }
        public CompositionOperationId OperationId { get; }
        public long Revision { get; }
        public IReadOnlyList<ContentKey> ChangedKeys => _changedKeys;

        internal CompositionDelta(
            CompositionContextId contextId,
            CompositionOperationId operationId,
            long revision,
            IEnumerable<ContentKey> changedKeys)
        {
            ContextId = contextId;
            OperationId = operationId;
            Revision = revision;
            _changedKeys = Array.AsReadOnly(new List<ContentKey>(changedKeys).ToArray());
        }
    }
}
