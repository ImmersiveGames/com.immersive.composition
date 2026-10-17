using System;

namespace Immersive.Composition
{
    public readonly struct ContentKey : IEquatable<ContentKey>, IComparable<ContentKey>
    {
        public string Value { get; }
        public bool IsValid => !string.IsNullOrEmpty(Value);

        private ContentKey(string value)
        {
            Value = value;
        }

        public static ContentKey Create(string value)
        {
            if (!TryCreate(value, out ContentKey key))
            {
                throw new ArgumentException(
                    "A content key must be non-empty, have no surrounding whitespace, and contain no whitespace or control characters.",
                    nameof(value));
            }

            return key;
        }

        public static bool TryCreate(string value, out ContentKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsWhiteSpace(value[i]) || char.IsControl(value[i]))
                {
                    return false;
                }
            }

            key = new ContentKey(value);
            return true;
        }

        public bool Equals(ContentKey other)
        {
            return string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is ContentKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        }

        public int CompareTo(ContentKey other)
        {
            return string.Compare(Value, other.Value, StringComparison.Ordinal);
        }

        public override string ToString()
        {
            return Value ?? string.Empty;
        }

        public static bool operator ==(ContentKey left, ContentKey right) => left.Equals(right);
        public static bool operator !=(ContentKey left, ContentKey right) => !left.Equals(right);
    }
}
