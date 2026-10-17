using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Immersive.Composition
{
    public sealed class CompositionComposer
    {
        private readonly object _sync = new object();
        private readonly Dictionary<CompositionOwnerId, ContributionState> _owners =
            new Dictionary<CompositionOwnerId, ContributionState>();
        private readonly HashSet<CompositionOwnerId> _closedOwnerIds = new HashSet<CompositionOwnerId>();
        private readonly Dictionary<ContentKey, PartRecord> _parts = new Dictionary<ContentKey, PartRecord>();
        private readonly Dictionary<ContentKey, CompositionOperationId> _latestOperationByKey =
            new Dictionary<ContentKey, CompositionOperationId>();
        private readonly Dictionary<ContentKey, SemaphoreSlim> _keyGates =
            new Dictionary<ContentKey, SemaphoreSlim>();
        private readonly Dictionary<CompositionOperationId, CancellationTokenSource> _activeOperations =
            new Dictionary<CompositionOperationId, CancellationTokenSource>();
        private readonly SortedDictionary<long, CompositionDelta> _pendingDeltas =
            new SortedDictionary<long, CompositionDelta>();
        private readonly HashSet<ContentKey> _applyingKeys = new HashSet<ContentKey>();
        private readonly SemaphoreSlim _commitGate = new SemaphoreSlim(1, 1);
        private readonly CompositionContextId _contextId = new CompositionContextId(Guid.NewGuid());

        private long _revision;
        private bool _closed;
        private bool _publishingDeltas;
        private int _applicationThreadId;

        public event Action<CompositionDelta> Committed;

        public CompositionContextId ContextId => _contextId;

        public long Revision
        {
            get
            {
                lock (_sync)
                {
                    return _revision;
                }
            }
        }

        public CompositionContribution CreateContribution(CompositionOwnerId ownerId, int priority)
        {
            if (!ownerId.IsValid)
            {
                throw new ArgumentException("A valid owner ID is required.", nameof(ownerId));
            }

            lock (_sync)
            {
                if (_closed)
                {
                    throw new InvalidOperationException("The composition context is closed.");
                }

                if (_closedOwnerIds.Contains(ownerId))
                {
                    throw new InvalidOperationException("A closed owner ID cannot be reused in this composition context.");
                }

                if (_owners.ContainsKey(ownerId))
                {
                    throw new InvalidOperationException("A composition owner ID can be opened only once per context.");
                }

                var contribution = new CompositionContribution(this, ownerId, priority);
                _owners.Add(ownerId, new ContributionState(contribution));
                return contribution;
            }
        }

        public Task<CompositionResult> CloseAsync(CancellationToken cancellationToken = default)
        {
            return CloseContextAsync(cancellationToken);
        }

        internal Task<CompositionResult> SetPartsAsync(
            CompositionContribution contribution,
            IEnumerable<PartDefinition> definitions,
            CancellationToken cancellationToken)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions));
            }

            var definitionList = new List<PartDefinition>();
            var seen = new HashSet<ContentKey>();
            foreach (PartDefinition definition in definitions)
            {
                if (definition == null)
                {
                    throw new ArgumentException("Part definitions cannot contain null entries.", nameof(definitions));
                }

                if (!seen.Add(definition.Key))
                {
                    return Task.FromResult(Immediate(
                        CompositionResultStatus.Rejected,
                        CompositionIssueCode.DuplicateContentKey,
                        definition.Key,
                        "A contribution cannot define the same content key more than once."));
                }

                definitionList.Add(definition);
            }

            if (definitionList.Count == 0)
            {
                return Task.FromResult(Immediate(
                    CompositionResultStatus.Rejected,
                    CompositionIssueCode.EmptyContributionUpdate,
                    default,
                    "At least one part definition is required."));
            }

            return ChangeContributionAsync(contribution, definitionList, null, false, cancellationToken);
        }

        internal Task<CompositionResult> RemovePartAsync(
            CompositionContribution contribution,
            ContentKey key,
            CancellationToken cancellationToken)
        {
            if (!key.IsValid)
            {
                throw new ArgumentException("A valid content key is required.", nameof(key));
            }

            return ChangeContributionAsync(contribution, null, new[] { key }, false, cancellationToken);
        }

        internal Task<CompositionResult> CloseContributionAsync(
            CompositionContribution contribution,
            CancellationToken cancellationToken)
        {
            return ChangeContributionAsync(contribution, null, null, true, cancellationToken);
        }

        private async Task<CompositionResult> CloseContextAsync(CancellationToken cancellationToken)
        {
            Operation operation;
            List<CancellationTokenSource> cancellations;
            lock (_sync)
            {
                if (_closed)
                {
                    return Result(CompositionResultStatus.NoChange, default, _revision, false, null, null);
                }

                if (_applicationThreadId == Thread.CurrentThread.ManagedThreadId)
                {
                    return Immediate(
                        CompositionResultStatus.Rejected,
                        CompositionIssueCode.ReentrantMutation,
                        default,
                        "A composition context cannot be synchronously closed from a materializer callback.");
                }

                Dictionary<ContentKey, Winner> oldWinners = ResolveWinners(_owners, out List<CompositionIssue> oldWinnerIssues);
                var oldDefinitions = DefinitionsFromWinners(oldWinners);
                Dictionary<ContentKey, PartDefinition> oldParts = DefinitionsFromParts(_parts);
                MergeDefinitions(oldDefinitions, oldParts);

                _closed = true;
                foreach (CompositionOwnerId ownerId in _owners.Keys)
                {
                    _closedOwnerIds.Add(ownerId);
                }

                _owners.Clear();
                var newWinners = new Dictionary<ContentKey, Winner>();
                var seeds = GetWinnerChanges(oldWinners, newWinners);
                foreach (ContentKey key in _parts.Keys)
                {
                    seeds.Add(key);
                }

                foreach (ContentKey key in _latestOperationByKey.Keys)
                {
                    seeds.Add(key);
                }

                HashSet<ContentKey> actionKeys = CompositionGraph.ExpandDependents(
                    seeds,
                    oldDefinitions,
                    new Dictionary<ContentKey, PartDefinition>(),
                    oldParts);
                HashSet<ContentKey> lockKeys = CompositionGraph.ExpandDependencyClosure(
                    actionKeys,
                    oldDefinitions,
                    oldParts);
                if (lockKeys.Count == 0)
                {
                    return Result(CompositionResultStatus.NoChange, default, _revision, false, null, oldWinnerIssues);
                }

                cancellations = new List<CancellationTokenSource>();
                operation = CreateOperation(
                    default,
                    actionKeys,
                    lockKeys,
                    new Dictionary<ContentKey, Winner>(),
                    Array.Empty<CompositionIssue>(),
                    true,
                    cancellationToken,
                    cancellations);
            }

            operation.CancellationIssues.AddRange(CancelOperations(cancellations));
            return await ExecuteOperationAsync(operation);
        }

        private Task<CompositionResult> ChangeContributionAsync(
            CompositionContribution contribution,
            IReadOnlyList<PartDefinition> definitionsToSet,
            IReadOnlyCollection<ContentKey> keysToRemove,
            bool closeOwner,
            CancellationToken cancellationToken)
        {
            if (contribution == null)
            {
                throw new ArgumentNullException(nameof(contribution));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(Immediate(CompositionResultStatus.Cancelled, CompositionIssueCode.Cancelled, default,
                    "The operation was cancelled before changing contribution intent."));
            }

            Operation operation;
            List<CancellationTokenSource> cancellations;
            lock (_sync)
            {
                if (_closed)
                {
                    return Task.FromResult(Immediate(CompositionResultStatus.Rejected, CompositionIssueCode.ComposerClosed, default,
                        "The composition context is closed."));
                }

                if (_applicationThreadId == Thread.CurrentThread.ManagedThreadId && !closeOwner)
                {
                    return Task.FromResult(Immediate(CompositionResultStatus.Rejected, CompositionIssueCode.ReentrantMutation, default,
                        "Contribution changes are rejected during a materializer application callback."));
                }

                if (!_owners.TryGetValue(contribution.OwnerId, out ContributionState owner) ||
                    !ReferenceEquals(owner.Handle, contribution))
                {
                    if (closeOwner && _closedOwnerIds.Contains(contribution.OwnerId))
                    {
                        return Task.FromResult(Result(CompositionResultStatus.NoChange, default, _revision, false, null, null));
                    }

                    return Task.FromResult(Immediate(CompositionResultStatus.Rejected, CompositionIssueCode.OwnerClosed, default,
                        "The contribution owner is not open in this composition context."));
                }

                if (!closeOwner && keysToRemove != null)
                {
                    for (int i = 0; i < keysToRemove.Count; i++)
                    {
                        if (!owner.Entries.ContainsKey(keysToRemove.ElementAt(i)))
                        {
                            ContentKey key = keysToRemove.ElementAt(i);
                            return Task.FromResult(Immediate(CompositionResultStatus.Rejected,
                                CompositionIssueCode.OwnerDoesNotContributeKey, key,
                                "This contribution does not own an intent for the requested key."));
                        }
                    }
                }

                Dictionary<ContentKey, Winner> oldWinners = ResolveWinners(_owners, out _);
                Dictionary<ContentKey, PartDefinition> oldDefinitions = DefinitionsFromWinners(oldWinners);
                Dictionary<ContentKey, PartDefinition> oldParts = DefinitionsFromParts(_parts);
                MergeDefinitions(oldDefinitions, oldParts);

                var rollbackEntries = new Dictionary<ContentKey, PartDefinition>();
                var absentEntries = new HashSet<ContentKey>();
                if (closeOwner)
                {
                    foreach (KeyValuePair<ContentKey, PartDefinition> pair in owner.Entries)
                    {
                        rollbackEntries.Add(pair.Key, pair.Value);
                    }
                }
                else
                {
                    if (definitionsToSet != null)
                    {
                        for (int i = 0; i < definitionsToSet.Count; i++)
                        {
                            ContentKey key = definitionsToSet[i].Key;
                            if (owner.Entries.TryGetValue(key, out PartDefinition prior))
                            {
                                rollbackEntries[key] = prior;
                            }
                            else
                            {
                                absentEntries.Add(key);
                            }

                            owner.Entries[key] = definitionsToSet[i];
                        }
                    }

                    if (keysToRemove != null)
                    {
                        foreach (ContentKey key in keysToRemove)
                        {
                            if (owner.Entries.TryGetValue(key, out PartDefinition prior))
                            {
                                rollbackEntries[key] = prior;
                                owner.Entries.Remove(key);
                            }
                        }
                    }
                }

                if (closeOwner)
                {
                    owner.IsClosed = true;
                    _owners.Remove(contribution.OwnerId);
                    _closedOwnerIds.Add(contribution.OwnerId);
                }

                Dictionary<ContentKey, Winner> newWinners = ResolveWinners(_owners, out List<CompositionIssue> winnerIssues);
                Dictionary<ContentKey, PartDefinition> newDefinitions = DefinitionsFromWinners(newWinners);
                Dictionary<ContentKey, PartDefinition> currentParts = DefinitionsFromParts(_parts);
                var seeds = GetWinnerChanges(oldWinners, newWinners);

                if (definitionsToSet != null)
                {
                    for (int i = 0; i < definitionsToSet.Count; i++)
                    {
                        ContentKey key = definitionsToSet[i].Key;
                        if (newWinners.TryGetValue(key, out Winner winner) && !PartMatches(_parts, key, winner))
                        {
                            seeds.Add(key);
                        }
                    }
                }

                HashSet<ContentKey> actionKeys = CompositionGraph.ExpandDependents(
                    seeds,
                    oldDefinitions,
                    newDefinitions,
                    currentParts);
                HashSet<ContentKey> lockKeys = CompositionGraph.ExpandDependencyClosure(
                    actionKeys,
                    oldDefinitions,
                    newDefinitions,
                    currentParts);

                if (!closeOwner)
                {
                    CompositionGraphValidation validation = CompositionGraph.Validate(newDefinitions, winnerIssues);
                    List<CompositionIssue> relevantIssues = FilterIssues(validation.Issues, lockKeys);
                    bool hasInvalidRelevantKey = validation.InvalidKeys.Any(lockKeys.Contains);
                    if (hasInvalidRelevantKey)
                    {
                        RestoreEntries(owner, rollbackEntries, absentEntries);
                        return Task.FromResult(Result(CompositionResultStatus.Rejected, default, _revision, false, null, relevantIssues));
                    }

                    if (actionKeys.Any(_applyingKeys.Contains) || lockKeys.Any(_applyingKeys.Contains))
                    {
                        RestoreEntries(owner, rollbackEntries, absentEntries);
                        return Task.FromResult(Immediate(CompositionResultStatus.Rejected, CompositionIssueCode.Busy, default,
                            "A related key is in its synchronous application phase."));
                    }
                }

                CompositionGraphValidation targetValidation = CompositionGraph.Validate(newDefinitions, winnerIssues);
                List<CompositionIssue> targetIssues = FilterIssues(targetValidation.Issues, lockKeys);
                var targets = new Dictionary<ContentKey, Winner>();
                foreach (KeyValuePair<ContentKey, Winner> pair in newWinners)
                {
                    if (!targetValidation.InvalidKeys.Contains(pair.Key))
                    {
                        targets.Add(pair.Key, pair.Value);
                    }
                }

                if (actionKeys.Count == 0)
                {
                    return Task.FromResult(Result(
                        closeOwner ? CompositionResultStatus.NoChange : CompositionResultStatus.NoChange,
                        default,
                        _revision,
                        false,
                        null,
                        null));
                }

                cancellations = new List<CancellationTokenSource>();
                operation = CreateOperation(
                    contribution.OwnerId,
                    actionKeys,
                    lockKeys,
                    targets,
                    targetIssues,
                    false,
                    cancellationToken,
                    cancellations);
            }

            operation.CancellationIssues.AddRange(CancelOperations(cancellations));
            return ExecuteOperationAsync(operation);
        }

        private Operation CreateOperation(
            CompositionOwnerId initiatingOwnerId,
            HashSet<ContentKey> actionKeys,
            HashSet<ContentKey> lockKeys,
            Dictionary<ContentKey, Winner> targets,
            IReadOnlyList<CompositionIssue> graphIssues,
            bool closeContext,
            CancellationToken cancellationToken,
            ICollection<CancellationTokenSource> cancellations)
        {
            var operationId = new CompositionOperationId(Guid.NewGuid());
            foreach (ContentKey key in lockKeys)
            {
                if (_latestOperationByKey.TryGetValue(key, out CompositionOperationId previousId) &&
                    _activeOperations.TryGetValue(previousId, out CancellationTokenSource previousCancellation) &&
                    !cancellations.Contains(previousCancellation))
                {
                    cancellations.Add(previousCancellation);
                }

                _latestOperationByKey[key] = operationId;
            }

            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeOperations[operationId] = source;
            return new Operation(
                operationId,
                initiatingOwnerId,
                new HashSet<ContentKey>(actionKeys),
                new HashSet<ContentKey>(lockKeys),
                new Dictionary<ContentKey, Winner>(targets),
                new List<CompositionIssue>(graphIssues),
                closeContext,
                source);
        }

        private async Task<CompositionResult> ExecuteOperationAsync(Operation operation)
        {
            CompositionResult result = await ExecuteOperationCoreAsync(operation);
            var notificationIssues = new List<CompositionIssue>();
            try
            {
                notificationIssues.AddRange(DrainPendingDeltas());
            }
            catch (Exception exception)
            {
                notificationIssues.Add(new CompositionIssue(
                    CompositionIssueCode.NotificationFailed,
                    default,
                    exception.Message));
            }

            List<CompositionIssue> releaseIssues = await ReleasePendingAsync(operation);
            if (notificationIssues.Count > 0 || releaseIssues.Count > 0)
            {
                var issues = new List<CompositionIssue>(result.Issues);
                issues.AddRange(notificationIssues);
                issues.AddRange(releaseIssues);
                result = Result(CompositionResultStatus.Degraded, operation.Id, result.Revision,
                    result.EffectsMayHaveApplied, result.ChangedKeys, issues);
            }

            return result;
        }

        private async Task<CompositionResult> ExecuteOperationCoreAsync(Operation operation)
        {
            var acquiredGates = new List<SemaphoreSlim>();
            var staged = new Dictionary<ContentKey, StagedPart>();
            var issues = new List<CompositionIssue>(operation.GraphIssues);
            issues.AddRange(operation.CancellationIssues);
            var effects = new HashSet<ContentKey>();
            var detached = new HashSet<ContentKey>();
            var uncertainDetach = new HashSet<ContentKey>();
            var applied = new Dictionary<ContentKey, PartRecord>();
            var uncertainApply = new Dictionary<ContentKey, PartRecord>();
            Dictionary<ContentKey, PartRecord> previousParts = new Dictionary<ContentKey, PartRecord>();
            bool commitGateAcquired = false;
            bool applicationStarted = false;
            CompositionResult result = null;

            try
            {
                foreach (ContentKey key in operation.LockKeys.OrderBy(item => item))
                {
                    SemaphoreSlim gate = GetKeyGate(key);
                    await gate.WaitAsync(operation.Token);
                    acquiredGates.Add(gate);
                }

                if (!IsOperationCurrent(operation))
                {
                    result = Result(CompositionResultStatus.Stale, operation.Id, Revision, false, null,
                        AddIssue(issues, CompositionIssueCode.StaleOperation, default, "A newer operation owns this key closure."));
                    return result;
                }

                lock (_sync)
                {
                    foreach (ContentKey key in operation.ActionKeys)
                    {
                        if (_parts.TryGetValue(key, out PartRecord record))
                        {
                            previousParts.Add(key, record);
                        }
                    }
                }

                List<ContentKey> targetOrder = TopologicalTargetOrder(operation);
                foreach (ContentKey key in targetOrder)
                {
                    Winner target = operation.Targets[key];
                    var context = CreateContext(operation, target.OwnerId, key);
                    PartPreparationResult preparation;
                    try
                    {
                        preparation = await target.Definition.Materializer.PrepareAsync(
                            target.Definition,
                            context,
                            operation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        result = await FinishBeforeEffectsAsync(
                            operation,
                            staged,
                            issues,
                            operation.Token.IsCancellationRequested
                                ? CompositionResultStatus.Cancelled
                                : CompositionResultStatus.Stale,
                            CompositionIssueCode.Cancelled,
                            key,
                            "Part preparation was cancelled.");
                        return result;
                    }
                    catch (Exception exception)
                    {
                        result = await FinishBeforeEffectsAsync(
                            operation, staged, issues, CompositionResultStatus.Faulted,
                            CompositionIssueCode.PreparationFailed, key, exception.Message);
                        return result;
                    }

                    if (preparation == null || !preparation.Succeeded || preparation.Materialization == null)
                    {
                        string message = preparation == null ? "The materializer returned no preparation result." : preparation.Issue;
                        result = await FinishBeforeEffectsAsync(
                            operation, staged, issues, CompositionResultStatus.Faulted,
                            CompositionIssueCode.PreparationFailed, key, message);
                        return result;
                    }

                    staged.Add(key, new StagedPart(target, preparation.Materialization, context));
                    if (!IsOperationCurrent(operation))
                    {
                        result = await FinishBeforeEffectsAsync(
                            operation, staged, issues, CompositionResultStatus.Stale,
                            CompositionIssueCode.StaleOperation, key,
                            "The prepared result no longer matches its exact owner, key and operation.");
                        return result;
                    }
                }

                if (operation.Token.IsCancellationRequested)
                {
                    result = await FinishBeforeEffectsAsync(
                        operation, staged, issues,
                        IsOperationCurrent(operation) ? CompositionResultStatus.Cancelled : CompositionResultStatus.Stale,
                        CompositionIssueCode.Cancelled, default,
                        "The operation was cancelled before application.");
                    return result;
                }

                try
                {
                    await _commitGate.WaitAsync(operation.Token);
                    commitGateAcquired = true;
                }
                catch (OperationCanceledException)
                {
                    result = await FinishBeforeEffectsAsync(
                        operation, staged, issues,
                        IsOperationCurrent(operation) ? CompositionResultStatus.Cancelled : CompositionResultStatus.Stale,
                        CompositionIssueCode.Cancelled, default,
                        "The operation was cancelled before the serialized commit boundary.");
                    return result;
                }

                lock (_sync)
                {
                    if (!IsOperationCurrentLocked(operation))
                    {
                        result = Result(CompositionResultStatus.Stale, operation.Id, _revision, false, null,
                            AddIssue(issues, CompositionIssueCode.StaleOperation, default,
                                "The desired target changed before application."));
                    }
                    else
                    {
                        foreach (ContentKey key in operation.ActionKeys)
                        {
                            _applyingKeys.Add(key);
                        }

                        _applicationThreadId = Thread.CurrentThread.ManagedThreadId;
                        applicationStarted = true;
                    }
                }

                if (result != null)
                {
                    ExitApplicationPhase(operation, ref applicationStarted, ref commitGateAcquired);
                    result = await FinishBeforeEffectsAsync(
                        operation, staged, issues, CompositionResultStatus.Stale,
                        CompositionIssueCode.StaleOperation, default,
                        "The desired target changed before application.");
                    return result;
                }

                Dictionary<ContentKey, PartDefinition> previousDefinitions =
                    previousParts.ToDictionary(pair => pair.Key, pair => pair.Value.Winner.Definition);
                List<ContentKey> previousOrder = CompositionGraph.TopologicalOrder(previousParts.Keys, previousDefinitions);
                previousOrder.Reverse();

                foreach (ContentKey key in previousOrder)
                {
                    PartRecord previous = previousParts[key];
                    PartMaterializationContext context = CreateContext(operation, previous.Winner.OwnerId, key);
                    PartEffectResult detachResult;
                    try
                    {
                        detachResult = previous.Winner.Definition.Materializer.Detach(previous.Materialization, context);
                    }
                    catch (Exception exception)
                    {
                        detachResult = PartEffectResult.Failed(exception.Message, true);
                    }

                    if (detachResult == null || !detachResult.Succeeded)
                    {
                        bool mayHaveEffects = detachResult == null || detachResult.EffectsMayHaveApplied;
                        if (mayHaveEffects)
                        {
                            uncertainDetach.Add(key);
                            effects.Add(key);
                        }

                        issues.Add(new CompositionIssue(
                            CompositionIssueCode.DetachFailed,
                            key,
                            detachResult == null ? "The materializer returned no detach result." : detachResult.Issue));
                        break;
                    }

                    detached.Add(key);
                    if (detachResult.EffectsMayHaveApplied)
                    {
                        effects.Add(key);
                    }

                    if (!IsOperationCurrent(operation))
                    {
                        issues.Add(new CompositionIssue(CompositionIssueCode.StaleOperation, key,
                            "The operation was invalidated during detach."));
                        break;
                    }
                }

                bool detachFailed = issues.Any(issue => issue.Code == CompositionIssueCode.DetachFailed);
                bool staleDuringEffects = issues.Any(issue => issue.Code == CompositionIssueCode.StaleOperation);
                if (!detachFailed && !staleDuringEffects)
                {
                    foreach (ContentKey key in targetOrder)
                    {
                        StagedPart candidate = staged[key];
                        PartMaterialization previous = previousParts.TryGetValue(key, out PartRecord record)
                            ? record.Materialization
                            : null;
                        PartEffectResult applyResult;
                        try
                        {
                            applyResult = candidate.Winner.Definition.Materializer.Apply(
                                candidate.Materialization,
                                previous,
                                candidate.Context);
                        }
                        catch (Exception exception)
                        {
                            applyResult = PartEffectResult.Failed(exception.Message, true);
                        }

                        if (applyResult == null || !applyResult.Succeeded)
                        {
                            bool mayHaveEffects = applyResult == null || applyResult.EffectsMayHaveApplied;
                            if (mayHaveEffects)
                            {
                                effects.Add(key);
                                uncertainApply[key] = new PartRecord(candidate.Winner, candidate.Materialization, true);
                                staged.Remove(key);
                            }

                            issues.Add(new CompositionIssue(
                                CompositionIssueCode.ApplyFailed,
                                key,
                                applyResult == null ? "The materializer returned no apply result." : applyResult.Issue));
                            break;
                        }

                        if (applyResult.EffectsMayHaveApplied)
                        {
                            effects.Add(key);
                        }

                        applied[key] = new PartRecord(candidate.Winner, candidate.Materialization, false);
                        staged.Remove(key);
                        if (!IsOperationCurrent(operation))
                        {
                            issues.Add(new CompositionIssue(CompositionIssueCode.StaleOperation, key,
                                "The operation was invalidated during application."));
                            break;
                        }
                    }
                }

                bool applyOrCloseFailure = issues.Any(issue =>
                    issue.Code == CompositionIssueCode.DetachFailed ||
                    issue.Code == CompositionIssueCode.ApplyFailed ||
                    issue.Code == CompositionIssueCode.StaleOperation);
                if (applyOrCloseFailure)
                {
                    ExitApplicationPhase(operation, ref applicationStarted, ref commitGateAcquired);
                    result = await FinishAfterPartialEffectsAsync(
                        operation,
                        previousParts,
                        detached,
                        uncertainDetach,
                        applied,
                        uncertainApply,
                        staged,
                        effects,
                        issues);
                    return result;
                }

                var changed = effects.OrderBy(key => key).ToArray();
                bool committed;
                long committedRevision;
                lock (_sync)
                {
                    committed = IsOperationCurrentLocked(operation);
                    if (committed)
                    {
                        foreach (ContentKey key in operation.ActionKeys)
                        {
                            _parts.Remove(key);
                        }

                        foreach (KeyValuePair<ContentKey, PartRecord> pair in applied)
                        {
                            _parts[pair.Key] = pair.Value;
                        }

                        if (effects.Count > 0)
                        {
                            _revision = checked(_revision + 1);
                            if (changed.Length > 0)
                            {
                                _pendingDeltas.Add(_revision,
                                    new CompositionDelta(_contextId, operation.Id, _revision, changed));
                            }
                        }

                        committedRevision = _revision;
                    }
                    else
                    {
                        committedRevision = _revision;
                    }
                }

                if (!committed)
                {
                    issues.Add(new CompositionIssue(CompositionIssueCode.StaleOperation, default,
                        "The operation was invalidated at the commit boundary after effects began."));
                    ExitApplicationPhase(operation, ref applicationStarted, ref commitGateAcquired);
                    result = await FinishAfterPartialEffectsAsync(
                        operation, previousParts, detached, uncertainDetach, applied, uncertainApply,
                        staged, effects, issues);
                    return result;
                }

                ExitApplicationPhase(operation, ref applicationStarted, ref commitGateAcquired);
                QueueReleaseRecords(
                    operation,
                    previousParts.Where(pair => detached.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));

                bool degraded = issues.Count > operation.GraphIssues.Count;
                if (operation.GraphIssues.Count > 0)
                {
                    degraded = true;
                }

                result = Result(
                    degraded ? CompositionResultStatus.Degraded : changed.Length > 0
                        ? CompositionResultStatus.Committed
                        : operation.GraphIssues.Count > 0 ? CompositionResultStatus.Degraded : CompositionResultStatus.NoChange,
                    operation.Id,
                    committedRevision,
                    changed.Length > 0,
                    changed,
                    issues);
            }
            catch (OperationCanceledException)
            {
                result = await FinishBeforeEffectsAsync(
                    operation,
                    staged,
                    issues,
                    IsOperationCurrent(operation) ? CompositionResultStatus.Cancelled : CompositionResultStatus.Stale,
                    CompositionIssueCode.Cancelled,
                    default,
                    "The operation was cancelled while waiting for its key closure.");
            }
            finally
            {
                if (applicationStarted)
                {
                    lock (_sync)
                    {
                        _applicationThreadId = 0;
                        foreach (ContentKey key in operation.ActionKeys)
                        {
                            _applyingKeys.Remove(key);
                        }
                    }
                }

                if (commitGateAcquired)
                {
                    _commitGate.Release();
                }

                for (int i = acquiredGates.Count - 1; i >= 0; i--)
                {
                    acquiredGates[i].Release();
                }

                lock (_sync)
                {
                    _activeOperations.Remove(operation.Id);
                }

                operation.Cancellation.Dispose();
            }

            return result;
        }

        private async Task<CompositionResult> FinishBeforeEffectsAsync(
            Operation operation,
            IDictionary<ContentKey, StagedPart> staged,
            IList<CompositionIssue> issues,
            CompositionResultStatus requestedStatus,
            CompositionIssueCode issueCode,
            ContentKey key,
            string message)
        {
            if (issueCode != CompositionIssueCode.None && !string.IsNullOrEmpty(message))
            {
                issues.Add(new CompositionIssue(issueCode, key, message));
            }

            List<CompositionIssue> releaseIssues = await ReleaseStagedAsync(staged);
            issues.AddRange(releaseIssues);
            CompositionResultStatus status = releaseIssues.Count > 0 ? CompositionResultStatus.Degraded : requestedStatus;
            return Result(status, operation.Id, Revision, false, null, issues);
        }

        private async Task<CompositionResult> FinishAfterPartialEffectsAsync(
            Operation operation,
            IDictionary<ContentKey, PartRecord> previousParts,
            ISet<ContentKey> detached,
            ISet<ContentKey> uncertainDetach,
            IDictionary<ContentKey, PartRecord> applied,
            IDictionary<ContentKey, PartRecord> uncertainApply,
            IDictionary<ContentKey, StagedPart> staged,
            ISet<ContentKey> effects,
            IList<CompositionIssue> issues)
        {
            long revision;
            var changed = effects.OrderBy(key => key).ToArray();
            lock (_sync)
            {
                foreach (ContentKey key in detached)
                {
                    _parts.Remove(key);
                }

                foreach (ContentKey key in uncertainDetach)
                {
                    if (previousParts.TryGetValue(key, out PartRecord previous))
                    {
                        _parts[key] = previous.WithUncertain(true);
                    }
                }

                foreach (KeyValuePair<ContentKey, PartRecord> pair in applied)
                {
                    _parts[pair.Key] = pair.Value.WithUncertain(true);
                }

                foreach (KeyValuePair<ContentKey, PartRecord> pair in uncertainApply)
                {
                    _parts[pair.Key] = pair.Value.WithUncertain(true);
                }

                if (effects.Count > 0)
                {
                    _revision = checked(_revision + 1);
                    if (changed.Length > 0)
                    {
                        _pendingDeltas.Add(_revision,
                            new CompositionDelta(_contextId, operation.Id, _revision, changed));
                    }
                }

                revision = _revision;
            }

            var detachedRecords = new Dictionary<ContentKey, PartRecord>();
            foreach (ContentKey key in detached)
            {
                if (previousParts.TryGetValue(key, out PartRecord record))
                {
                    detachedRecords.Add(key, record);
                }
            }

            QueueReleaseRecords(operation, detachedRecords);
            issues.AddRange(await ReleaseStagedAsync(staged));

            return Result(
                effects.Count > 0 || uncertainDetach.Count > 0 || uncertainApply.Count > 0
                    ? CompositionResultStatus.Degraded
                    : CompositionResultStatus.Faulted,
                operation.Id,
                revision,
                effects.Count > 0 || uncertainDetach.Count > 0 || uncertainApply.Count > 0,
                changed,
                issues);
        }

        private async Task<List<CompositionIssue>> ReleaseStagedAsync(
            IDictionary<ContentKey, StagedPart> staged)
        {
            var issues = new List<CompositionIssue>();
            foreach (KeyValuePair<ContentKey, StagedPart> pair in staged.ToArray())
            {
                issues.AddRange(await ReleaseOneAsync(
                    pair.Key,
                    pair.Value.Winner,
                    pair.Value.Materialization,
                    pair.Value.Context));
            }

            staged.Clear();
            return issues;
        }

        private void QueueReleaseRecords(
            Operation operation,
            IDictionary<ContentKey, PartRecord> records)
        {
            Dictionary<ContentKey, PartDefinition> definitions =
                records.ToDictionary(pair => pair.Key, pair => pair.Value.Winner.Definition);
            List<ContentKey> releaseOrder = CompositionGraph.TopologicalOrder(records.Keys, definitions);
            releaseOrder.Reverse();
            foreach (ContentKey key in releaseOrder)
            {
                PartRecord record = records[key];
                PartMaterializationContext context = CreateContext(operation, record.Winner.OwnerId, key);
                operation.PendingReleases.Add(new PendingRelease(
                    key,
                    record.Winner,
                    record.Materialization,
                    context));
            }
        }

        private async Task<List<CompositionIssue>> ReleasePendingAsync(Operation operation)
        {
            var issues = new List<CompositionIssue>();
            foreach (PendingRelease release in operation.PendingReleases.ToArray())
            {
                issues.AddRange(await ReleaseOneAsync(
                    release.Key,
                    release.Winner,
                    release.Materialization,
                    release.Context));
            }

            operation.PendingReleases.Clear();
            return issues;
        }

        private void ExitApplicationPhase(
            Operation operation,
            ref bool applicationStarted,
            ref bool commitGateAcquired)
        {
            if (applicationStarted)
            {
                lock (_sync)
                {
                    _applicationThreadId = 0;
                    foreach (ContentKey key in operation.ActionKeys)
                    {
                        _applyingKeys.Remove(key);
                    }
                }

                applicationStarted = false;
            }

            if (commitGateAcquired)
            {
                _commitGate.Release();
                commitGateAcquired = false;
            }
        }

        private async Task<List<CompositionIssue>> ReleaseOneAsync(
            ContentKey key,
            Winner winner,
            PartMaterialization materialization,
            PartMaterializationContext context)
        {
            var issues = new List<CompositionIssue>();
            PartReleaseResult release;
            try
            {
                release = await winner.Definition.Materializer.ReleaseAsync(
                    materialization,
                    context,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                issues.Add(new CompositionIssue(CompositionIssueCode.ReleaseFailed, key, exception.Message));
                return issues;
            }

            if (release == null || release.Outcome == PartReleaseOutcome.Failed)
            {
                issues.Add(new CompositionIssue(CompositionIssueCode.ReleaseFailed, key,
                    release == null ? "The materializer returned no release result." : release.Issue));
            }
            else if (release.Outcome == PartReleaseOutcome.Pending)
            {
                issues.Add(new CompositionIssue(CompositionIssueCode.ReleasePending, key, release.Issue));
            }

            return issues;
        }

        private List<CompositionIssue> PublishDelta(CompositionDelta delta)
        {
            var issues = new List<CompositionIssue>();
            Action<CompositionDelta> handlers = Committed;
            if (handlers == null)
            {
                return issues;
            }

            foreach (Action<CompositionDelta> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(delta);
                }
                catch (Exception exception)
                {
                    issues.Add(new CompositionIssue(CompositionIssueCode.NotificationFailed, default, exception.Message));
                }
            }

            return issues;
        }

        private List<CompositionIssue> DrainPendingDeltas()
        {
            lock (_sync)
            {
                if (_publishingDeltas)
                {
                    return new List<CompositionIssue>();
                }

                _publishingDeltas = true;
            }

            var issues = new List<CompositionIssue>();
            try
            {
                while (true)
                {
                    CompositionDelta delta;
                    lock (_sync)
                    {
                        if (_pendingDeltas.Count == 0)
                        {
                            _publishingDeltas = false;
                            return issues;
                        }

                        KeyValuePair<long, CompositionDelta> next = _pendingDeltas.First();
                        _pendingDeltas.Remove(next.Key);
                        delta = next.Value;
                    }

                    issues.AddRange(PublishDelta(delta));
                }
            }
            catch
            {
                lock (_sync)
                {
                    _publishingDeltas = false;
                }

                throw;
            }
        }

        private PartMaterializationContext CreateContext(Operation operation, CompositionOwnerId ownerId, ContentKey key)
        {
            return new PartMaterializationContext(
                _contextId,
                operation.Id,
                ownerId.IsValid ? ownerId : operation.InitiatingOwnerId,
                key,
                Revision,
                operation.LockKeys.OrderBy(item => item).ToArray());
        }

        private List<ContentKey> TopologicalTargetOrder(Operation operation)
        {
            var selected = operation.Targets.Keys.Where(operation.ActionKeys.Contains).ToArray();
            var definitions = operation.Targets.ToDictionary(pair => pair.Key, pair => pair.Value.Definition);
            return CompositionGraph.TopologicalOrder(selected, definitions);
        }

        private SemaphoreSlim GetKeyGate(ContentKey key)
        {
            lock (_sync)
            {
                if (!_keyGates.TryGetValue(key, out SemaphoreSlim gate))
                {
                    gate = new SemaphoreSlim(1, 1);
                    _keyGates.Add(key, gate);
                }

                return gate;
            }
        }

        private bool IsOperationCurrent(Operation operation)
        {
            lock (_sync)
            {
                return IsOperationCurrentLocked(operation);
            }
        }

        private bool IsOperationCurrentLocked(Operation operation)
        {
            if (_closed && !operation.CloseContext)
            {
                return false;
            }

            foreach (ContentKey key in operation.LockKeys)
            {
                if (!_latestOperationByKey.TryGetValue(key, out CompositionOperationId current) || current != operation.Id)
                {
                    return false;
                }
            }

            return true;
        }

        private bool PartMatches(IDictionary<ContentKey, PartRecord> parts, ContentKey key, Winner winner)
        {
            return parts.TryGetValue(key, out PartRecord part) &&
                   !part.IsUncertain &&
                   part.Winner.OwnerId == winner.OwnerId &&
                   ReferenceEquals(part.Winner.Definition, winner.Definition);
        }

        private static Dictionary<ContentKey, Winner> ResolveWinners(
            IReadOnlyDictionary<CompositionOwnerId, ContributionState> owners,
            out List<CompositionIssue> issues)
        {
            var candidates = new Dictionary<ContentKey, List<Winner>>();
            foreach (ContributionState owner in owners.Values)
            {
                if (owner.IsClosed)
                {
                    continue;
                }

                foreach (KeyValuePair<ContentKey, PartDefinition> pair in owner.Entries)
                {
                    if (!candidates.TryGetValue(pair.Key, out List<Winner> list))
                    {
                        list = new List<Winner>();
                        candidates.Add(pair.Key, list);
                    }

                    list.Add(new Winner(owner.Handle.OwnerId, owner.Handle.Priority, pair.Value));
                }
            }

            var winners = new Dictionary<ContentKey, Winner>();
            issues = new List<CompositionIssue>();
            foreach (KeyValuePair<ContentKey, List<Winner>> pair in candidates.OrderBy(item => item.Key))
            {
                int highestPriority = pair.Value.Max(candidate => candidate.Priority);
                Winner winner = null;
                int highestCount = 0;
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    if (pair.Value[i].Priority == highestPriority)
                    {
                        highestCount++;
                        winner = pair.Value[i];
                    }
                }

                if (highestCount > 1)
                {
                    issues.Add(new CompositionIssue(
                        CompositionIssueCode.EqualPriorityConflict,
                        pair.Key,
                        "Multiple owners contribute this key at the highest priority."));
                    continue;
                }

                winners.Add(pair.Key, winner);
            }

            return winners;
        }

        private static Dictionary<ContentKey, PartDefinition> DefinitionsFromWinners(
            IReadOnlyDictionary<ContentKey, Winner> winners)
        {
            return winners.ToDictionary(pair => pair.Key, pair => pair.Value.Definition);
        }

        private static Dictionary<ContentKey, PartDefinition> DefinitionsFromParts(
            IReadOnlyDictionary<ContentKey, PartRecord> parts)
        {
            return parts.ToDictionary(pair => pair.Key, pair => pair.Value.Winner.Definition);
        }

        private static void MergeDefinitions(
            IDictionary<ContentKey, PartDefinition> destination,
            IReadOnlyDictionary<ContentKey, PartDefinition> source)
        {
            foreach (KeyValuePair<ContentKey, PartDefinition> pair in source)
            {
                if (!destination.ContainsKey(pair.Key))
                {
                    destination.Add(pair.Key, pair.Value);
                }
            }
        }

        private static HashSet<ContentKey> GetWinnerChanges(
            IReadOnlyDictionary<ContentKey, Winner> previous,
            IReadOnlyDictionary<ContentKey, Winner> current)
        {
            var changed = new HashSet<ContentKey>(previous.Keys);
            changed.UnionWith(current.Keys);
            changed.RemoveWhere(key => WinnersEqual(
                previous.TryGetValue(key, out Winner oldWinner) ? oldWinner : null,
                current.TryGetValue(key, out Winner newWinner) ? newWinner : null));
            return changed;
        }

        private static bool WinnersEqual(Winner left, Winner right)
        {
            return left == null
                ? right == null
                : right != null && left.OwnerId == right.OwnerId && left.Priority == right.Priority &&
                  ReferenceEquals(left.Definition, right.Definition);
        }

        private static void RestoreEntries(
            ContributionState owner,
            IReadOnlyDictionary<ContentKey, PartDefinition> previous,
            ISet<ContentKey> absent)
        {
            foreach (KeyValuePair<ContentKey, PartDefinition> pair in previous)
            {
                owner.Entries[pair.Key] = pair.Value;
            }

            foreach (ContentKey key in absent)
            {
                owner.Entries.Remove(key);
            }
        }

        private static List<CompositionIssue> FilterIssues(
            IEnumerable<CompositionIssue> issues,
            ISet<ContentKey> keys)
        {
            return issues.Where(issue => !issue.Key.IsValid || keys.Contains(issue.Key)).ToList();
        }

        private static List<CompositionIssue> AddIssue(
            IList<CompositionIssue> issues,
            CompositionIssueCode code,
            ContentKey key,
            string message)
        {
            issues.Add(new CompositionIssue(code, key, message));
            return issues.ToList();
        }

        private CompositionResult Immediate(
            CompositionResultStatus status,
            CompositionIssueCode code,
            ContentKey key,
            string message)
        {
            return Result(status, default, Revision, false, null,
                new[] { new CompositionIssue(code, key, message) });
        }

        private CompositionResult Result(
            CompositionResultStatus status,
            CompositionOperationId operationId,
            long revision,
            bool effectsMayHaveApplied,
            IEnumerable<ContentKey> changedKeys,
            IEnumerable<CompositionIssue> issues)
        {
            return new CompositionResult(status, _contextId, operationId, revision,
                effectsMayHaveApplied, changedKeys, issues);
        }

        private static List<CompositionIssue> CancelOperations(IEnumerable<CancellationTokenSource> cancellations)
        {
            var issues = new List<CompositionIssue>();
            foreach (CancellationTokenSource cancellation in cancellations)
            {
                try
                {
                    cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A completed operation already relinquished its cancellation source.
                }
                catch (AggregateException exception)
                {
                    issues.Add(new CompositionIssue(
                        CompositionIssueCode.CancellationCallbackFailed,
                        default,
                        exception.Flatten().Message));
                }
            }

            return issues;
        }

        private sealed class ContributionState
        {
            public readonly CompositionContribution Handle;
            public readonly Dictionary<ContentKey, PartDefinition> Entries = new Dictionary<ContentKey, PartDefinition>();
            public bool IsClosed;

            public ContributionState(CompositionContribution handle)
            {
                Handle = handle;
            }
        }

        private sealed class Winner
        {
            public readonly CompositionOwnerId OwnerId;
            public readonly int Priority;
            public readonly PartDefinition Definition;

            public Winner(CompositionOwnerId ownerId, int priority, PartDefinition definition)
            {
                OwnerId = ownerId;
                Priority = priority;
                Definition = definition;
            }
        }

        private sealed class PartRecord
        {
            public readonly Winner Winner;
            public readonly PartMaterialization Materialization;
            public readonly bool IsUncertain;

            public PartRecord(Winner winner, PartMaterialization materialization, bool isUncertain)
            {
                Winner = winner;
                Materialization = materialization;
                IsUncertain = isUncertain;
            }

            public PartRecord WithUncertain(bool uncertain) => new PartRecord(Winner, Materialization, uncertain);
        }

        private sealed class StagedPart
        {
            public readonly Winner Winner;
            public readonly PartMaterialization Materialization;
            public readonly PartMaterializationContext Context;

            public StagedPart(Winner winner, PartMaterialization materialization, PartMaterializationContext context)
            {
                Winner = winner;
                Materialization = materialization;
                Context = context;
            }
        }

        private sealed class PendingRelease
        {
            public readonly ContentKey Key;
            public readonly Winner Winner;
            public readonly PartMaterialization Materialization;
            public readonly PartMaterializationContext Context;

            public PendingRelease(
                ContentKey key,
                Winner winner,
                PartMaterialization materialization,
                PartMaterializationContext context)
            {
                Key = key;
                Winner = winner;
                Materialization = materialization;
                Context = context;
            }
        }

        private sealed class Operation
        {
            public readonly CompositionOperationId Id;
            public readonly CompositionOwnerId InitiatingOwnerId;
            public readonly HashSet<ContentKey> ActionKeys;
            public readonly HashSet<ContentKey> LockKeys;
            public readonly Dictionary<ContentKey, Winner> Targets;
            public readonly List<CompositionIssue> GraphIssues;
            public readonly bool CloseContext;
            public readonly CancellationTokenSource Cancellation;
            public CancellationToken Token => Cancellation.Token;
            public readonly List<CompositionIssue> CancellationIssues = new List<CompositionIssue>();
            public readonly List<PendingRelease> PendingReleases = new List<PendingRelease>();

            public Operation(
                CompositionOperationId id,
                CompositionOwnerId initiatingOwnerId,
                HashSet<ContentKey> actionKeys,
                HashSet<ContentKey> lockKeys,
                Dictionary<ContentKey, Winner> targets,
                List<CompositionIssue> graphIssues,
                bool closeContext,
                CancellationTokenSource cancellation)
            {
                Id = id;
                InitiatingOwnerId = initiatingOwnerId;
                ActionKeys = actionKeys;
                LockKeys = lockKeys;
                Targets = targets;
                GraphIssues = graphIssues;
                CloseContext = closeContext;
                Cancellation = cancellation;
            }
        }
    }
}
