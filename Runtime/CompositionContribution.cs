using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Immersive.Composition
{
    public sealed class CompositionContribution
    {
        private readonly CompositionComposer _composer;

        public CompositionOwnerId OwnerId { get; }
        public int Priority { get; }

        internal CompositionContribution(CompositionComposer composer, CompositionOwnerId ownerId, int priority)
        {
            _composer = composer ?? throw new ArgumentNullException(nameof(composer));
            OwnerId = ownerId;
            Priority = priority;
        }

        public Task<CompositionResult> SetPartAsync(
            PartDefinition definition,
            CancellationToken cancellationToken = default)
        {
            return SetPartWhileCurrentAsync(definition, null, cancellationToken);
        }

        /// <summary>
        /// Sets a part only while the caller's external target remains current. The guard is
        /// rechecked after preparation and throughout effects; guarded winners in the affected
        /// dependency closure are checked as well.
        /// </summary>
        public Task<CompositionResult> SetPartWhileCurrentAsync(
            PartDefinition definition,
            Func<bool> isTargetCurrent,
            CancellationToken cancellationToken = default)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            return _composer.SetPartsAsync(this, new[] { definition }, isTargetCurrent, cancellationToken);
        }

        public Task<CompositionResult> SetPartsAsync(
            IEnumerable<PartDefinition> definitions,
            CancellationToken cancellationToken = default)
        {
            return SetPartsWhileCurrentAsync(definitions, null, cancellationToken);
        }

        /// <summary>
        /// Sets parts only while the caller's external target remains current. The guard is
        /// rechecked after preparation and throughout effects; guarded winners in the affected
        /// dependency closure are checked as well.
        /// </summary>
        public Task<CompositionResult> SetPartsWhileCurrentAsync(
            IEnumerable<PartDefinition> definitions,
            Func<bool> isTargetCurrent,
            CancellationToken cancellationToken = default)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions));
            }

            return _composer.SetPartsAsync(this, definitions, isTargetCurrent, cancellationToken);
        }

        public Task<CompositionResult> RemovePartAsync(
            ContentKey key,
            CancellationToken cancellationToken = default)
        {
            return RemovePartWhileCurrentAsync(key, null, cancellationToken);
        }

        /// <summary>Removes a part only while the caller's external target remains current.</summary>
        public Task<CompositionResult> RemovePartWhileCurrentAsync(
            ContentKey key,
            Func<bool> isTargetCurrent,
            CancellationToken cancellationToken = default)
        {
            return _composer.RemovePartAsync(this, key, isTargetCurrent, cancellationToken);
        }

        public Task<CompositionResult> CloseAsync(CancellationToken cancellationToken = default)
        {
            return _composer.CloseContributionAsync(this, cancellationToken);
        }
    }
}
