namespace QuickMember;

/// <summary>
/// Specifies the column ordinal position of a property or field when used with
/// <see cref="ObjectReader"/> or other <see cref="System.Data.IDataReader"/> scenarios.
/// Members decorated with this attribute will be ordered by their ordinal value
/// when determining column positions.
/// </summary>
/// <example>
/// <code>
/// public class Customer
/// {
///     [Ordinal(0)]
///     public int Id { get; set; }
///
///     [Ordinal(1)]
///     public string Name { get; set; }
/// }
/// </code>
/// </example>
[System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Field, AllowMultiple = false)]
public sealed class OrdinalAttribute : System.Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OrdinalAttribute"/> class
    /// with the specified column ordinal.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal position.</param>
    public OrdinalAttribute(ushort ordinal)
    {
        Ordinal = ordinal;
    }

    /// <summary>
    /// Gets the zero-based column ordinal position.
    /// </summary>
    public ushort Ordinal { get; }
}
