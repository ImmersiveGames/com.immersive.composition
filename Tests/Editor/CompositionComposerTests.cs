using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Immersive.Composition;
using NUnit.Framework;

namespace Immersive.Composition.Tests
{
    public sealed class CompositionComposerTests
    {
        [Test]
        public async Task HigherPriorityContributionWinsAndClosingItRestoresTheLowerPriorityPart()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer();
            ContentKey key = ContentKey.Create("visual");
            CompositionContribution baseOwner = composer.CreateContribution(
                CompositionOwnerId.Create("profile.base"), 10);
            CompositionContribution temporaryOwner = composer.CreateContribution(
                CompositionOwnerId.Create("activity.override"), 20);

            CompositionResult baseResult = await baseOwner.SetPartAsync(Definition(key, "base", materializer));
            CompositionResult overrideResult = await temporaryOwner.SetPartAsync(
                Definition(key, "override", materializer));

            Assert.That(baseResult.Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(overrideResult.Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(materializer.Active[key], Is.EqualTo("override"));

            CompositionResult closeResult = await temporaryOwner.CloseAsync();

            Assert.That(closeResult.Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(materializer.Active[key], Is.EqualTo("base"));
        }

        [Test]
        public async Task EqualPriorityConflictIsRejectedBeforeMaterializerEffects()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer();
            ContentKey key = ContentKey.Create("visual");
            CompositionContribution first = composer.CreateContribution(
                CompositionOwnerId.Create("owner.first"), 5);
            CompositionContribution second = composer.CreateContribution(
                CompositionOwnerId.Create("owner.second"), 5);

            await first.SetPartAsync(Definition(key, "first", materializer));
            int preparesBeforeConflict = materializer.PrepareCalls;
            int appliesBeforeConflict = materializer.ApplyCalls;

            CompositionResult result = await second.SetPartAsync(Definition(key, "second", materializer));

            Assert.That(result.Status, Is.EqualTo(CompositionResultStatus.Rejected));
            Assert.That(result.Issues.Any(issue => issue.Code == CompositionIssueCode.EqualPriorityConflict), Is.True);
            Assert.That(materializer.PrepareCalls, Is.EqualTo(preparesBeforeConflict));
            Assert.That(materializer.ApplyCalls, Is.EqualTo(appliesBeforeConflict));
            Assert.That(materializer.Active[key], Is.EqualTo("first"));
        }

        [Test]
        public async Task MissingDependencyAndCycleAreRejectedBeforeMaterialization()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer();
            CompositionContribution owner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.graph"), 1);
            ContentKey a = ContentKey.Create("part.a");
            ContentKey b = ContentKey.Create("part.b");

            CompositionResult missing = await owner.SetPartAsync(
                Definition(a, "a", materializer, b));
            CompositionResult cycle = await owner.SetPartsAsync(new[]
            {
                Definition(a, "a", materializer, b),
                Definition(b, "b", materializer, a)
            });

            Assert.That(missing.Status, Is.EqualTo(CompositionResultStatus.Rejected));
            Assert.That(missing.Issues.Any(issue => issue.Code == CompositionIssueCode.MissingDependency), Is.True);
            Assert.That(cycle.Status, Is.EqualTo(CompositionResultStatus.Rejected));
            Assert.That(cycle.Issues.Any(issue => issue.Code == CompositionIssueCode.DependencyCycle), Is.True);
            Assert.That(materializer.PrepareCalls, Is.Zero);
            Assert.That(materializer.ApplyCalls, Is.Zero);
        }

        [Test]
        public async Task DependencyClosureAppliesPrerequisitesFirstAndAdvancesOneRevision()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer();
            CompositionContribution owner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.graph"), 1);
            ContentKey dependencyKey = ContentKey.Create("data.palette");
            ContentKey dependentKey = ContentKey.Create("visual.actor");

