using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.SqlServer.Tests;

[Collection(nameof(LocalDbCollection))]
public sealed class SchemaReaderTests
{
    private readonly LocalDbFixture _fixture;

    public SchemaReaderTests(LocalDbFixture fixture) => _fixture = fixture;

    private async Task<DatabaseSchema> ReadSourceAsync()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var reader = new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase));

        return await reader.ReadAsync();
    }

    private static TableDefinition Table(DatabaseSchema schema, string name) =>
        schema.Tables.Single(t => t.Identity.Name == name);

    [SkippableFact]
    public async Task Reads_the_user_tables()
    {
        var schema = await ReadSourceAsync();

        Assert.Equal(
            ["Category", "Company", "Contact", "Digest", "Metric", "Territory"],
            schema.Tables.Select(t => t.Identity.Name).OrderBy(n => n));
    }

    [SkippableFact]
    public async Task Nvarchar_length_is_characters_not_bytes()
    {
        // sys.columns.max_length reports 80 for NVARCHAR(40). Taking it raw would make every unicode
        // column look twice as wide as it is and produce a phantom diff against a correct target.
        var schema = await ReadSourceAsync();

        var segment = Table(schema, "Company").Columns.Single(c => c.Name == "Segment");

        Assert.Equal(40, segment.DataType.MaxLength);
        Assert.Equal("NVARCHAR(40)", segment.DataType.ToString());
    }

    [SkippableFact]
    public async Task Decimal_precision_and_scale_are_captured()
    {
        var schema = await ReadSourceAsync();

        var total = Table(schema, "Company").Columns.Single(c => c.Name == "Total");

        Assert.Equal("DECIMAL(18,2)", total.DataType.ToString());
    }

    [SkippableFact]
    public async Task Identity_seed_and_increment_are_captured()
    {
        var schema = await ReadSourceAsync();

        var id = Table(schema, "Company").Columns.Single(c => c.Name == "CompanyId");

        Assert.NotNull(id.Identity);
        Assert.Equal(1, id.Identity.Seed);
        Assert.Equal(1, id.Identity.Increment);
    }

    [SkippableFact]
    public async Task Nullability_and_defaults_are_captured()
    {
        var schema = await ReadSourceAsync();
        var company = Table(schema, "Company");

        Assert.True(company.Columns.Single(c => c.Name == "Segment").IsNullable);
        Assert.False(company.Columns.Single(c => c.Name == "CompanyName").IsNullable);

        var total = company.Columns.Single(c => c.Name == "Total");
        Assert.Equal("DF_Company_Total", total.DefaultConstraintName);
        Assert.NotNull(total.DefaultExpression);
    }

    [SkippableFact]
    public async Task Primary_key_is_captured_with_its_columns()
    {
        var schema = await ReadSourceAsync();

        var pk = Table(schema, "Company").PrimaryKey;

        Assert.NotNull(pk);
        Assert.Equal("PK_Company", pk.Name);
        Assert.Equal("CompanyId", Assert.Single(pk.Columns).Name);
    }

    [SkippableFact]
    public async Task Unique_constraint_is_separate_from_indexes()
    {
        var schema = await ReadSourceAsync();
        var category = Table(schema, "Category");

        Assert.Equal("UQ_Category_Name", Assert.Single(category.UniqueConstraints).Name);
        Assert.Empty(category.Indexes);
    }

    [SkippableFact]
    public async Task The_index_backing_a_primary_key_is_not_reported_twice()
    {
        var schema = await ReadSourceAsync();

        var company = Table(schema, "Company");

        Assert.DoesNotContain(company.Indexes, i => i.Name == "PK_Company");
        Assert.All(company.Indexes, i => Assert.StartsWith(i.IsUnique ? "UX" : "IX", i.Name));
    }

    [SkippableFact]
    public async Task Index_include_columns_are_captured()
    {
        var schema = await ReadSourceAsync();

        var index = Table(schema, "Company").Indexes.Single(i => i.Name == "IX_Company_Rating");

        Assert.Equal("RatingBand", Assert.Single(index.Columns).Name);
        Assert.Equal("CompanyName", Assert.Single(index.IncludedColumns));
        Assert.False(index.IsUnique);
    }

    [SkippableFact]
    public async Task Filtered_index_keeps_its_predicate()
    {
        var schema = await ReadSourceAsync();

        var index = Table(schema, "Company").Indexes.Single(i => i.Name == "UX_Company_Segment");

        Assert.True(index.IsUnique);
        Assert.NotNull(index.FilterExpression);
        Assert.Contains("Segment", index.FilterExpression);
    }

    [SkippableFact]
    public async Task Foreign_key_records_the_table_it_points_at()
    {
        var schema = await ReadSourceAsync();

        var fk = Assert.Single(Table(schema, "Company").ForeignKeys);

        Assert.Equal("FK_Company_Category", fk.Name);
        Assert.Equal("dbo.Category", fk.ReferencedTable.QualifiedName);
        Assert.Equal("CategoryId", Assert.Single(fk.Columns));
        Assert.Equal("CategoryId", Assert.Single(fk.ReferencedColumns));
    }

    [SkippableFact]
    public async Task Check_constraint_is_captured()
    {
        var schema = await ReadSourceAsync();

        var check = Assert.Single(Table(schema, "Company").CheckConstraints);

        Assert.Equal("CK_Company_Rating", check.Name);
        Assert.Contains("RatingBand", check.Expression);
    }

    [SkippableFact]
    public async Task Views_and_routines_are_read_with_their_bodies()
    {
        var schema = await ReadSourceAsync();

        var view = schema.Views.Single(v => v.Identity.Name == "vCompanySegment");
        Assert.Equal("sales.vCompanySegment", view.Identity.QualifiedName);
        Assert.Contains("SELECT", view.Definition, StringComparison.OrdinalIgnoreCase);

        var routine = schema.Routines.Single(r => r.Identity.Name == "usp_GetCompany");
        Assert.Equal("dbo.usp_GetCompany", routine.Identity.QualifiedName);
        Assert.Equal(RoutineKind.Procedure, routine.Kind);
        Assert.Contains("SELECT", routine.Definition, StringComparison.OrdinalIgnoreCase);
    }

    // Views depend on each other and on tables through their SQL bodies, and only the catalog knows
    // that. The view-to-view edges are what let the emitter order a view after the view it selects
    // from; the table edges are what let closure pull in a table the view cannot be created without.
    // Only direct references are recorded, so vActiveSegments names the view it reads and not the
    // table behind it.
    [SkippableFact]
    public async Task A_views_dependencies_on_views_and_on_tables_are_both_captured()
    {
        var schema = await ReadSourceAsync();

        var dependent = schema.Views.Single(v => v.Identity.Name == "vActiveSegments");
        Assert.Equal(
            ["sales.vCompanySegment"],
            dependent.DependsOn.Select(d => d.QualifiedName));

        var referenced = schema.Views.Single(v => v.Identity.Name == "vCompanySegment");
        var table = Assert.Single(referenced.DependsOn);

        Assert.Equal(ObjectType.Table, table.Type);
        Assert.Equal("dbo.Company", table.QualifiedName);
    }

    [SkippableFact]
    public async Task Database_collation_is_reported()
    {
        var schema = await ReadSourceAsync();

        Assert.False(string.IsNullOrWhiteSpace(schema.Collation));
    }

    [SkippableFact]
    public async Task An_empty_database_reports_itself_as_empty()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var reader = new SqlServerSchemaReader(LocalDbFixture.ConnectionStringFor("master"));
        var schema = await reader.ReadAsync();

        // master holds no user objects, so it stands in for the empty-target case.
        Assert.True(schema.IsEmpty);
        Assert.Equal(0, schema.ObjectCount);
    }

    [SkippableFact]
    public async Task Provider_probe_reports_the_server()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var info = await new SqlServerProvider()
            .ProbeAsync(LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase));

        Assert.Equal(LocalDbFixture.SourceDatabase, info.DatabaseName);
        Assert.False(string.IsNullOrWhiteSpace(info.ProductVersion));
        Assert.False(string.IsNullOrWhiteSpace(info.Collation));
    }

    [SkippableFact]
    public void Quoter_escapes_a_closing_bracket()
    {
        Assert.Equal("[weird]]name]", SqlServerQuoter.Instance.Quote("weird]name"));
        Assert.Equal(
            "[dbo].[Company]",
            SqlServerQuoter.Instance.Qualify(new ObjectIdentity(ObjectType.Table, "dbo", "Company")));
    }
}
