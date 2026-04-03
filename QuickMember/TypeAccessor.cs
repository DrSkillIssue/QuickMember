using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
#if NET8_0_OR_GREATER
using System.Collections.Frozen;
#endif

namespace QuickMember;

/// <summary>
/// Provides high-performance, by-name member access to objects of a given type.
/// Uses runtime IL generation to produce accessors that approach the speed of
/// direct compiled property access, while resolving member names at runtime.
/// </summary>
/// <remarks>
/// <para>Accessors are cached internally per type; calling <see cref="Create(Type)"/>
/// multiple times for the same type returns the same instance.</para>
/// <para>For dynamic (<see cref="System.Dynamic.IDynamicMetaObjectProvider"/>) objects,
/// a DLR-based accessor is used automatically.</para>
/// </remarks>
/// <example>
/// <code>
/// var accessor = TypeAccessor.Create(typeof(MyType));
/// var obj = accessor.CreateNew();
/// accessor[obj, "Name"] = "hello";
/// string name = (string)accessor[obj, "Name"];
/// </code>
/// </example>
public abstract class TypeAccessor
{
    private static readonly Hashtable s_publicAccessorsOnly = new Hashtable(), s_nonPublicAccessors = new Hashtable();

    /// <summary>
    /// Gets a value indicating whether this type supports creating new instances
    /// via <see cref="CreateNew"/>. Returns <c>true</c> when the type has a
    /// public parameterless constructor.
    /// </summary>
    public virtual bool CreateNewSupported { get { return false; } }

    /// <summary>
    /// Creates a new instance of the type represented by this accessor.
    /// </summary>
    /// <returns>A new instance of the target type.</returns>
    /// <exception cref="NotSupportedException">The type does not have a public parameterless constructor.</exception>
    public virtual object CreateNew() { throw new NotSupportedException(); }

    /// <summary>
    /// Gets a value indicating whether <see cref="GetMembers"/> is supported.
    /// Returns <c>false</c> for dynamic objects.
    /// </summary>
    public virtual bool GetMembersSupported { get { return false; } }

    /// <summary>
    /// Returns the set of members (properties and fields) available on this type.
    /// </summary>
    /// <returns>A <see cref="MemberSet"/> describing the type's members.</returns>
    /// <exception cref="NotSupportedException"><see cref="GetMembersSupported"/> is <c>false</c>.</exception>
    public virtual MemberSet GetMembers() { throw new NotSupportedException(); }

    /// <summary>
    /// Creates or retrieves a cached <see cref="TypeAccessor"/> for the specified type,
    /// providing by-name access to its public members.
    /// </summary>
    /// <param name="type">The type to create an accessor for.</param>
    /// <returns>A <see cref="TypeAccessor"/> for <paramref name="type"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <c>null</c>.</exception>
    public static TypeAccessor Create(Type type)
    {
        return Create(type, false);
    }

    /// <summary>
    /// Creates or retrieves a cached <see cref="TypeAccessor"/> for the specified type.
    /// </summary>
    /// <param name="type">The type to create an accessor for.</param>
    /// <param name="allowNonPublicAccessors">
    /// If <c>true</c>, non-public (internal/private) properties and their getters/setters
    /// are included in the accessor. Fields are always public-only.
    /// </param>
    /// <returns>A <see cref="TypeAccessor"/> for <paramref name="type"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <c>null</c>.</exception>
    public static TypeAccessor Create(Type type, bool allowNonPublicAccessors)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        Hashtable lookup = allowNonPublicAccessors ? s_nonPublicAccessors : s_publicAccessorsOnly;
        TypeAccessor obj = (TypeAccessor)lookup[type];
        if (obj != null) return obj;

        lock (lookup)
        {
            // double-check
            obj = (TypeAccessor)lookup[type];
            if (obj != null) return obj;

            obj = CreateNew(type, allowNonPublicAccessors);

            lookup[type] = obj;
            return obj;
        }
    }
    private sealed class DynamicAccessor : TypeAccessor
    {
        public static readonly DynamicAccessor Singleton = new DynamicAccessor();
        private DynamicAccessor() { }
        public override object this[object target, string name]
        {
            get { return CallSiteCache.GetValue(name, target); }
            set { CallSiteCache.SetValue(name, target, value); }
        }
    }

    private static AssemblyBuilder s_assembly;
    private static ModuleBuilder s_module;
