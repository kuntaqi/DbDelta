using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

// The data-compare read path against a live server: two tables that differ by one insert, one update and one
// delete are streamed, merged, and the classification checked; then the row detail, by-value, uniqueness,
// fingerprint and volume readers are exercised over the same data. Uses two schemas in the shared test
// database — the readers address a table by its identity, so no separate database is needed.
[Collection(nameof(PgCollection))]
[Trait("Speed", "Slow")]
[Trait("RequiresPostgres", "true")]
public sealed class PostgreSqlDataComparePathTests
{
    private const string Ddl = """
        drop schema if exists wp_src cascade;
        drop schema if exists wp_tgt cascade;
        create schema wp_src;
        create schema wp_tgt;
        create table wp_src.widget (id integer primary key, name text, qty integer);
        create table wp_tgt.widget (id integer primary key, name text, qty integer);
        insert into wp_src.widget values (1, 'a', 10), (2, 'b', 20), (3, 'c', 30);
        insert into wp_tgt.widget values (2, 'b', 20), (3, 'c', 99), (4, 'd', 40);
        analyze wp_src.widget;
        analyze wp_tgt.widget;
        """;

    private static readonly string[] Compared = ["name", "qty"];

    private readonly PgFixture _fixture;

    public PostgreSqlDataComparePathTests(PgFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Compares_fetches_and_profiles_table_data()
    {
        Skip.IfNot(_fixture.Available, $"PostgreSQL is not available: {_fixture.UnavailableReason}");

        await _fixture.ExecuteAsync(Ddl);
        var conn = _fixture.ConnectionString!;

        var source = Widget("wp_src");
        var target = Widget("wp_tgt");
        var request = new DataCompareRequest { Table = source.Identity, KeyColumns = ["id"], Mode = TableDataMode.AllRows };

        // The merge join over the two streams: id 1 only in source, id 3 differs, id 4 only in target.
        var hashReader = new PostgreSqlRowHashReader(conn);
        var result = await DataComparer.CompareAsync(
            hashReader.StreamAsync(source, request, Compared, RowSetSide.Source),
            hashReader.StreamAsync(target, request, Compared, RowSetSide.Target),
            new DataCompareSettings { Mode = TableDataMode.AllRows, ComparedColumns = Compared });

        Assert.Equal(1, result.InsertCount);
        Assert.Equal(1, result.UpdateCount);
        Assert.Equal(1, result.DeleteCount);
        Assert.Equal(1, result.SameCount);

        // Row detail for the inserted key fetches the source values behind it.
        var insertKey = result.Of(RowClassification.Insert).Single().Key;
        var detail = await new PostgreSqlRowDetailReader(conn).FetchAsync(source, request, Compared, [insertKey]);
        var inserted = Assert.Single(detail);
        Assert.Equal("a", inserted.Values["name"]);
        Assert.Equal("10", inserted.Values["qty"]);

        // Row-by-value addresses parents by literal id; 4 is not in the source, so only 1 comes back.
        var byValue = await new PostgreSqlRowByValueReader(conn).FetchAsync(
            source, ["id"], [new ParentKey(["1"]), new ParentKey(["4"])], ["name"]);
        var parent = Assert.Single(byValue);
        Assert.Equal("a", parent.Values["name"]);

        // Key uniqueness: id is a clean key.
        var uniqueness = await new PostgreSqlKeyUniquenessChecker(conn).CheckAsync(source, ["id"]);
        Assert.True(uniqueness.IsUnique);
        Assert.Equal(3, uniqueness.RowCount);

        // Fingerprint screens the two tables as different, and a table against itself as the same.
        var fingerprints = await new PostgreSqlFingerprintReader(conn).ReadAsync(
        [
            new FingerprintRequest(source, ["id"], Compared),
            new FingerprintRequest(target, ["id"], Compared),
        ]);
        Assert.Equal(3, fingerprints[0].RowCount);
        Assert.False(fingerprints[0].Matches(fingerprints[1]));

        // Volume reports the source table's footprint (reltuples after ANALYZE).
        var volume = await new PostgreSqlVolumeReader(conn).ReadAsync();
        var widget = volume.Tables.Single(t => t.Table.Schema == "wp_src" && t.Table.Name == "widget");
        Assert.Equal(3, widget.RowCount);

        await _fixture.ExecuteAsync("drop schema if exists wp_src cascade; drop schema if exists wp_tgt cascade;");
    }

    private static TableDefinition Widget(string schema) => new()
    {
        Identity = new ObjectIdentity(ObjectType.Table, schema, "widget"),
        Columns =
        [
            new ColumnDefinition { Name = "id", DataType = new DataTypeSpec("integer"), OrdinalPosition = 1 },
            new ColumnDefinition { Name = "name", DataType = new DataTypeSpec("text"), IsNullable = true, OrdinalPosition = 2 },
            new ColumnDefinition { Name = "qty", DataType = new DataTypeSpec("integer"), IsNullable = true, OrdinalPosition = 3 },
        ],
        PrimaryKey = new PrimaryKeyDefinition { Name = "widget_pkey", Columns = [new IndexColumn("id")] },
    };
}