            CompositionResult initial = await owner.SetPartsAsync(new[]
            {
                Definition(dependentKey, "visual.v1", materializer, dependencyKey),
                Definition(dependencyKey, "palette.v1", materializer)
            });
            Assert.That(initial.Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(initial.Revision, Is.EqualTo(1));

            materializer.Events.Clear();
            CompositionResult update = await owner.SetPartAsync(
                Definition(dependencyKey, "palette.v2", materializer));

            Assert.That(update.Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(update.Revision, Is.EqualTo(2));
            Assert.That(materializer.Events, Is.EqualTo(new[]
            {
                "prepare:data.palette",
                "prepare:visual.actor",
                "detach:visual.actor",
                "detach:data.palette",
                "apply:data.palette",
                "apply:visual.actor",
                "release:visual.actor",
                "release:data.palette"
            }));
        }

        [Test]
        public async Task SlowPreparationForOneKeyDoesNotBlockAnUnrelatedKey()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer { BlockedDescriptor = "slow" };
            CompositionContribution owner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.concurrent"), 1);
            Task<CompositionResult> slow = owner.SetPartAsync(
                Definition(ContentKey.Create("data.slow"), "slow", materializer));
            await materializer.BlockedPreparationStarted.Task;

            Task<CompositionResult> fast = owner.SetPartAsync(
                Definition(ContentKey.Create("data.fast"), "fast", materializer));
            Task completed = await Task.WhenAny(fast, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.That(completed, Is.SameAs(fast), "An unrelated key must prepare and commit while the first provider is pending.");
            Assert.That((await fast).Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(slow.IsCompleted, Is.False);

            materializer.CompleteBlockedPreparation(PartOwnership.Acquired);
            Assert.That((await slow).Status, Is.EqualTo(CompositionResultStatus.Committed));
        }

        [Test]
        public async Task SlowReleaseDoesNotBlockUnrelatedCommitOrReverseDeltaOrder()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer { BlockedReleaseValue = "old" };
            var fastMaterializer = new RecordingMaterializer();
            var revisions = new List<long>();
            composer.Committed += delta => revisions.Add(delta.Revision);
            CompositionContribution owner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.release-concurrency"), 1);
            ContentKey slowKey = ContentKey.Create("data.slow-release");
            ContentKey fastKey = ContentKey.Create("data.fast-commit");

            await owner.SetPartAsync(Definition(slowKey, "old", materializer));
            Task<CompositionResult> slowUpdate = owner.SetPartAsync(
                Definition(slowKey, "new", materializer));
            await materializer.BlockedReleaseStarted.Task;

            Task<CompositionResult> fastUpdate = owner.SetPartAsync(
                Definition(fastKey, "fast", fastMaterializer));
            Task completed = await Task.WhenAny(fastUpdate, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.That(completed, Is.SameAs(fastUpdate), "Cleanup for one key must not hold the global commit boundary.");
            Assert.That((await fastUpdate).Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(slowUpdate.IsCompleted, Is.False);
            Assert.That(revisions, Is.EqualTo(new long[] { 1, 2, 3 }));

            materializer.CompleteBlockedRelease();
            Assert.That((await slowUpdate).Status, Is.EqualTo(CompositionResultStatus.Committed));
        }

        [Test]
        public async Task NewerOperationInvalidatesLatePreparationAndReleasesItsAcquisition()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer { BlockedDescriptor = "old" };
            CompositionContribution owner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.operation"), 1);
            ContentKey key = ContentKey.Create("visual");

            Task<CompositionResult> oldOperation = owner.SetPartAsync(Definition(key, "old", materializer));
            await materializer.BlockedPreparationStarted.Task;
            Task<CompositionResult> currentOperation = owner.SetPartAsync(Definition(key, "current", materializer));
            materializer.CompleteBlockedPreparation(PartOwnership.Acquired);

            CompositionResult stale = await oldOperation;
            CompositionResult committed = await currentOperation;

            Assert.That(stale.Status, Is.EqualTo(CompositionResultStatus.Stale));
            Assert.That(committed.Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(materializer.DestroyedValues, Does.Contain("old"));
            Assert.That(materializer.Active[key], Is.EqualTo("current"));
        }

        [Test]
        public async Task ExternalTargetChangeDuringPreparationRejectsBeforeMaterializerEffects()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer { BlockedDescriptor = "late-target" };
            CompositionContribution owner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.external-target"), 1);
            ContentKey key = ContentKey.Create("visual");
            bool isTargetCurrent = true;

            Task<CompositionResult> update = owner.SetPartWhileCurrentAsync(
                Definition(key, "late-target", materializer),
                () => isTargetCurrent);
            await materializer.BlockedPreparationStarted.Task;
            isTargetCurrent = false;
            materializer.CompleteBlockedPreparation(PartOwnership.Acquired);

            CompositionResult result = await update;

            Assert.That(result.Status, Is.EqualTo(CompositionResultStatus.Stale));
            Assert.That(result.EffectsMayHaveApplied, Is.False);
            Assert.That(result.Revision, Is.Zero);
            Assert.That(materializer.ApplyCalls, Is.Zero);
            Assert.That(materializer.DestroyedValues, Does.Contain("late-target"));
            Assert.That(materializer.Active, Is.Empty);
            Assert.That((await composer.CloseAsync()).Status,
                Is.EqualTo(CompositionResultStatus.NoChange));
        }

        [Test]
        public async Task StaleGuardedIntentCannotWinALaterOwnerCommit()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer { BlockedDescriptor = "stale-target" };
            CompositionContribution staleOwner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.stale-target"), 20);
            CompositionContribution currentOwner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.current-target"), 10);
            ContentKey key = ContentKey.Create("visual");
            bool isTargetCurrent = true;

            Task<CompositionResult> staleUpdate = staleOwner.SetPartWhileCurrentAsync(
                Definition(key, "stale-target", materializer),
                () => isTargetCurrent);
            await materializer.BlockedPreparationStarted.Task;
            isTargetCurrent = false;

            Task<CompositionResult> currentUpdate = currentOwner.SetPartAsync(
                Definition(key, "current-target", materializer));
            materializer.CompleteBlockedPreparation(PartOwnership.Acquired);

            CompositionResult staleResult = await staleUpdate;
            CompositionResult currentResult = await currentUpdate;

            Assert.That(staleResult.Status, Is.EqualTo(CompositionResultStatus.Stale));
            Assert.That(materializer.DestroyedValues, Does.Contain("stale-target"));
            Assert.That(currentResult.Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(materializer.Active[key], Is.EqualTo("current-target"),
                "A later owner must not materialize intent rejected by the stale target guard.");
            Assert.That(materializer.ApplyCalls, Is.EqualTo(1));
        }

        [Test]
        public async Task GuardedOwnerRejectsUnguardedMutationAfterItsTargetBecomesStale()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer();
            CompositionContribution guardedOwner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.expired-target"), 20);
            CompositionContribution currentOwner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.live-target"), 10);
            ContentKey key = ContentKey.Create("visual");
            bool isTargetCurrent = true;

            CompositionResult initial = await guardedOwner.SetPartWhileCurrentAsync(
                Definition(key, "old-guarded-content", materializer),
                () => isTargetCurrent);
            Assert.That(initial.Status, Is.EqualTo(CompositionResultStatus.Committed));

            isTargetCurrent = false;
            CompositionResult staleAttempt = await guardedOwner.SetPartAsync(
                Definition(key, "never-accepted-content", materializer),
                CancellationToken.None);
            CompositionResult replacement = await currentOwner.SetPartAsync(
                Definition(key, "live-content", materializer));

            Assert.That(staleAttempt.Status, Is.EqualTo(CompositionResultStatus.Stale));
            Assert.That(replacement.Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(materializer.Active[key], Is.EqualTo("live-content"),
                "Once its target is stale, a guarded owner's earlier intent must not retain precedence.");
        }

        [Test]
        public async Task StaleGuardedWinnerIsInvalidatedWhenAnotherOwnersDependencyUpdateIsPreparing()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer();
            CompositionContribution paletteOwner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.palette"), 10);
            CompositionContribution visualOwner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.guarded-visual"), 20);
            ContentKey paletteKey = ContentKey.Create("data.palette");
            ContentKey visualKey = ContentKey.Create("visual.actor");
            bool isVisualTargetCurrent = true;

            CompositionResult initialPalette = await paletteOwner.SetPartAsync(
                Definition(paletteKey, "palette.v1", materializer));
            CompositionResult initialVisual = await visualOwner.SetPartWhileCurrentAsync(
                Definition(visualKey, "visual.v1", materializer, paletteKey),
                () => isVisualTargetCurrent);

            Assert.That(initialPalette.Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(initialVisual.Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(materializer.Active[paletteKey], Is.EqualTo("palette.v1"));
            Assert.That(materializer.Active[visualKey], Is.EqualTo("visual.v1"));
            int applyCallsBeforeUpdate = materializer.ApplyCalls;
            materializer.BlockedDescriptor = "palette.v2";

            Task<CompositionResult> update = paletteOwner.SetPartAsync(
                Definition(paletteKey, "palette.v2", materializer));
            await materializer.BlockedPreparationStarted.Task;
            isVisualTargetCurrent = false;
            materializer.CompleteBlockedPreparation(PartOwnership.Acquired);

            CompositionResult result = await update;

            Assert.That(result.Status, Is.EqualTo(CompositionResultStatus.Stale));
            Assert.That(result.EffectsMayHaveApplied, Is.False);
            Assert.That(result.Revision, Is.EqualTo(2));
            Assert.That(materializer.ApplyCalls, Is.EqualTo(applyCallsBeforeUpdate));
            Assert.That(materializer.DestroyedValues, Does.Contain("palette.v2"));
            Assert.That(materializer.Active[paletteKey], Is.EqualTo("palette.v1"));
            Assert.That(materializer.Active[visualKey], Is.EqualTo("visual.v1"));
            await composer.CloseAsync();
        }

        [Test]
        public async Task CallerCancellationDuringPreparationReleasesLateAcquisitionWithoutApplying()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer { BlockedDescriptor = "cancelled-late" };
            CompositionContribution owner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.cancelled-preparation"), 1);
            ContentKey key = ContentKey.Create("visual");
            using var cancellation = new CancellationTokenSource();

            Task<CompositionResult> preparation = owner.SetPartAsync(
                Definition(key, "cancelled-late", materializer),
                cancellation.Token);
            await materializer.BlockedPreparationStarted.Task;
            cancellation.Cancel();
            materializer.CompleteBlockedPreparation(PartOwnership.Acquired);

            CompositionResult result = await preparation;

            Assert.That(result.Status, Is.EqualTo(CompositionResultStatus.Cancelled));
            Assert.That(result.EffectsMayHaveApplied, Is.False);
            Assert.That(result.Revision, Is.Zero);
            Assert.That(materializer.ApplyCalls, Is.Zero);
            Assert.That(materializer.DestroyedValues, Does.Contain("cancelled-late"));
            Assert.That(materializer.Active, Is.Empty);
        }

        [Test]
        public async Task OwnerCloseInvalidatesPendingPreparationAndDoesNotPublishLateContent()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer { BlockedDescriptor = "late" };
            CompositionContribution owner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.closing"), 1);
            ContentKey key = ContentKey.Create("visual");

            Task<CompositionResult> preparation = owner.SetPartAsync(Definition(key, "late", materializer));
            await materializer.BlockedPreparationStarted.Task;
            Task<CompositionResult> closing = owner.CloseAsync();
            materializer.CompleteBlockedPreparation(PartOwnership.Acquired);

            CompositionResult stale = await preparation;
            CompositionResult closeResult = await closing;

            Assert.That(stale.Status, Is.EqualTo(CompositionResultStatus.Stale));
            Assert.That(closeResult.Status, Is.EqualTo(CompositionResultStatus.NoChange));
            Assert.That(materializer.ApplyCalls, Is.Zero);
            Assert.That(materializer.DestroyedValues, Does.Contain("late"));
        }

        [Test]
        public async Task NonOwnerCannotRemoveAnotherOwnersPart()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer();
            ContentKey key = ContentKey.Create("visual");
            CompositionContribution owner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.visual"), 1);
            CompositionContribution other = composer.CreateContribution(
                CompositionOwnerId.Create("owner.other"), 2);
            await owner.SetPartAsync(Definition(key, "owned", materializer));
            int releasesBeforeAttempt = materializer.ReleaseCalls;

            CompositionResult unauthorized = await other.RemovePartAsync(key);

            Assert.That(unauthorized.Status, Is.EqualTo(CompositionResultStatus.Rejected));
            Assert.That(unauthorized.Issues.Any(issue => issue.Code == CompositionIssueCode.OwnerDoesNotContributeKey), Is.True);
            Assert.That(materializer.ReleaseCalls, Is.EqualTo(releasesBeforeAttempt));
            Assert.That(materializer.Active[key], Is.EqualTo("owned"));
        }

