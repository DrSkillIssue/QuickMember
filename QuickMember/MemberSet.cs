using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace QuickMember;

/// <summary>
/// Represents a read-only, ordered collection of <see cref="Member"/> instances
/// describing the public properties and fields of a type. Obtained via
/// <see cref="TypeAccessor.GetMembers"/>.
/// </summary>
/// <remarks>
/// Members are sorted alphabetically by name. The collection is immutable;
/// mutation methods from <see cref="IList{T}"/> throw <see cref="NotSupportedException"/>.
/// Iterating with <c>foreach</c> on the concrete type uses a zero-allocation
/// <see cref="Enumerator"/> struct.
/// </remarks>
public sealed class MemberSet : IEnumerable<Member>, IList<Member>
{
    readonly Member[] _members;
    internal MemberSet(Type type)
    {
        const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        PropertyInfo[] props = type.GetTypeAndInterfaceProperties(PublicInstance);
        FieldInfo[] fields = type.GetFields(PublicInstance);

        int total = props.Length + fields.Length;

        _members = new Member[total];
        for (int i = 0; i < props.Length; i++)
            _members[i] = new Member(props[i]);

        for (int i = 0; i < fields.Length; i++)
            _members[props.Length + i] = new Member(fields[i]);

        if (total > 1)
            Array.Sort(_members, static (x, y) => string.CompareOrdinal(x.Name, y.Name));
    }

    /// <summary>
    /// Returns a zero-allocation struct enumerator over the members.
    /// </summary>
    /// <returns>A <see cref="Enumerator"/> that iterates over the member collection.</returns>
    public Enumerator GetEnumerator() => new Enumerator(_members);
    IEnumerator<Member> IEnumerable<Member>.GetEnumerator() => ((IEnumerable<Member>)_members).GetEnumerator();

    /// <summary>
    /// A value-type enumerator that avoids heap allocation when iterating
    /// a <see cref="MemberSet"/> with <c>foreach</c>.
    /// </summary>
    public struct Enumerator : IDisposable
    {
        private readonly Member[] _members;
        private int _index;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(Member[] members) { _members = members; _index = -1; }

        /// <summary>Gets the member at the current position of the enumerator.</summary>
        public readonly Member Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _members[_index];
        }

        /// <summary>Advances the enumerator to the next member.</summary>
        /// <returns><c>true</c> if the enumerator was successfully advanced; <c>false</c> if past the end.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => ++_index < _members.Length;
        readonly void IDisposable.Dispose() { }
    }

    /// <summary>
    /// Gets the member at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the member.</param>
    /// <returns>The <see cref="Member"/> at <paramref name="index"/>.</returns>
    public Member this[int index]
    {
        get { return _members[index]; }
    }

    /// <summary>
    /// Gets the number of members in this set.
    /// </summary>
    public int Count { get { return _members.Length; } }

    Member IList<Member>.this[int index]
    {
        get { return _members[index]; }
        set { throw new NotSupportedException(); }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return _members.GetEnumerator(); }
    bool ICollection<Member>.Remove(Member item) { throw new NotSupportedException(); }
    void ICollection<Member>.Add(Member item) { throw new NotSupportedException(); }
    void ICollection<Member>.Clear() { throw new NotSupportedException(); }
    void IList<Member>.RemoveAt(int index) { throw new NotSupportedException(); }
    void IList<Member>.Insert(int index, Member item) { throw new NotSupportedException(); }

    bool ICollection<Member>.Contains(Member item) => Array.IndexOf(_members, item) >= 0;
    void ICollection<Member>.CopyTo(Member[] array, int arrayIndex) { _members.CopyTo(array, arrayIndex); }
    bool ICollection<Member>.IsReadOnly { get { return true; } }
    int IList<Member>.IndexOf(Member member) { return Array.IndexOf(_members, member); }
}

/// <summary>
/// Represents metadata about a single property or field on a type,
/// including its name, type, ordinal, and read/write capabilities.
/// </summary>
public sealed class Member
{
    private readonly MemberInfo _member;
    private readonly bool _isProperty;

    internal Member(MemberInfo member)
    {
        _member = member;
        Name = member.Name;
        if (member is PropertyInfo pi)
        {
            Type = pi.PropertyType;
            IsIndexer = pi.GetIndexParameters().Length > 0;
            _isProperty = true;
            CanRead = pi.CanRead;
            CanWrite = pi.CanWrite;
        }
        else
        {
            Type = ((FieldInfo)member).FieldType;
            IsIndexer = false;
            _isProperty = false;
            CanRead = true;
            CanWrite = true;
        }

        var ordAttr = (OrdinalAttribute)Attribute.GetCustomAttribute(member, typeof(OrdinalAttribute));
        Ordinal = ordAttr != null ? ordAttr.Ordinal : -1;
    }

    /// <summary>
    /// Gets the column ordinal specified by <see cref="OrdinalAttribute"/>,
    /// or <c>-1</c> if no ordinal attribute is present.
    /// </summary>
    public int Ordinal { get; }

    /// <summary>
    /// Gets the name of this property or field.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the declared type of this property or field.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Determines whether a custom attribute of the specified type is defined on this member.
    /// </summary>
    /// <param name="attributeType">The type of attribute to search for.</param>
    /// <returns><c>true</c> if an attribute of <paramref name="attributeType"/> is defined; otherwise <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attributeType"/> is <c>null</c>.</exception>
    public bool IsDefined(Type attributeType)
    {
        if (attributeType == null) throw new ArgumentNullException(nameof(attributeType));
        return Attribute.IsDefined(_member, attributeType);
    }

    /// <summary>
    /// Retrieves a custom attribute of the specified type from this member.
    /// </summary>
    /// <param name="attributeType">The type of attribute to retrieve.</param>
    /// <param name="inherit"><c>true</c> to search the member's inheritance chain; otherwise <c>false</c>.</param>
    /// <returns>The matching <see cref="Attribute"/>, or <c>null</c> if not found.</returns>
    public Attribute GetAttribute(Type attributeType, bool inherit)
        => Attribute.GetCustomAttribute(_member, attributeType, inherit);

    /// <summary>
    /// Gets a value indicating whether this property has a setter.
    /// </summary>
    /// <exception cref="NotSupportedException">The member is not a property.</exception>
    public bool CanWrite
    {
        get
        {
            if (!_isProperty) throw new NotSupportedException(_member.MemberType.ToString());
            return field;
        }
    }

    /// <summary>
    /// Gets a value indicating whether this member is an indexer property
    /// (i.e. has index parameters such as <c>this[int index]</c>).
    /// Returns <c>false</c> for fields and non-indexer properties.
    /// </summary>
    public bool IsIndexer { get; }

    /// <summary>
    /// Gets a value indicating whether this property has a getter.
    /// </summary>
    /// <exception cref="NotSupportedException">The member is not a property.</exception>
    public bool CanRead
    {
        get
        {
            if (!_isProperty) throw new NotSupportedException(_member.MemberType.ToString());
            return field;
        }
    }
}