#if NET9_0_OR_GREATER
    private static readonly Lock s_assemblyLock = new();
#else
    private static readonly object s_assemblyLock = new();
#endif
    private static int s_counter;

    private static int GetNextCounterValue()
    {
        return Interlocked.Increment(ref s_counter);
    }

    private static readonly ConstructorInfo s_argOutOfRangeCtor = typeof(ArgumentOutOfRangeException).GetConstructor(new Type[] { typeof(string) });
    private static readonly TypeAttributes s_baseTypeAttributes = typeof(TypeAccessor).Attributes;
    private static readonly PropertyInfo s_indexerProp = typeof(TypeAccessor).GetProperty("Item");
    private static readonly MethodInfo s_indexerGetter = s_indexerProp.GetGetMethod();
    private static readonly MethodInfo s_indexerSetter = s_indexerProp.GetSetMethod();
    private static readonly MethodInfo s_createNewSupportedGetter = typeof(TypeAccessor).GetProperty("CreateNewSupported").GetGetMethod();
    private static readonly MethodInfo s_createNewMethod = typeof(TypeAccessor).GetMethod("CreateNew");
    private static readonly MethodInfo s_typePropertyGetter = typeof(RuntimeTypeAccessor).GetProperty("Type", BindingFlags.NonPublic | BindingFlags.Instance).GetGetMethod(true);
    private static readonly ConstructorInfo s_runtimeTypeAccessorCtor = typeof(RuntimeTypeAccessor).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
    private static readonly MethodInfo s_getTypeFromHandle = typeof(Type).GetMethod("GetTypeFromHandle");
#if NET8_0_OR_GREATER
    private static readonly MethodInfo s_tryGetValue = typeof(FrozenDictionary<string, int>).GetMethod("TryGetValue");
#else
    private static readonly MethodInfo s_tryGetValue = typeof(Dictionary<string, int>).GetMethod("TryGetValue");
