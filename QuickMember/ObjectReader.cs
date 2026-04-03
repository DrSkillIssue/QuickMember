using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
#if NET8_0_OR_GREATER
using System.Collections.Frozen;
using System.Runtime.InteropServices;
#endif

namespace QuickMember;

/// <summary>
/// Exposes a sequence of objects as a <see cref="DbDataReader"/>, enabling integration
/// with ADO.NET consumers such as <c>SqlBulkCopy</c>, <c>DataTable.Load</c>, and
/// table-valued parameters.
/// </summary>
/// <remarks>
/// <para>When <paramref name="members"/> are not specified, all public properties and fields
/// are included, ordered by <see cref="OrdinalAttribute"/> then alphabetically.</para>
/// <para>The reader uses <see cref="TypeAccessor"/> internally for high-performance
/// member access on each row.</para>
/// </remarks>
/// <example>
/// <code>
/// IEnumerable&lt;Customer&gt; customers = GetCustomers();
/// using var reader = ObjectReader.Create(customers, "Id", "Name", "Email");
/// myDataTable.Load(reader);
/// </code>
/// </example>
public class ObjectReader : DbDataReader
{
    private IEnumerator _source;
    private object _current;
    private bool _active = true;
    private readonly TypeAccessor _accessor;
    private readonly string[] _memberNames;

    private readonly Type[] _effectiveTypes;
    private readonly bool[] _allowNull;
    private readonly IDisposable _sourceDisposable;

#if NET8_0_OR_GREATER
    private FrozenDictionary<string, int> _nameOrdinals;
#else
    private Dictionary<string, int> _nameOrdinals;
#endif
    private DataTable _schemaTable;

    /// <summary>
    /// Creates an <see cref="ObjectReader"/> that reads from the specified sequence.
    /// The element type <typeparamref name="T"/> is used to resolve member metadata.
    /// </summary>
    /// <typeparam name="T">The type of objects in the sequence.</typeparam>
    /// <param name="source">The sequence of objects to expose as rows.</param>
    /// <param name="members">
    /// The member names to include as columns. If empty, all public properties and fields are included.
    /// </param>
    /// <returns>A new <see cref="ObjectReader"/> positioned before the first row.</returns>
    public static ObjectReader Create<T>(IEnumerable<T> source, params string[] members)
    {
        return new ObjectReader(typeof(T), source, members);
    }

    /// <summary>
    /// Initializes a new <see cref="ObjectReader"/> for the specified type and data source.
    /// </summary>
    /// <param name="type">The expected type of objects in <paramref name="source"/>.</param>
    /// <param name="source">The sequence of objects to expose as rows.</param>
    /// <param name="members">
    /// The member names to include as columns. If empty, all public properties and fields are included.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// No members were specified and the type does not support member discovery.
    /// </exception>
    public ObjectReader(Type type, IEnumerable source, params string[] members)
    {
        if (source == null) throw new ArgumentOutOfRangeException(nameof(source));

        bool allMembers = members == null || members.Length == 0;

        _accessor = TypeAccessor.Create(type);
        if (_accessor.GetMembersSupported)
        {
            MemberSet rawMembers = _accessor.GetMembers();
            int memberCount = rawMembers.Count;

            if (allMembers)
            {
                // Sort needed only to determine column order
                var sorted = new Member[memberCount];
                for (int j = 0; j < memberCount; j++) sorted[j] = rawMembers[j];
                if (memberCount > 1)
                    Array.Sort(sorted, static (a, b) =>
                    {
                        int cmp = a.Ordinal.CompareTo(b.Ordinal);
                        return cmp != 0 ? cmp : string.CompareOrdinal(a.Name, b.Name);
                    });
                members = new string[memberCount];
                for (int j = 0; j < memberCount; j++)
                    members[j] = sorted[j].Name;
            }

            // Build lookup from raw members — no sorted order needed
            var memberLookup = new Dictionary<string, Member>(memberCount);
            for (int j = 0; j < memberCount; j++)
            {
                Member m = rawMembers[j];
#if NET8_0_OR_GREATER
                ref Member slot = ref CollectionsMarshal.GetValueRefOrAddDefault(memberLookup, m.Name, out bool exists);
                slot = exists ? null : m;
#else
                if (memberLookup.ContainsKey(m.Name))
                    memberLookup[m.Name] = null;
                else
                    memberLookup[m.Name] = m;
#endif
            }

            _allowNull = new bool[members.Length];
            _effectiveTypes = new Type[members.Length];
            for (int j = 0; j < members.Length; j++)
            {
                Type memberType = null;
                bool isNullable = true;
                if (memberLookup.TryGetValue(members[j], out Member found) && found != null)
                {
                    Type tmp = found.Type;
                    if (tmp.IsValueType)
                    {
                        memberType = Nullable.GetUnderlyingType(tmp) ?? tmp;
                        isNullable = memberType != tmp; // true if was Nullable<T>
                    }
                    else
                    {
                        memberType = tmp;
                        // isNullable stays true for reference types
                    }
                }
                _allowNull[j] = isNullable;
                _effectiveTypes[j] = memberType ?? typeof(object);
            }
        }
        else if (allMembers)
        {
            throw new InvalidOperationException("Member information is not available for this type; the required members must be specified explicitly");
        }
        else
        {
            // No member metadata available; fill with defaults so hot-path methods avoid null checks
            var len = members.Length;
            var types = new Type[len];
            var nulls = new bool[len];
            for (int j = 0; j < len; j++)
            {
                types[j] = typeof(object);
                nulls[j] = true;
            }
            _effectiveTypes = types;
            _allowNull = nulls;
        }

        // we own the array when allMembers — skip the defensive copy
        _memberNames = allMembers ? members : (string[])members.Clone();

        IEnumerator enumerator = source.GetEnumerator();
        _source = enumerator;
        _sourceDisposable = enumerator as IDisposable;
    }

