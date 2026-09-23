namespace DbDelta.Core.Model;

// Hand-created statistics only; the server makes and drops its own as it pleases. Not compared — two
// databases tuned by different people differ here for no reason worth syncing — but read, because a
// statistics object on a column blocks ALTER COLUMN just as an index does.
public sealed class StatisticsDefinition
{
    public required string Name { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];

    public string? FilterExpression { get; init; }

    public bool NoRecompute { get; init; }
}
