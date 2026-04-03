using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using QuickMember;

BenchmarkSwitcher.FromAssembly(typeof(AccessorBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class AccessorBenchmarks
{
    public class TestObj
    {
        public string Value { get; set; }
    }

    private TestObj obj;
    private dynamic dlr;
    private PropertyInfo prop;
    private PropertyDescriptor descriptor;
    private QuickMember.TypeAccessor accessor;
    private QuickMember.ObjectAccessor wrapped;
    private Type type;

    [GlobalSetup]
    public void Setup()
    {
        obj = new TestObj();
        dlr = obj;
        prop = typeof(TestObj).GetProperty("Value");
        descriptor = TypeDescriptor.GetProperties(obj)["Value"];
        accessor = QuickMember.TypeAccessor.Create(typeof(TestObj));
        wrapped = QuickMember.ObjectAccessor.Create(obj);
        type = typeof(TestObj);
    }

    [Benchmark(Description = "1. Static C#", Baseline = true)]
    public string StaticCSharp()
    {
        obj.Value = "abc";
        return obj.Value;
    }

    [Benchmark(Description = "2. Dynamic C#")]
    public string DynamicCSharp()
    {
        dlr.Value = "abc";
        return dlr.Value;
    }

    [Benchmark(Description = "3. PropertyInfo")]
    public string PropertyInfo()
    {
        prop.SetValue(obj, "abc", null);
        return (string)prop.GetValue(obj, null);
    }

    [Benchmark(Description = "4. PropertyDescriptor")]
    public string PropertyDescriptor()
    {
        descriptor.SetValue(obj, "abc");
        return (string)descriptor.GetValue(obj);
    }

    [Benchmark(Description = "5. TypeAccessor")]
    public string ViaTypeAccessor()
    {
        accessor[obj, "Value"] = "abc";
        return (string)accessor[obj, "Value"];
    }

    [Benchmark(Description = "6. ObjectAccessor")]
    public string ViaObjectAccessor()
    {
        wrapped["Value"] = "abc";
        return (string)wrapped["Value"];
    }

    [Benchmark(Description = "7. c# new()")]
    public TestObj CSharpNew()
    {
        return new TestObj();
    }

    [Benchmark(Description = "8. Activator.CreateInstance")]
    public object ActivatorCreateInstance()
    {
        return Activator.CreateInstance(type);
    }

    [Benchmark(Description = "9. TypeAccessor.CreateNew")]
    public object TypeAccessorCreateNew()
    {
        return accessor.CreateNew();
    }
}

[MemoryDiagnoser]
public class ConstructionBenchmarks
{
    public class MultiPropObj
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime Created { get; set; }
        public decimal Price { get; set; }
        public bool Active { get; set; }
        public double Score { get; set; }
        public Guid Reference { get; set; }
        public int? NullableInt { get; set; }
    }

    private Type type;
    private QuickMember.TypeAccessor accessor;

    [GlobalSetup]
    public void Setup()
    {
        type = typeof(MultiPropObj);
        // Warm the cache so GetMembers benchmark is isolated
        accessor = QuickMember.TypeAccessor.Create(type);
    }

    [Benchmark(Description = "GetMembers")]
    public MemberSet GetMembers()
    {
        return accessor.GetMembers();
    }

    [Benchmark(Description = "ObjectAccessor.Create")]
    public QuickMember.ObjectAccessor CreateObjectAccessor()
    {
        return QuickMember.ObjectAccessor.Create(new MultiPropObj());
    }

    [Benchmark(Description = "ObjectReader.Create")]
    public ObjectReader CreateObjectReader()
    {
        var data = new List<MultiPropObj>
        {
            new MultiPropObj { Id = 1, Name = "test", Price = 9.99m, Active = true }
        };
        return ObjectReader.Create(data);
    }

    [Benchmark(Description = "ObjectReader.Read+GetValues")]
    public int ReadAllValues()
    {
        var data = new List<MultiPropObj>
        {
            new MultiPropObj { Id = 1, Name = "a", Price = 1m },
            new MultiPropObj { Id = 2, Name = "b", Price = 2m },
            new MultiPropObj { Id = 3, Name = "c", Price = 3m },
        };
        using var reader = ObjectReader.Create(data, "Id", "Name", "Price");
        var values = new object[3];
        int rows = 0;
        while (reader.Read())
        {
            reader.GetValues(values);
            rows++;
        }
        return rows;
    }

    [Benchmark(Description = "GetSchemaTable")]
    public DataTable GetSchemaTable()
    {
        var data = new List<MultiPropObj>
        {
            new MultiPropObj { Id = 1, Name = "test" }
        };
        using var reader = ObjectReader.Create(data, "Id", "Name", "Price");
        return reader.GetSchemaTable();
    }
}