    public override int Depth
    {
        get { return 0; }
    }

    private static readonly object s_boxedFalse = false;
    private static readonly object s_boxedTrue = true;
    private static readonly object s_boxedMinusOne = -1;

    public override DataTable GetSchemaTable()
    {
        if (_schemaTable != null) return _schemaTable;
        // these are the columns used by DataTable load
        DataTable table = new DataTable
        {
            Columns =
            {
                {"ColumnOrdinal", typeof(int)},
                {"ColumnName", typeof(string)},
                {"DataType", typeof(Type)},
                {"ColumnSize", typeof(int)},
                {"AllowDBNull", typeof(bool)},
                {"IsKey", typeof(bool)}
            }
        };
        object[] rowData = new object[6];
        rowData[3] = s_boxedMinusOne;
        rowData[5] = s_boxedFalse;
        for (int i = 0; i < _memberNames.Length; i++)
        {
            rowData[0] = i;
            rowData[1] = _memberNames[i];
            rowData[2] = _effectiveTypes[i];
            rowData[4] = _allowNull[i] ? s_boxedTrue : s_boxedFalse;
            table.Rows.Add(rowData);
        }
        _schemaTable = table;
        return table;
    }
    public override void Close()
    {
        Shutdown();
    }

    public override bool HasRows
    {
        get
        {
            return _active;
        }
    }
    public override bool NextResult()
    {
        _active = false;
        return false;
    }
    public override bool Read()
    {
        if (_active)
        {
            IEnumerator tmp = _source;
            if (tmp != null && tmp.MoveNext())
            {
                _current = tmp.Current;
                return true;
            }
            _active = false;
        }
        _current = null;
        return false;
    }

    public override int RecordsAffected
    {
        get { return 0; }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Shutdown();
    }
    private void Shutdown()
    {
        _active = false;
        _current = null;
        _source = null;
        _sourceDisposable?.Dispose();
    }

    public override int FieldCount
    {
        get { return _memberNames.Length; }
    }
    public override bool IsClosed
    {
        get
        {
            return _source == null;
        }
    }

    public override bool GetBoolean(int ordinal)
    {
        return (bool)_accessor[_current, _memberNames[ordinal]];
    }

    public override byte GetByte(int ordinal)
    {
        return (byte)_accessor[_current, _memberNames[ordinal]];
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length)
    {
        byte[] s = (byte[])_accessor[_current, _memberNames[ordinal]];
        int available = s.Length - (int)dataOffset;
        if (available <= 0) return 0;

        int count = Math.Min(length, available);
#if NET8_0_OR_GREATER
        s.AsSpan((int)dataOffset, count).CopyTo(buffer.AsSpan(bufferOffset, count));
#else
        Buffer.BlockCopy(s, (int)dataOffset, buffer, bufferOffset, count);
#endif
        return count;
    }