#endif
    private static void WriteMapImpl(ILGenerator il, Type type, MemberInfo[] members, int memberCount, FieldBuilder mapField, bool allowNonPublicAccessors, bool isGet)
    {
        OpCode obj, index, value;

        Label fail = il.DefineLabel();
        if (mapField == null)
        {
            index = OpCodes.Ldarg_0;
            obj = OpCodes.Ldarg_1;
            value = OpCodes.Ldarg_2;
        }
        else
        {
            il.DeclareLocal(typeof(int));
            index = OpCodes.Ldloc_0;
            obj = OpCodes.Ldarg_1;
            value = OpCodes.Ldarg_3;

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, mapField);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldloca_S, (byte)0);
            il.EmitCall(OpCodes.Callvirt, s_tryGetValue, null);
            il.Emit(OpCodes.Brfalse, fail);
        }
        Label[] labels = new Label[memberCount];
        for (int i = 0; i < memberCount; i++)
        {
            labels[i] = il.DefineLabel();
        }
        il.Emit(index);
        il.Emit(OpCodes.Switch, labels);
        il.MarkLabel(fail);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Newobj, s_argOutOfRangeCtor);
        il.Emit(OpCodes.Throw);
        for (int i = 0; i < labels.Length; i++)
        {
            il.MarkLabel(labels[i]);
            MemberInfo member = members[i];
            bool isFail = true;

            void WriteField(FieldInfo fieldToWrite)
            {
                if (!fieldToWrite.FieldType.IsByRef)
                {
                    il.Emit(obj);
                    Cast(il, type, true);
                    if (isGet)
                    {
                        il.Emit(OpCodes.Ldfld, fieldToWrite);
                        if (fieldToWrite.FieldType.IsValueType) il.Emit(OpCodes.Box, fieldToWrite.FieldType);
                    }
                    else
                    {
                        il.Emit(value);
                        Cast(il, fieldToWrite.FieldType, false);
                        il.Emit(OpCodes.Stfld, fieldToWrite);
                    }
                    il.Emit(OpCodes.Ret);
                    isFail = false;
                }
            }
            if (member is FieldInfo field)
            {
                WriteField(field);
            }
            else if (member is PropertyInfo prop)
            {
                Type propType = prop.PropertyType;
                bool isByRef = propType.IsByRef, isValid = true;
                if (isByRef)
                {
                    if (!isGet)
                    {
                        foreach (CustomAttributeData attr in prop.CustomAttributes)
                        {
                            if (attr.AttributeType.FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute")
                            {
                                isValid = false; // can't assign indirectly to ref-readonly
                                break;
                            }
                        }
                    }
                    propType = propType.GetElementType(); // from "ref Foo" to "Foo"
                }

                MethodInfo accessor = (isGet || isByRef) ? prop.GetGetMethod(allowNonPublicAccessors) : prop.GetSetMethod(allowNonPublicAccessors);
                if (accessor == null && allowNonPublicAccessors && !isByRef)
                {
                    // No getter/setter, use backing field instead if it exists
                    var backingField = $"<{prop.Name}>k__BackingField";
                    field = prop.DeclaringType?.GetField(backingField, BindingFlags.Instance | BindingFlags.NonPublic);

                    if (field != null)
                    {
                        WriteField(field);
                    }
                }
                else if (isValid && prop.CanRead && accessor != null)
                {
                    il.Emit(obj);
                    Cast(il, type, true); // cast the input object to the right target type

                    if (isGet)
                    {
                        il.EmitCall(type.IsValueType ? OpCodes.Call : OpCodes.Callvirt, accessor, null);
                        if (isByRef) il.Emit(OpCodes.Ldobj, propType); // defererence if needed
                        if (propType.IsValueType) il.Emit(OpCodes.Box, propType); // box the value if needed
                    }
                    else
                    {
                        // when by-ref, we get the target managed pointer *first*, i.e. put obj.TheRef on the stack
                        if (isByRef) il.EmitCall(type.IsValueType ? OpCodes.Call : OpCodes.Callvirt, accessor, null);

                        // load the new value, and type it
                        il.Emit(value);
                        Cast(il, propType, false);

                        if (isByRef)
                        {   // assign to the managed pointer
                            il.Emit(OpCodes.Stobj, propType);
                        }
                        else
                        {   // call the setter
                            il.EmitCall(type.IsValueType ? OpCodes.Call : OpCodes.Callvirt, accessor, null);
                        }
                    }
                    il.Emit(OpCodes.Ret);
                    isFail = false;
                }
            }
            if (isFail) il.Emit(OpCodes.Br, fail);
        }
    }

    /// <summary>
    /// A TypeAccessor based on a Type implementation, with available member metadata
    /// </summary>
    protected abstract class RuntimeTypeAccessor : TypeAccessor
    {
        /// <summary>
        /// Returns the Type represented by this accessor
        /// </summary>
        protected abstract Type Type { get; }

        /// <summary>
        /// Can this type be queried for member availability?
        /// </summary>
        public override bool GetMembersSupported { get { return true; } }
        private MemberSet _members;
        /// <summary>
        /// Query the members available for this type
        /// </summary>
        public override MemberSet GetMembers()
        {
            return _members ??= new MemberSet(Type);
        }
    }
    private sealed class DelegateAccessor : RuntimeTypeAccessor
    {
#if NET8_0_OR_GREATER
        private readonly FrozenDictionary<string, int> _map;
#else
        private readonly Dictionary<string, int> _map;
#endif
        private readonly Func<int, object, object> _getter;
        private readonly Action<int, object, object> _setter;
        private readonly Func<object> _ctor;
        private readonly Type _type;
        protected override Type Type
        {
            get { return _type; }
        }
#if NET8_0_OR_GREATER
        public DelegateAccessor(FrozenDictionary<string, int> map, Func<int, object, object> getter, Action<int, object, object> setter, Func<object> ctor, Type type)
#else
        public DelegateAccessor(Dictionary<string, int> map, Func<int, object, object> getter, Action<int, object, object> setter, Func<object> ctor, Type type)
#endif
        {
            _map = map;
            _getter = getter;
            _setter = setter;
            _ctor = ctor;
            _type = type;
        }
        public override bool CreateNewSupported { get { return _ctor != null; } }
        public override object CreateNew()
        {
            return _ctor != null ? _ctor() : base.CreateNew();
        }
        public override object this[object target, string name]
        {
            get
            {
                if (_map.TryGetValue(name, out var index)) return _getter(index, target);
                else throw new ArgumentOutOfRangeException(nameof(name));
            }
            set
            {
                if (_map.TryGetValue(name, out var index)) _setter(index, target, value);
                else throw new ArgumentOutOfRangeException(nameof(name));
            }
        }
    }
    private static bool IsFullyPublic(Type type, PropertyInfo[] props, bool allowNonPublicAccessors)
    {
        while (type.IsNestedPublic) type = type.DeclaringType;
        if (!type.IsPublic) return false;

        if (allowNonPublicAccessors)
        {
            for (int i = 0; i < props.Length; i++)
            {
                MethodInfo getter = props[i].GetGetMethod(true);
                if (getter != null && !getter.IsPublic) return false;
                MethodInfo setter = props[i].GetSetMethod(true);
                if (setter != null && !setter.IsPublic) return false;
            }
        }

        return true;
    }
    private static TypeAccessor CreateNew(Type type, bool allowNonPublicAccessors)
    {
        if (typeof(IDynamicMetaObjectProvider).IsAssignableFrom(type))
        {
            return DynamicAccessor.Singleton;
        }

        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        if (allowNonPublicAccessors)
            flags |= BindingFlags.NonPublic;
        PropertyInfo[] props = type.GetTypeAndInterfaceProperties(flags);
        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        int capacity = props.Length + fields.Length;
        Dictionary<string, int> map = new Dictionary<string, int>(capacity);
        MemberInfo[] members = new MemberInfo[capacity];
        int i = 0;
        foreach (PropertyInfo prop in props)
        {
            if (prop.GetIndexParameters().Length == 0)
            {
#if NET8_0_OR_GREATER
                if (map.TryAdd(prop.Name, i))
#else
                if (!map.ContainsKey(prop.Name))
#endif
                {
#if !NET8_0_OR_GREATER
                    map.Add(prop.Name, i);
#endif
                    members[i++] = prop;
                }
            }
        }
#if NET8_0_OR_GREATER
        foreach (FieldInfo field in fields) if (map.TryAdd(field.Name, i)) { members[i++] = field; }
#else
        foreach (FieldInfo field in fields) if (!map.ContainsKey(field.Name)) { map.Add(field.Name, i); members[i++] = field; }
#endif
        int memberCount = i;

        ConstructorInfo ctor = null;
        if (type.IsClass && !type.IsAbstract)
        {
            ctor = type.GetConstructor(Type.EmptyTypes);
        }
        ILGenerator il;
        if (!IsFullyPublic(type, props, allowNonPublicAccessors))
        {
            var typeName = type.Name;
            DynamicMethod dynGetter = new DynamicMethod(typeName + "_get", typeof(object), new Type[] { typeof(int), typeof(object) }, type, true),
                          dynSetter = new DynamicMethod(typeName + "_set", null, new Type[] { typeof(int), typeof(object), typeof(object) }, type, true);
            WriteMapImpl(dynGetter.GetILGenerator(), type, members, memberCount, null, allowNonPublicAccessors, true);
            WriteMapImpl(dynSetter.GetILGenerator(), type, members, memberCount, null, allowNonPublicAccessors, false);
            DynamicMethod dynCtor = null;
            if (ctor != null)
            {
                dynCtor = new DynamicMethod(typeName + "_ctor", typeof(object), Type.EmptyTypes, type, true);
                il = dynCtor.GetILGenerator();
                il.Emit(OpCodes.Newobj, ctor);
                il.Emit(OpCodes.Ret);
            }
            return new DelegateAccessor(
#if NET8_0_OR_GREATER
                map.ToFrozenDictionary(),
#else
                map,
#endif
                (Func<int, object, object>)dynGetter.CreateDelegate(typeof(Func<int, object, object>)),
                (Action<int, object, object>)dynSetter.CreateDelegate(typeof(Action<int, object, object>)),
                dynCtor == null ? null : (Func<object>)dynCtor.CreateDelegate(typeof(Func<object>)), type);
        }

        if (s_assembly == null)
        {
            lock (s_assemblyLock)
            {
                if (s_assembly == null)
                {
                    AssemblyName name = new AssemblyName("FastMember_dynamic");
                    var asm = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
                    s_module = asm.DefineDynamicModule(name.Name);
                    s_assembly = asm; // volatile-like: set assembly last so readers see module
                }
            }
        }
        TypeAttributes attribs = s_baseTypeAttributes;
        TypeBuilder tb = s_module.DefineType("FastMember_dynamic." + type.Name + "_" + GetNextCounterValue(),
            (attribs | TypeAttributes.Sealed | TypeAttributes.Public) & ~(TypeAttributes.Abstract | TypeAttributes.NotPublic), typeof(RuntimeTypeAccessor));

#if NET8_0_OR_GREATER
        Type mapType = typeof(FrozenDictionary<string, int>);
#else
        Type mapType = typeof(Dictionary<string, int>);
#endif
        FieldBuilder mapField = tb.DefineField("_map", mapType, FieldAttributes.InitOnly | FieldAttributes.Private);
        il = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] {
            mapType
        }).GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, s_runtimeTypeAccessorCtor);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, mapField);
        il.Emit(OpCodes.Ret);

        MethodInfo baseGetter = s_indexerGetter, baseSetter = s_indexerSetter;
        MethodBuilder body = tb.DefineMethod(baseGetter.Name, baseGetter.Attributes & ~MethodAttributes.Abstract, typeof(object), new Type[] { typeof(object), typeof(string) });
        il = body.GetILGenerator();
        WriteMapImpl(il, type, members, memberCount, mapField, allowNonPublicAccessors, true);
        tb.DefineMethodOverride(body, baseGetter);

        body = tb.DefineMethod(baseSetter.Name, baseSetter.Attributes & ~MethodAttributes.Abstract, null, new Type[] { typeof(object), typeof(string), typeof(object) });
        il = body.GetILGenerator();
        WriteMapImpl(il, type, members, memberCount, mapField, allowNonPublicAccessors, false);
        tb.DefineMethodOverride(body, baseSetter);

        MethodInfo baseMethod;
        if (ctor != null)
        {
            baseMethod = s_createNewSupportedGetter;
            body = tb.DefineMethod(baseMethod.Name, baseMethod.Attributes, baseMethod.ReturnType, Type.EmptyTypes);
            il = body.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(body, baseMethod);

            baseMethod = s_createNewMethod;
            body = tb.DefineMethod(baseMethod.Name, baseMethod.Attributes, baseMethod.ReturnType, Type.EmptyTypes);
            il = body.GetILGenerator();
            il.Emit(OpCodes.Newobj, ctor);
            il.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(body, baseMethod);
        }

        baseMethod = s_typePropertyGetter;
        body = tb.DefineMethod(baseMethod.Name, baseMethod.Attributes & ~MethodAttributes.Abstract, baseMethod.ReturnType, Type.EmptyTypes);
        il = body.GetILGenerator();
        il.Emit(OpCodes.Ldtoken, type);
        il.Emit(OpCodes.Call, s_getTypeFromHandle);
        il.Emit(OpCodes.Ret);
        tb.DefineMethodOverride(body, baseMethod);

#if NET8_0_OR_GREATER
        var accessor = (TypeAccessor)Activator.CreateInstance(tb.CreateTypeInfo().AsType(), map.ToFrozenDictionary());
#else
        var accessor = (TypeAccessor)Activator.CreateInstance(tb.CreateTypeInfo().AsType(), map);
#endif
        return accessor;
    }

    private static void Cast(ILGenerator il, Type type, bool valueAsPointer)
    {
        if (type == typeof(object)) { }
        else if (type.IsValueType)
        {
            if (valueAsPointer)
            {
                il.Emit(OpCodes.Unbox, type);
            }
            else
            {
                il.Emit(OpCodes.Unbox_Any, type);
            }
        }
        else
        {
            il.Emit(OpCodes.Castclass, type);
        }
    }

    /// <summary>
    /// Gets or sets the value of the named member on the target object.
    /// </summary>
    /// <param name="target">The object instance to read from or write to.</param>
    /// <param name="name">The name of the property or field.</param>
    /// <returns>The current value of the member.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="name"/> does not correspond to a known member.</exception>
    public abstract object this[object target, string name]
    {
        get;
        set;
    }
}
