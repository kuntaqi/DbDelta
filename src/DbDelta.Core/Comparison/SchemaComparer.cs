using DbDelta.Core.Model;

namespace DbDelta.Core.Comparison;

public sealed class SchemaComparer
{
    private readonly ComparisonOptions _options;

    public SchemaComparer(ComparisonOptions? options = null) =>
        _options = options ?? ComparisonOptions.Default;

    public SchemaDiff Compare(DatabaseSchema source, DatabaseSchema target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var objects = new List<ObjectDiff>();
        objects.AddRange(CompareTables(source.Tables, target.Tables));
        objects.AddRange(CompareViews(source.Views, target.Views));
        objects.AddRange(CompareRoutines(source.Routines, target.Routines));
        objects.AddRange(CompareTriggers(source.Triggers, target.Triggers));
        objects.AddRange(CompareSequences(source.Sequences, target.Sequences));
        objects.AddRange(CompareUserDefinedTypes(source.UserDefinedTypes, target.UserDefinedTypes));

        return new SchemaDiff
        {
            SourceDatabase = source.DatabaseName,
            TargetDatabase = target.DatabaseName,
            Objects = objects
                .OrderBy(o => o.Identity.Type)
                .ThenBy(o => o.Identity.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private IEnumerable<ObjectDiff> CompareTables(
        IReadOnlyList<TableDefinition> source,
        IReadOnlyList<TableDefinition> target)
    {
        foreach (var (src, tgt, identity) in Pair(source, target, t => t.Identity))
        {
            if (!_options.ShouldCompareSchema(identity.Schema))
            {
                continue;
            }

            if (src is null || tgt is null)
            {
                yield return Presence(identity, src, tgt);
                continue;
            }

            var children = new List<ObjectDiff>();
            children.AddRange(CompareColumns(identity, src.Columns, tgt.Columns));
            children.AddRange(ComparePrimaryKey(identity, src.PrimaryKey, tgt.PrimaryKey));
            children.AddRange(CompareUniqueConstraints(identity, src.UniqueConstraints, tgt.UniqueConstraints));
            children.AddRange(CompareIndexes(identity, src.Indexes, tgt.Indexes));
            children.AddRange(CompareForeignKeys(identity, src.ForeignKeys, tgt.ForeignKeys));
            children.AddRange(CompareCheckConstraints(identity, src.CheckConstraints, tgt.CheckConstraints));

            yield return new ObjectDiff
            {
                Identity = identity,
                Kind = children.Any(c => c.HasChanges) ? DiffKind.Different : DiffKind.Same,
                Children = children
            };
        }
    }

    private IEnumerable<ObjectDiff> CompareColumns(
        ObjectIdentity table,
        IReadOnlyList<ColumnDefinition> source,
        IReadOnlyList<ColumnDefinition> target)
    {
        foreach (var (src, tgt, name) in PairByName(source, target, c => c.Name))
        {
            var identity = Child(table, ObjectType.Column, name);

            if (src is null || tgt is null)
            {
                yield return Presence(identity, src, tgt);
                continue;
            }

            var properties = new List<PropertyDiff>();
            Add(properties, "DataType", src.DataType.ToString(), tgt.DataType.ToString());
            Add(properties, "Nullable", src.IsNullable, tgt.IsNullable);
            Add(properties, "Identity", Describe(src.Identity), Describe(tgt.Identity));
            Add(properties, "Computed", src.ComputedExpression, tgt.ComputedExpression);
            Add(properties, "Default", src.DefaultExpression, tgt.DefaultExpression);

            if (!_options.IgnoreCollation)
            {
                Add(properties, "Collation", src.Collation, tgt.Collation);
            }

            if (!_options.IgnoreColumnOrder)
            {
                Add(properties, "Ordinal", src.OrdinalPosition, tgt.OrdinalPosition);
            }

            yield return Result(identity, properties);
        }
    }

    private static IEnumerable<ObjectDiff> ComparePrimaryKey(
        ObjectIdentity table,
        PrimaryKeyDefinition? source,
        PrimaryKeyDefinition? target)
    {
        if (source is null && target is null)
        {
            yield break;
        }

        var identity = Child(table, ObjectType.PrimaryKey, source?.Name ?? target!.Name);

        if (source is null || target is null)
        {
            yield return Presence(identity, source, target);
            yield break;
        }

        var properties = new List<PropertyDiff>();
        Add(properties, "Name", source.Name, target.Name);
        Add(properties, "Columns", Join(source.Columns), Join(target.Columns));
        Add(properties, "Clustered", source.IsClustered, target.IsClustered);

        yield return Result(identity, properties);
    }

    private static IEnumerable<ObjectDiff> CompareUniqueConstraints(
        ObjectIdentity table,
        IReadOnlyList<UniqueConstraintDefinition> source,
        IReadOnlyList<UniqueConstraintDefinition> target)
    {
        foreach (var (src, tgt, name) in PairByName(source, target, u => u.Name))
        {
            var identity = Child(table, ObjectType.UniqueConstraint, name);

            if (src is null || tgt is null)
            {
                yield return Presence(identity, src, tgt);
                continue;
            }

            var properties = new List<PropertyDiff>();
            Add(properties, "Columns", Join(src.Columns), Join(tgt.Columns));
            Add(properties, "Clustered", src.IsClustered, tgt.IsClustered);

            yield return Result(identity, properties);
        }
    }

    private IEnumerable<ObjectDiff> CompareIndexes(
        ObjectIdentity table,
        IReadOnlyList<IndexDefinition> source,
        IReadOnlyList<IndexDefinition> target)
    {
        foreach (var (src, tgt, name) in PairByName(source, target, i => i.Name))
        {
            var identity = Child(table, ObjectType.Index, name);

            if (src is null || tgt is null)
            {
                yield return Presence(identity, src, tgt);
                continue;
            }

            var properties = new List<PropertyDiff>();
            Add(properties, "Columns", Join(src.Columns), Join(tgt.Columns));
            Add(properties, "Included", string.Join(", ", src.IncludedColumns), string.Join(", ", tgt.IncludedColumns));
            Add(properties, "Unique", src.IsUnique, tgt.IsUnique);
            Add(properties, "Clustered", src.IsClustered, tgt.IsClustered);
            Add(properties, "Filter", src.FilterExpression, tgt.FilterExpression);

            if (!_options.IgnoreFillFactor)
            {
                Add(properties, "FillFactor", src.Extras["FillFactor"], tgt.Extras["FillFactor"]);
            }

            yield return Result(identity, properties);
        }
    }

    private static IEnumerable<ObjectDiff> CompareForeignKeys(
        ObjectIdentity table,
        IReadOnlyList<ForeignKeyDefinition> source,
        IReadOnlyList<ForeignKeyDefinition> target)
    {
        foreach (var (src, tgt, name) in PairByName(source, target, f => f.Name))
        {
            var identity = Child(table, ObjectType.ForeignKey, name);

            if (src is null || tgt is null)
            {
                yield return Presence(identity, src, tgt);
                continue;
            }

            var properties = new List<PropertyDiff>();
            Add(properties, "Columns", string.Join(", ", src.Columns), string.Join(", ", tgt.Columns));
            Add(properties, "References", src.ReferencedTable.QualifiedName, tgt.ReferencedTable.QualifiedName);
            Add(properties, "ReferencedColumns", string.Join(", ", src.ReferencedColumns), string.Join(", ", tgt.ReferencedColumns));
            AddEnum(properties, "OnDelete", src.OnDelete, tgt.OnDelete);
            AddEnum(properties, "OnUpdate", src.OnUpdate, tgt.OnUpdate);
            Add(properties, "Disabled", src.IsDisabled, tgt.IsDisabled);

            yield return Result(identity, properties);
        }
    }

    private static IEnumerable<ObjectDiff> CompareCheckConstraints(
        ObjectIdentity table,
        IReadOnlyList<CheckConstraintDefinition> source,
        IReadOnlyList<CheckConstraintDefinition> target)
    {
        foreach (var (src, tgt, name) in PairByName(source, target, c => c.Name))
        {
            var identity = Child(table, ObjectType.CheckConstraint, name);

            if (src is null || tgt is null)
            {
                yield return Presence(identity, src, tgt);
                continue;
            }

            var properties = new List<PropertyDiff>();
            Add(properties, "Expression", src.Expression, tgt.Expression);
            Add(properties, "Disabled", src.IsDisabled, tgt.IsDisabled);

            yield return Result(identity, properties);
        }
    }

    private IEnumerable<ObjectDiff> CompareViews(
        IReadOnlyList<ViewDefinition> source,
        IReadOnlyList<ViewDefinition> target)
    {
        foreach (var (src, tgt, identity) in Pair(source, target, v => v.Identity))
        {
            if (!_options.ShouldCompareSchema(identity.Schema))
            {
                continue;
            }

            if (src is null || tgt is null)
            {
                yield return Presence(identity, src, tgt);
                continue;
            }

            yield return BodyDiff(identity, src.Definition, tgt.Definition);
        }
    }

    private IEnumerable<ObjectDiff> CompareRoutines(
        IReadOnlyList<RoutineDefinition> source,
        IReadOnlyList<RoutineDefinition> target)
    {
        foreach (var (src, tgt, identity) in Pair(source, target, r => r.Identity))
        {
            if (!_options.ShouldCompareSchema(identity.Schema))
            {
                continue;
            }

            if (src is null || tgt is null)
            {
                yield return Presence(identity, src, tgt);
                continue;
            }

            var properties = new List<PropertyDiff>();
            AddEnum(properties, "Kind", src.Kind, tgt.Kind);

            if (!SqlBodyNormalizer.AreEquivalent(src.Definition, tgt.Definition, _options.IgnoreWhitespaceInBodies))
            {
                properties.Add(new PropertyDiff("Definition", src.Definition, tgt.Definition));
            }

            yield return Result(identity, properties);
        }
    }

    private IEnumerable<ObjectDiff> CompareTriggers(
        IReadOnlyList<TriggerDefinition> source,
        IReadOnlyList<TriggerDefinition> target)
    {
        foreach (var (src, tgt, identity) in Pair(source, target, t => t.Identity))
        {
            if (!_options.ShouldCompareSchema(identity.Schema))
            {
                continue;
            }

            if (src is null || tgt is null)
            {
                yield return Presence(identity, src, tgt);
                continue;
            }

            var properties = new List<PropertyDiff>();
            Add(properties, "Table", src.Table.QualifiedName, tgt.Table.QualifiedName);
            Add(properties, "Disabled", src.IsDisabled, tgt.IsDisabled);

            if (!SqlBodyNormalizer.AreEquivalent(src.Definition, tgt.Definition, _options.IgnoreWhitespaceInBodies))
            {
                properties.Add(new PropertyDiff("Definition", src.Definition, tgt.Definition));
            }

            yield return Result(identity, properties);
        }
    }

    // A type has no ALTER in T-SQL, so a difference here does not mean "change it" — it means "drop every
    // column that uses it, recreate the type, put them back". That is not a thing to do on someone's
    // behalf, so the difference is reported in full and the emitter refuses to act on it.
    private IEnumerable<ObjectDiff> CompareUserDefinedTypes(
        IReadOnlyList<UserDefinedTypeDefinition> source,
        IReadOnlyList<UserDefinedTypeDefinition> target)
    {
        foreach (var (src, tgt, identity) in Pair(source, target, t => t.Identity))
        {
            if (!_options.ShouldCompareSchema(identity.Schema))
            {
                continue;
            }

            if (src is null || tgt is null)
            {
                yield return Presence(identity, src, tgt);
                continue;
            }

            var properties = new List<PropertyDiff>();
            Add(properties, "Kind", src.Kind.ToString(), tgt.Kind.ToString());
            Add(properties, "BaseType", src.BaseType?.ToString(), tgt.BaseType?.ToString());
            Add(properties, "Nullable", src.IsNullable, tgt.IsNullable);

            // A table type's shape is its columns, compared as one string rather than as children:
            // there is no statement that could alter one of them on its own.
            Add(properties, "Columns", Shape(src), Shape(tgt));

            yield return Result(identity, properties);
        }
    }

    private static string Shape(UserDefinedTypeDefinition type) =>
        string.Join(", ", type.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(c => $"{c.Name} {c.DataType} {(c.IsNullable ? "NULL" : "NOT NULL")}"));

    private IEnumerable<ObjectDiff> CompareSequences(
        IReadOnlyList<SequenceDefinition> source,
        IReadOnlyList<SequenceDefinition> target)
    {
        foreach (var (src, tgt, identity) in Pair(source, target, s => s.Identity))
        {
            if (!_options.ShouldCompareSchema(identity.Schema))
            {
                continue;
            }

            if (src is null || tgt is null)
            {
                yield return Presence(identity, src, tgt);
                continue;
            }

            var properties = new List<PropertyDiff>();
            Add(properties, "DataType", src.DataType.ToString(), tgt.DataType.ToString());
            Add(properties, "Increment", src.Increment, tgt.Increment);
            Add(properties, "MinValue", src.MinValue, tgt.MinValue);
            Add(properties, "MaxValue", src.MaxValue, tgt.MaxValue);
            Add(properties, "Cycling", src.IsCycling, tgt.IsCycling);

            yield return Result(identity, properties);
        }
    }

    private ObjectDiff BodyDiff(ObjectIdentity identity, string source, string target)
    {
        var properties = new List<PropertyDiff>();

        if (!SqlBodyNormalizer.AreEquivalent(source, target, _options.IgnoreWhitespaceInBodies))
        {
            properties.Add(new PropertyDiff("Definition", source, target));
        }

        return Result(identity, properties);
    }

    private static ObjectDiff Result(ObjectIdentity identity, List<PropertyDiff> properties) =>
        new()
        {
            Identity = identity,
            Kind = properties.Count > 0 ? DiffKind.Different : DiffKind.Same,
            Properties = properties
        };

    private static ObjectDiff Presence<T>(ObjectIdentity identity, T? source, T? target) =>
        new()
        {
            Identity = identity,
            Kind = source is not null ? DiffKind.SourceOnly : DiffKind.TargetOnly
        };

    private static ObjectIdentity Child(ObjectIdentity parent, ObjectType type, string name) =>
        new(type, parent.QualifiedName, name);

    private static string Join(IEnumerable<IndexColumn> columns) =>
        string.Join(", ", columns.Select(c => c.ToString()));

    private static string? Describe(IdentitySpec? spec) =>
        spec is null ? null : $"IDENTITY({spec.Seed},{spec.Increment})";

    private static void Add(List<PropertyDiff> into, string property, string? source, string? target)
    {
        if (!string.Equals(source, target, StringComparison.Ordinal))
        {
            into.Add(new PropertyDiff(property, source, target));
        }
    }

    private static void AddEnum<T>(List<PropertyDiff> into, string property, T source, T target)
        where T : struct, Enum
    {
        if (!EqualityComparer<T>.Default.Equals(source, target))
        {
            into.Add(new PropertyDiff(property, source.ToString(), target.ToString()));
        }
    }

    private static void Add<T>(List<PropertyDiff> into, string property, T source, T target)
        where T : struct, IEquatable<T>
    {
        if (!source.Equals(target))
        {
            into.Add(new PropertyDiff(property, source.ToString(), target.ToString()));
        }
    }

    private static void Add<T>(List<PropertyDiff> into, string property, T? source, T? target)
        where T : struct, IEquatable<T>
    {
        if (!Nullable.Equals(source, target))
        {
            into.Add(new PropertyDiff(property, source?.ToString(), target?.ToString()));
        }
    }

    private static IEnumerable<(T? Source, T? Target, ObjectIdentity Identity)> Pair<T>(
        IReadOnlyList<T> source,
        IReadOnlyList<T> target,
        Func<T, ObjectIdentity> key)
        where T : class
    {
        var targetByKey = target.ToDictionary(key);
        var seen = new HashSet<ObjectIdentity>();

        foreach (var item in source)
        {
            var identity = key(item);
            seen.Add(identity);
            yield return (item, targetByKey.GetValueOrDefault(identity), identity);
        }

        foreach (var item in target)
        {
            var identity = key(item);
            if (!seen.Contains(identity))
            {
                yield return (null, item, identity);
            }
        }
    }

    private static IEnumerable<(T? Source, T? Target, string Name)> PairByName<T>(
        IReadOnlyList<T> source,
        IReadOnlyList<T> target,
        Func<T, string> key)
        where T : class
    {
        var targetByName = target.ToDictionary(key, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in source)
        {
            var name = key(item);
            seen.Add(name);
            yield return (item, targetByName.GetValueOrDefault(name), name);
        }

        foreach (var item in target)
        {
            var name = key(item);
            if (!seen.Contains(name))
            {
                yield return (null, item, name);
            }
        }
    }
}