    public override char GetChar(int ordinal)
    {
        return (char)_accessor[_current, _memberNames[ordinal]];
    }

    public override long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length)
    {
        string s = (string)_accessor[_current, _memberNames[ordinal]];
        int available = s.Length - (int)dataOffset;
        if (available <= 0) return 0;

        int count = Math.Min(length, available);
#if NET8_0_OR_GREATER
        s.AsSpan((int)dataOffset, count).CopyTo(buffer.AsSpan(bufferOffset, count));
#else
        s.CopyTo((int)dataOffset, buffer, bufferOffset, count);
#endif
        return count;
    }

    protected override DbDataReader GetDbDataReader(int ordinal)
    {
        throw new NotSupportedException();
    }

    public override string GetDataTypeName(int ordinal)
    {
        return _effectiveTypes[ordinal].Name;
    }

    public override DateTime GetDateTime(int ordinal)
    {
        return (DateTime)_accessor[_current, _memberNames[ordinal]];
    }

    public override decimal GetDecimal(int ordinal)
    {
        return (decimal)_accessor[_current, _memberNames[ordinal]];
    }

    public override double GetDouble(int ordinal)
    {
        return (double)_accessor[_current, _memberNames[ordinal]];
    }

    public override Type GetFieldType(int ordinal)
    {
        return _effectiveTypes[ordinal];
    }

    public override float GetFloat(int ordinal)
    {
        return (float)_accessor[_current, _memberNames[ordinal]];
    }

    public override Guid GetGuid(int ordinal)
    {
        return (Guid)_accessor[_current, _memberNames[ordinal]];
    }

    public override short GetInt16(int ordinal)
    {
        return (short)_accessor[_current, _memberNames[ordinal]];
    }

    public override int GetInt32(int ordinal)
    {
        return (int)_accessor[_current, _memberNames[ordinal]];
    }

    public override long GetInt64(int ordinal)
    {
        return (long)_accessor[_current, _memberNames[ordinal]];
    }

    public override string GetName(int ordinal)
    {
        return _memberNames[ordinal];
    }

    public override int GetOrdinal(string name)
    {
        var lookup = _nameOrdinals;
        if (lookup == null)
        {
            var dict = new Dictionary<string, int>(_memberNames.Length);
            for (int i = 0; i < _memberNames.Length; i++)
                dict[_memberNames[i]] = i;
#if NET8_0_OR_GREATER
            lookup = dict.ToFrozenDictionary();
#else
            lookup = dict;
#endif
            _nameOrdinals = lookup;
        }
        return lookup.TryGetValue(name, out int ordinal) ? ordinal : -1;
    }

    public override string GetString(int ordinal)
    {
        return (string)_accessor[_current, _memberNames[ordinal]];
    }

    public override object GetValue(int ordinal)
    {
        return _accessor[_current, _memberNames[ordinal]] ?? DBNull.Value;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    public override int GetValues(object[] values)
    {
        // duplicate the key fields on the stack
        var members = _memberNames;
        var current = _current;
        TypeAccessor accessor = _accessor;
        DBNull dbNull = DBNull.Value;

        int count = Math.Min(values.Length, members.Length);
        for (int i = 0; i < count; i++) values[i] = accessor[current, members[i]] ?? dbNull;
        return count;
    }

    public override bool IsDBNull(int ordinal)
    {
        return _accessor[_current, _memberNames[ordinal]] == null;
    }

    /// <summary>
    /// Gets the value of the named member on the current row.
    /// Returns <see cref="DBNull.Value"/> if the underlying value is <c>null</c>.
    /// </summary>
    /// <param name="name">The member name.</param>
    public override object this[string name]
    {
        get { return _accessor[_current, name] ?? DBNull.Value; }
    }

    /// <summary>
    /// Gets the value of the member at the specified column ordinal on the current row.
    /// Returns <see cref="DBNull.Value"/> if the underlying value is <c>null</c>.
    /// </summary>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    public override object this[int ordinal]
    {
        get { return _accessor[_current, _memberNames[ordinal]] ?? DBNull.Value; }
    }
}
