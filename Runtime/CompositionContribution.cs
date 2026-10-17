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
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            return _composer.SetPartsAsync(this, new[] { definition }, cancellationToken);
        }

        public Task<CompositionResult> SetPartsAsync(
            IEnumerable<PartDefinition> definitions,
            CancellationToken cancellationToken = default)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions));
            }

            return _composer.SetPartsAsync(this, definitions, cancellationToken);
        }

        public Task<CompositionResult> RemovePartAsync(
            ContentKey key,
            CancellationToken cancellationToken = default)
        {
            return _composer.RemovePartAsync(this, key, cancellationToken);
        }

        public Task<CompositionResult> CloseAsync(CancellationToken cancellationToken = default)
        {
            return _composer.CloseContributionAsync(this, cancellationToken);
        }
    }
}