        [Test]
        public async Task AdoptedPartIsDetachedButMaterializerRetainsItsResource()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer { Ownership = PartOwnership.Adopted };
            CompositionContribution owner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.scene"), 1);
            ContentKey key = ContentKey.Create("scene.visual");
            await owner.SetPartAsync(Definition(key, "scene-object", materializer));

            CompositionResult result = await owner.RemovePartAsync(key);

            Assert.That(result.Status, Is.EqualTo(CompositionResultStatus.Committed));
            Assert.That(materializer.Active.ContainsKey(key), Is.False);
            Assert.That(materializer.DestroyedValues, Does.Not.Contain("scene-object"));
            Assert.That(materializer.RetainedValues, Does.Contain("scene-object"));
        }

        [Test]
        public async Task PostEffectApplyFailureReturnsDegradedResultAndAdvancesRevision()
        {
            var composer = new CompositionComposer();
            var materializer = new RecordingMaterializer { FailAfterApplying = true };
            CompositionContribution owner = composer.CreateContribution(
                CompositionOwnerId.Create("owner.fault"), 1);
            ContentKey key = ContentKey.Create("physical.part");

            CompositionResult result = await owner.SetPartAsync(Definition(key, "part", materializer));

            Assert.That(result.Status, Is.EqualTo(CompositionResultStatus.Degraded));
            Assert.That(result.EffectsMayHaveApplied, Is.True);
            Assert.That(result.Revision, Is.EqualTo(1));
            Assert.That(materializer.Active[key], Is.EqualTo("part"));
            Assert.That(result.Issues.Any(issue => issue.Code == CompositionIssueCode.ApplyFailed), Is.True);
        }

