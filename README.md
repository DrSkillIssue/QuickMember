# QuickMember

Fast by-name member access for .NET. Based on [FastMember](https://github.com/mgravell/fast-member) by Marc Gravell.

[![NuGet](https://img.shields.io/nuget/v/QuickMember.svg)](https://www.nuget.org/packages/QuickMember/)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

## What is this?

In .NET, reflection is slow. If you need access to the members of an arbitrary type, with the type and member names known only at runtime, it is frankly **hard** (especially for DLR types). This library makes such access easy and fast by generating IL at runtime that approaches the speed of direct compiled property access.

## Installation

```
dotnet add package QuickMember
```

**Targets:** `netstandard2.0`, `net8.0`, `net10.0`

## Usage

### Type-level access (best for many objects of the same type)

```csharp
var accessor = TypeAccessor.Create(typeof(MyType));

while (/* some loop of data */)
{
    accessor[obj, "Name"] = rowValue;
}
```

### Object-level access (wraps a single instance)

```csharp
var wrapped = ObjectAccessor.Create(obj); // works with static and DLR types
Console.WriteLine(wrapped["Name"]);
```

### As an IDataReader (for SqlBulkCopy, DataTable, TVPs)

Load a `DataTable` from a sequence of objects:

```csharp
IEnumerable<Customer> data = GetCustomers();
var table = new DataTable();
using (var reader = ObjectReader.Create(data))
{
    table.Load(reader);
}
```

Bulk-insert into a database:

```csharp
using var bcp = new SqlBulkCopy(connection);
using var reader = ObjectReader.Create(data, "Id", "Name", "Email");
bcp.DestinationTableName = "Customers";
bcp.WriteToServer(reader);
```

### Non-public members

```csharp
var accessor = TypeAccessor.Create(typeof(MyType), allowNonPublicAccessors: true);
accessor[obj, "InternalProp"] = value;
```

### Member inspection

```csharp
var accessor = TypeAccessor.Create(typeof(MyType));
var members = accessor.GetMembers();

foreach (var member in members)
{
    Console.WriteLine($"{member.Name}: {member.Type} (CanRead={member.CanRead}, IsIndexer={member.IsIndexer})");
}
```

### Column ordering with OrdinalAttribute

```csharp
public class Customer
{
    [Ordinal(2)]
    public string Email { get; set; }

    [Ordinal(0)]
    public int Id { get; set; }

    [Ordinal(1)]
    public string Name { get; set; }
}
```

## Benchmarks

### Accessor Performance (get + set per call)

```
BenchmarkDotNet v0.15.8, .NET 10.0.5, AMD Ryzen 9 7950X

| Method                   |       Mean | Allocated |
|------------------------- |-----------:|----------:|
| Static C# (baseline)     |   0.163 ns |       0 B |
| Dynamic C#               |   4.379 ns |       0 B |
| TypeAccessor             |   9.983 ns |       0 B |
| ObjectAccessor           |  10.533 ns |       0 B |
| PropertyInfo             |  14.073 ns |       0 B |
| PropertyDescriptor       |  47.308 ns |      32 B |
| TypeAccessor.CreateNew   |   3.392 ns |      24 B |
| Activator.CreateInstance |   8.742 ns |      24 B |
```

### Construction & Memory

```
| Method                       |        Mean | Allocated |
|----------------------------- |------------:|----------:|
| GetMembers (cached)          |    0.608 ns |       0 B |
| ObjectAccessor.Create        |   12.110 ns |     120 B |
| ObjectReader.Create (8 cols) |  348.893 ns |   1,000 B |
| ObjectReader.Read+GetValues  |  292.711 ns |   1,240 B |
| GetSchemaTable               | 2,608.989 ns|  11,832 B |
```

## Changes from FastMember

QuickMember is a derivative work of [FastMember](https://github.com/mgravell/fast-member) (Apache-2.0). Key changes:

### Features

- **`Member.IsIndexer`** -- detect indexer properties to avoid exceptions when iterating all members
- **Non-public property discovery** -- `TypeAccessor.Create(type, allowNonPublicAccessors: true)` now finds internal/private properties (not just non-public getters/setters on public properties)
- **`IsKey` column** in `GetSchemaTable` -- enables `ObjectReader` as a table-valued parameter source for `SqlParameter`

## Ahead of Time

This library emits IL at runtime. It will not work in constrained AOT environments (iOS, Unity IL2CPP, NativeAOT without `rd.xml` configuration).

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE) for original attribution.
