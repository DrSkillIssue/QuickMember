using System;
using System.Dynamic;
using System.Runtime.CompilerServices;

namespace QuickMember;

/// <summary>
/// Provides by-name member access to a specific object instance.
/// For access patterns involving many objects of the same type, prefer
/// <see cref="TypeAccessor"/> which amortizes setup cost across all instances.
/// </summary>
/// <example>
/// <code>
/// var obj = new MyType { Name = "abc" };
/// var accessor = ObjectAccessor.Create(obj);
/// string name = (string)accessor["Name"];
/// accessor["Name"] = "def";
/// </code>
/// </example>
public abstract class ObjectAccessor
{
    /// <summary>
    /// Gets or sets the value of the named member on the wrapped object.
    /// </summary>
    /// <param name="name">The name of the property or field to access.</param>
    /// <returns>The value of the member.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="name"/> does not correspond to a known member.</exception>
    public abstract object this[string name] { get; set; }

    /// <summary>
    /// Gets the underlying object represented by this accessor.
    /// </summary>
    public abstract object Target { get; }

    /// <inheritdoc/>
    public override bool Equals(object obj) => Target.Equals(obj);

    /// <inheritdoc/>
    public override int GetHashCode() => Target.GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => Target.ToString();

    /// <summary>
    /// Wraps an individual object, allowing by-name access to its public members.
    /// </summary>
    /// <param name="target">The object to wrap.</param>
    /// <returns>An <see cref="ObjectAccessor"/> bound to <paramref name="target"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <c>null</c>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ObjectAccessor Create(object target) => Create(target, false);

    /// <summary>
    /// Wraps an individual object, allowing by-name access to its members.
    /// </summary>
    /// <param name="target">The object to wrap.</param>
    /// <param name="allowNonPublicAccessors">
    /// If <c>true</c>, non-public (internal/private) properties and their getters/setters
    /// are accessible through the returned accessor.
    /// </param>
    /// <returns>An <see cref="ObjectAccessor"/> bound to <paramref name="target"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <c>null</c>.</exception>
    public static ObjectAccessor Create(object target, bool allowNonPublicAccessors)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (target is IDynamicMetaObjectProvider dlr) return new DynamicWrapper(dlr);
        return new TypeAccessorWrapper(target, TypeAccessor.Create(target.GetType(), allowNonPublicAccessors));
    }

    sealed class TypeAccessorWrapper : ObjectAccessor
    {
        private readonly object _target;
        private readonly TypeAccessor _accessor;
        public TypeAccessorWrapper(object target, TypeAccessor accessor)
        {
            _target = target;
            _accessor = accessor;
        }
        public override object this[string name]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _accessor[_target, name];
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _accessor[_target, name] = value;
        }
        public override object Target => _target;
    }

    sealed class DynamicWrapper : ObjectAccessor
    {
        private readonly IDynamicMetaObjectProvider _target;
        public DynamicWrapper(IDynamicMetaObjectProvider target) => _target = target;
        public override object Target => _target;
        public override object this[string name]
        {
            get => CallSiteCache.GetValue(name, _target);
            set => CallSiteCache.SetValue(name, _target, value);
        }
    }
}
