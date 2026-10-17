using System;

namespace Immersive.Composition
{
    public readonly struct CompositionContextId : IEquatable<CompositionContextId>
    {
        public Guid Value { get; }
        public bool IsValid => Value != Guid.Empty;

        internal CompositionContextId(Guid value)
        {
            Value = value;
        }

        public bool Equals(CompositionContextId other) => Value.Equals(other.Value);
        public override bool Equals(object obj) => obj is CompositionContextId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString("N");
        public static bool operator ==(CompositionContextId left, CompositionContextId right) => left.Equals(right);
        public static bool operator !=(CompositionContextId left, CompositionContextId right) => !left.Equals(right);
    }
}