        private static PartDefinition Definition(
            ContentKey key,
            string descriptor,
            IPartMaterializer materializer,
            params ContentKey[] dependencies)
        {
            return new PartDefinition(key, descriptor, materializer, dependencies);
        }

        private sealed class RecordingMaterializer : IPartMaterializer
        {
            public readonly List<string> Events = new List<string>();
            public readonly Dictionary<ContentKey, object> Active = new Dictionary<ContentKey, object>();
            public readonly List<object> DestroyedValues = new List<object>();
            public readonly List<object> RetainedValues = new List<object>();
            public readonly TaskCompletionSource<bool> BlockedPreparationStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> BlockedReleaseStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<PartMaterialization> _blockedPreparation =
                new TaskCompletionSource<PartMaterialization>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _blockedRelease =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public string BlockedDescriptor;
            public object BlockedReleaseValue;
            public PartOwnership Ownership = PartOwnership.Acquired;
            public bool FailAfterApplying;
            public int PrepareCalls;
            public int ApplyCalls;
            public int ReleaseCalls;

            public async Task<PartPreparationResult> PrepareAsync(
                PartDefinition definition,
                PartMaterializationContext context,
                System.Threading.CancellationToken cancellationToken)
            {
                PrepareCalls++;
                Events.Add("prepare:" + context.Key.Value);
                string descriptor = (string)definition.Descriptor;
                if (descriptor == BlockedDescriptor)
                {
                    BlockedPreparationStarted.TrySetResult(true);
                    PartMaterialization delayed = await _blockedPreparation.Task;
                    return PartPreparationResult.Prepared(delayed);
                }

                return PartPreparationResult.Prepared(new PartMaterialization(descriptor, Ownership));
            }

