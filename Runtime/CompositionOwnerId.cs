using System;

namespace Immersive.Composition
{
    public readonly struct CompositionOwnerId : IEquatable<CompositionOwnerId>, IComparable<CompositionOwnerId>
    {
        public string Value { get; }
        public bool IsValid => !string.IsNullOrEmpty(Value);

        private CompositionOwnerId(string value)
        {
            Value = value;
        }

        public static CompositionOwnerId Create(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("A composition owner ID must be non-empty and have no surrounding whitespace.", nameof(value));
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsControl(value[i]))
                {
                    throw new ArgumentException("A composition owner ID cannot contain control characters.", nameof(value));
                }
            }

            return new CompositionOwnerId(value);
        }

        public bool Equals(CompositionOwnerId other)
        {
            return string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is CompositionOwnerId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        }

        public int CompareTo(CompositionOwnerId other)
        {
            return string.Compare(Value, other.Value, StringComparison.Ordinal);
        }

        public override string ToString()
        {
            return Value ?? string.Empty;
        }

        public static bool operator ==(CompositionOwnerId left, CompositionOwnerId right) => left.Equals(right);
        public static bool operator !=(CompositionOwnerId left, CompositionOwnerId right) => !left.Equals(right);
    }
}
