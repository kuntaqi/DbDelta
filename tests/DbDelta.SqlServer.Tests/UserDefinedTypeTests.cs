using DbDelta.Core.Apply;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.SqlServer.Tests;

// A user-defined type used to be invisible to the reader while its *name* still reached the emitter through
// the columns that use it, which is worse than being ignored: the script named a type it never created.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollectionB))]
public sealed class UserDefinedTypeTests
{
    private readonly LocalDbFixture _fixture;

    public UserDefinedTypeTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task An_alias_type_and_a_table_type_are_both_read()
    {
        var schema = await SourceAsync();

        var alias = schema.UserDefinedTypes.Single(t => t.Identity.Name == "PhoneNumber");
        Assert.Equal(UserDefinedTypeKind.Alias, alias.Kind);
        Assert.Equal("NVARCHAR(20)", alias.BaseType?.ToString());
        Assert.False(alias.IsNullable);

        var table = schema.UserDefinedTypes.Single(t => t.Identity.Name == "IdList");
        Assert.Equal(UserDefinedTypeKind.Table, table.Kind);
        Assert.Equal(["Id", "Ref", "Amount", "Note"], table.Columns.Select(c => c.Name));
        Assert.Equal("NVARCHAR(40)", table.Columns.Single(c => c.Name == "Note").DataType.ToString());
    }

    // The column keeps the type's name, and the name has to carry its schema: an alias type is not a
    // built-in, so [dbo].[PhoneNumber] is the only form that resolves.
    [SkippableFact]
    public async Task A_column_using_an_alias_type_names_it_with_its_schema_and_no_length()
    {
        var schema = await SourceAsync();

        var phone = schema.Tables.Single(t => t.Identity.Name == "Contact")
            .Columns.Single(c => c.Name == "Phone");

        Assert.True(phone.DataType.IsUserDefined);
        Assert.Equal("dbo.PhoneNumber", phone.DataType.ToString());
    }

    // The point of the exercise. Against an empty target every type is a create, and the table that uses
    // one cannot be created before it.
    [SkippableFact]
    public async Task Types_are_created_before_the_tables_that_use_them_and_the_script_commits()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "UdtCreate";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var source = await SourceAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var diff = new SchemaComparer().Compare(source, target);

            var script = new TSqlEmitter().Emit(source, target, diff);
            var sql = script.ToSql();

            Assert.True(
                sql.IndexOf("CREATE TYPE [dbo].[PhoneNumber]", StringComparison.Ordinal)
                    < sql.IndexOf("CREATE TABLE [dbo].[Contact]", StringComparison.Ordinal),
                "the type has to be created before the table that uses it");

            var result = await new SqlServerScriptExecutor().ExecuteAsync(connectionString, script);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();

            Assert.Equal(
                ["dbo.IdList", "dbo.PhoneNumber"],
                after.UserDefinedTypes.Select(t => t.Identity.QualifiedName).Order(StringComparer.Ordinal));

            // Read back rather than trusted: the column has to still be the alias type on the target.
            var phone = after.Tables.Single(t => t.Identity.Name == "Contact")
                .Columns.Single(c => c.Name == "Phone");

            Assert.Equal("dbo.PhoneNumber", phone.DataType.ToString());
            Assert.Empty(new SchemaComparer().Compare(source, after).Differing);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // A table type is a table shape, and everything in that shape used to be dropped on the floor: only
    // the columns were read, so a type with a key, a unique constraint, a default, a check and an index
    // compared as though it had none of them.
    [SkippableFact]
    public async Task A_table_types_constraints_are_all_read()
    {
        var schema = await SourceAsync();
        var type = schema.UserDefinedTypes.Single(t => t.Identity.Name == "IdList");

        Assert.Equal(["Id"], type.PrimaryKey?.Columns.Select(c => c.Name));
        Assert.Equal(["Ref"], Assert.Single(type.UniqueConstraints).Columns.Select(c => c.Name));
        Assert.Contains("Amount", Assert.Single(type.CheckConstraints).Expression);
        // The catalog wraps a default in its own brackets, so the assertion is about the value in it.
        var amount = type.Columns.Single(c => c.Name == "Amount");
        Assert.Contains("0", amount.DefaultExpression);

        // The one part with a name the author chose, so the name is real and worth comparing.
        var index = Assert.Single(type.Indexes);
        Assert.Equal("IX_IdList_Note", index.Name);
        Assert.Equal(["Note"], index.Columns.Select(c => c.Name));
    }

    // The point of the exercise: emitted and re-read, the type comes back the same shape. A constraint name
    // cannot survive that trip — SQL Server invents a new one each time — which is exactly why the
    // comparison is by shape and the emitter writes no names.
    [SkippableFact]
    public async Task A_table_type_survives_being_emitted_and_read_back()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "TableTypeRoundTrip";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var source = await SourceAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var diff = new SchemaComparer().Compare(source, target);

            var script = new TSqlEmitter().Emit(source, target, diff);
            var result = await new SqlServerScriptExecutor().ExecuteAsync(connectionString, script);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var rebuilt = after.UserDefinedTypes.Single(t => t.Identity.Name == "IdList");

            Assert.Equal(["Id"], rebuilt.PrimaryKey?.Columns.Select(c => c.Name));
            Assert.Single(rebuilt.UniqueConstraints);
            Assert.Single(rebuilt.CheckConstraints);
            Assert.Equal("IX_IdList_Note", Assert.Single(rebuilt.Indexes).Name);
            Assert.NotNull(rebuilt.Columns.Single(c => c.Name == "Amount").DefaultExpression);

            // And the whole schema compares clean, which is the assertion that would have failed before:
            // a type read without its constraints emits without them and then differs from its source.
            Assert.Empty(new SchemaComparer().Compare(source, after).Differing);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // Closure follows a column's type the same way it follows a foreign key: ticking the table alone has
    // to bring the type it is declared with, or the CREATE TABLE names something that does not exist.
    [SkippableFact]
    public async Task Ticking_a_table_pulls_in_the_type_its_column_is_declared_with()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "UdtClosure";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var source = await SourceAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var diff = new SchemaComparer().Compare(source, target);

            var contact = new ObjectIdentity(ObjectType.Table, "dbo", "Contact");
            var closure = DbDelta.Core.Planning.SchemaClosure.Expand(source, target, diff, [contact]);

            var type = Assert.Single(closure.Required, r => r.Identity.Type == ObjectType.UserDefinedType);

            Assert.Equal("dbo.PhoneNumber", type.Identity.QualifiedName);
            Assert.Contains("Contact.Phone", type.Reason);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    private async Task<DatabaseSchema> SourceAsync()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        return await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();
    }
}
