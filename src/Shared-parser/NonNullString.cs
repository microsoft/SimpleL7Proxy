namespace SharedParser;

/// <summary>A string value that cannot be null. The default value represents an empty string.</summary>
/// NO need to check for null when using this type, as it guarantees a non-null string.
public readonly struct NonNullString : IEquatable<NonNullString> {
    private readonly string? _value;

    /// <summary>Creates a non-null string value, rejecting null input.</summary>
    public NonNullString(string value) {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    /// <summary>Gets the non-null string, including an empty string for the default value.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Returns the non-null string value.</summary>
    public override string ToString() => Value;

    /// <summary>Compares string values using ordinal equality.</summary>
    public bool Equals(NonNullString other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <summary>Compares this value with another non-null string value.</summary>
    public override bool Equals(object? obj) => obj is NonNullString other && Equals(other);

    /// <summary>Returns the ordinal hash code of the string value.</summary>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>Compares two string values using ordinal equality.</summary>
    public static bool operator ==(NonNullString left, NonNullString right) => left.Equals(right);

    /// <summary>Determines whether two string values differ using ordinal equality.</summary>
    public static bool operator !=(NonNullString left, NonNullString right) => !left.Equals(right);
}