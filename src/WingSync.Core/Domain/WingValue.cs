using System.Globalization;

namespace WingSync.Core.Domain;

/// <summary>
/// Identifies the native WING value type used on the remote protocol.
/// </summary>
public enum WingValueType
{
    /// <summary>An integer value (<c>I</c>).</summary>
    I,

    /// <summary>A single-precision floating-point value (<c>F</c>).</summary>
    F,

    /// <summary>A UTF-8 string or enumerated value (<c>S</c>).</summary>
    S,
}

/// <summary>
/// Represents one strongly typed WING protocol value.
/// </summary>
/// <remarks>
/// The inactive backing fields are deliberately kept at their default value. Use one of
/// the factory methods and the matching <c>As...</c> accessor instead of coercing values.
/// </remarks>
public readonly record struct WingValue
{
    private readonly int integerValue;
    private readonly float floatValue;
    private readonly string? stringValue;

    private WingValue(WingValueType type, int integerValue, float floatValue, string? stringValue)
    {
        Type = type;
        this.integerValue = integerValue;
        this.floatValue = floatValue;
        this.stringValue = stringValue;
    }

    /// <summary>Gets the native protocol type.</summary>
    public WingValueType Type { get; }

    /// <summary>Creates an integer (<c>I</c>) value.</summary>
    /// <param name="value">The integer payload.</param>
    /// <returns>A typed WING value.</returns>
    public static WingValue FromInt32(int value) => new(WingValueType.I, value, default, default);

    /// <summary>Creates a floating-point (<c>F</c>) value.</summary>
    /// <param name="value">The single-precision payload.</param>
    /// <returns>A typed WING value.</returns>
    public static WingValue FromFloat(float value) => new(WingValueType.F, default, value, default);

    /// <summary>Creates a string (<c>S</c>) value.</summary>
    /// <param name="value">The non-null string payload.</param>
    /// <returns>A typed WING value.</returns>
    public static WingValue FromString(string value) =>
        new(WingValueType.S, default, default, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Returns the integer payload.</summary>
    /// <returns>The integer payload.</returns>
    /// <exception cref="InvalidOperationException">The value is not an integer.</exception>
    public int AsInt32() =>
        Type == WingValueType.I
            ? integerValue
            : throw new InvalidOperationException($"A {Type} WING value does not contain an integer.");

    /// <summary>Returns the floating-point payload.</summary>
    /// <returns>The floating-point payload.</returns>
    /// <exception cref="InvalidOperationException">The value is not a floating-point value.</exception>
    public float AsFloat() =>
        Type == WingValueType.F
            ? floatValue
            : throw new InvalidOperationException($"A {Type} WING value does not contain a float.");

    /// <summary>Returns the string payload.</summary>
    /// <returns>The string payload.</returns>
    /// <exception cref="InvalidOperationException">The value is not a string.</exception>
    public string AsString() =>
        Type == WingValueType.S
            ? stringValue!
            : throw new InvalidOperationException($"A {Type} WING value does not contain a string.");

    /// <inheritdoc />
    public override string ToString() =>
        Type switch
        {
            WingValueType.I => integerValue.ToString(CultureInfo.InvariantCulture),
            WingValueType.F => floatValue.ToString("R", CultureInfo.InvariantCulture),
            WingValueType.S => stringValue ?? string.Empty,
            _ => string.Empty,
        };
}