            public PartEffectResult Apply(
                PartMaterialization candidate,
                PartMaterialization previous,
                PartMaterializationContext context)
            {
                ApplyCalls++;
                Events.Add("apply:" + context.Key.Value);
                Active[context.Key] = candidate.Value;
                if (FailAfterApplying)
                {
                    return PartEffectResult.Failed("Injected failure after attaching the part.", true);
                }

                return PartEffectResult.Applied();
            }

            public PartEffectResult Detach(
                PartMaterialization current,
                PartMaterializationContext context)
            {
                Events.Add("detach:" + context.Key.Value);
                Active.Remove(context.Key);
                return PartEffectResult.Applied();
            }

            public async Task<PartReleaseResult> ReleaseAsync(
                PartMaterialization materialization,
                PartMaterializationContext context,
                System.Threading.CancellationToken cancellationToken)
            {
                ReleaseCalls++;
                Events.Add("release:" + context.Key.Value);
                if (Equals(materialization.Value, BlockedReleaseValue))
                {
                    BlockedReleaseStarted.TrySetResult(true);
                    await _blockedRelease.Task;
                }

                if (materialization.Ownership == PartOwnership.Adopted)
                {
                    RetainedValues.Add(materialization.Value);
                    return PartReleaseResult.Retained();
                }

                DestroyedValues.Add(materialization.Value);
                return PartReleaseResult.Released();
            }

            public void CompleteBlockedPreparation(PartOwnership ownership)
            {
                _blockedPreparation.TrySetResult(new PartMaterialization(BlockedDescriptor, ownership));
            }

            public void CompleteBlockedRelease()
            {
                _blockedRelease.TrySetResult(true);
            }
        }
    }
}
