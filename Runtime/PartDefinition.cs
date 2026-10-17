using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Immersive.Composition
{
    public sealed class PartDefinition
    {
        private readonly ReadOnlyCollection<ContentKey> _dependencies;

        public ContentKey Key { get; }
        public object Descriptor { get; }
        public IPartMaterializer Materializer { get; }
        public IReadOnlyList<ContentKey> Dependencies => _dependencies;

        public PartDefinition(
            ContentKey key,
            object descriptor,
            IPartMaterializer materializer,
            IEnumerable<ContentKey> dependencies = null)
        {
            if (!key.IsValid)
            {
                throw new ArgumentException("A part definition requires a valid content key.", nameof(key));
            }

            Key = key;
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));

            var dependencyList = new List<ContentKey>();
            var seen = new HashSet<ContentKey>();
            if (dependencies != null)
            {
                foreach (ContentKey dependency in dependencies)
                {
                    if (!dependency.IsValid)
                    {
                        throw new ArgumentException("Part dependencies must be valid content keys.", nameof(dependencies));
                    }

                    if (!seen.Add(dependency))
                    {
                        throw new ArgumentException("Part dependencies cannot contain duplicates.", nameof(dependencies));
                    }

                    dependencyList.Add(dependency);
                }
            }

            _dependencies = Array.AsReadOnly(dependencyList.ToArray());
        }
    }
}
