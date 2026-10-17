using System;

namespace Immersive.Composition
{
    public readonly struct CompositionOperationId : IEquatable<CompositionOperationId>
    {
        public Guid Value { get; }
        public bool IsValid => Value != Guid.Empty;

        internal CompositionOperationId(Guid value)
        {
            Value = value;
        }

        public bool Equals(CompositionOperationId other) => Value.Equals(other.Value);
        public override bool Equals(object obj) => obj is CompositionOperationId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString("N");
        public static bool operator ==(CompositionOperationId left, CompositionOperationId right) => left.Equals(right);
        public static bool operator !=(CompositionOperationId left, CompositionOperationId right) => !left.Equals(right);
    }
}
