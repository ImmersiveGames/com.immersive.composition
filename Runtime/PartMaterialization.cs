using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Immersive.Composition
{
    public enum PartOwnership
    {
        Acquired = 0,
        Adopted = 1
    }

    public sealed class PartMaterialization
    {
        public object Value { get; }
        public PartOwnership Ownership { get; }
        public object Provenance { get; }

        public PartMaterialization(object value, PartOwnership ownership, object provenance = null)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            if (!Enum.IsDefined(typeof(PartOwnership), ownership))
            {
                throw new ArgumentOutOfRangeException(nameof(ownership));
            }

            Ownership = ownership;
            Provenance = provenance;
        }
    }

    public sealed class PartMaterializationContext
    {
        private readonly ReadOnlyCollection<ContentKey> _affectedKeys;

        public CompositionContextId ContextId { get; }
        public CompositionOperationId OperationId { get; }
        public CompositionOwnerId ContributionOwnerId { get; }
        public ContentKey Key { get; }
        public long ObservedRevision { get; }
        public IReadOnlyList<ContentKey> AffectedKeys => _affectedKeys;

        internal PartMaterializationContext(
            CompositionContextId contextId,
            CompositionOperationId operationId,
            CompositionOwnerId contributionOwnerId,
            ContentKey key,
            long observedRevision,
            IReadOnlyCollection<ContentKey> affectedKeys)
        {
            ContextId = contextId;
            OperationId = operationId;
            ContributionOwnerId = contributionOwnerId;
            Key = key;
            ObservedRevision = observedRevision;

            var keys = new List<ContentKey>(affectedKeys);
            _affectedKeys = Array.AsReadOnly(keys.ToArray());
        }
    }

    public interface IPartMaterializer
    {
        System.Threading.Tasks.Task<PartPreparationResult> PrepareAsync(
            PartDefinition definition,
            PartMaterializationContext context,
            System.Threading.CancellationToken cancellationToken);

        PartEffectResult Apply(
            PartMaterialization candidate,
            PartMaterialization previous,
            PartMaterializationContext context);

        PartEffectResult Detach(
            PartMaterialization current,
            PartMaterializationContext context);

        System.Threading.Tasks.Task<PartReleaseResult> ReleaseAsync(
            PartMaterialization materialization,
            PartMaterializationContext context,
            System.Threading.CancellationToken cancellationToken);
    }

    public sealed class PartPreparationResult
    {
        public bool Succeeded { get; }
        public PartMaterialization Materialization { get; }
        public string Issue { get; }

        private PartPreparationResult(bool succeeded, PartMaterialization materialization, string issue)
        {
            Succeeded = succeeded;
            Materialization = materialization;
            Issue = issue ?? string.Empty;
        }

        public static PartPreparationResult Prepared(PartMaterialization materialization)
        {
            return new PartPreparationResult(
                materialization != null,
                materialization,
                materialization == null ? "The materializer returned no part." : string.Empty);
        }

        public static PartPreparationResult Failed(string issue)
        {
            return new PartPreparationResult(false, null, issue);
        }
    }

    public sealed class PartEffectResult
    {
        public bool Succeeded { get; }
        public bool EffectsMayHaveApplied { get; }
        public string Issue { get; }

        private PartEffectResult(bool succeeded, bool effectsMayHaveApplied, string issue)
        {
            Succeeded = succeeded;
            EffectsMayHaveApplied = effectsMayHaveApplied;
            Issue = issue ?? string.Empty;
        }

        public static PartEffectResult Applied()
        {
            return new PartEffectResult(true, true, string.Empty);
        }

        public static PartEffectResult Failed(string issue, bool effectsMayHaveApplied)
        {
            return new PartEffectResult(false, effectsMayHaveApplied, issue);
        }
    }

    public enum PartReleaseOutcome
    {
        Released = 0,
        Retained = 1,
        Pending = 2,
        Failed = 3
    }

    public sealed class PartReleaseResult
    {
        public PartReleaseOutcome Outcome { get; }
        public string Issue { get; }

        private PartReleaseResult(PartReleaseOutcome outcome, string issue)
        {
            Outcome = outcome;
            Issue = issue ?? string.Empty;
        }

        public static PartReleaseResult Released() => new PartReleaseResult(PartReleaseOutcome.Released, string.Empty);
        public static PartReleaseResult Retained() => new PartReleaseResult(PartReleaseOutcome.Retained, string.Empty);
        public static PartReleaseResult Pending(string issue = null) => new PartReleaseResult(PartReleaseOutcome.Pending, issue);
        public static PartReleaseResult Failed(string issue) => new PartReleaseResult(PartReleaseOutcome.Failed, issue);
    }
}
