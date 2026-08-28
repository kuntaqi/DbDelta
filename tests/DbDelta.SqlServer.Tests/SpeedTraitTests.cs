using System.Reflection;

namespace DbDelta.SqlServer.Tests;

// The split is only worth having if the two halves still add up to the whole, and a trait is a string in an
// attribute — nothing stops one being misspelled, and a misspelled one drops its tests out of *both*
// filters at once. Counting them here is cheaper than noticing a month later that a class stopped running.
//
// No database needed: this reads its own assembly.
public sealed class SpeedTraitTests
{
    private static readonly IReadOnlyList<Type> TestClasses = typeof(SpeedTraitTests).Assembly
        .GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Tests", StringComparison.Ordinal))
        .Where(t => t.GetMethods().Any(m => m.GetCustomAttributes()
            .Any(a => a.GetType().Name is "FactAttribute" or "SkippableFactAttribute" or "TheoryAttribute")))
        .ToList();

    // TraitAttribute keeps its pair to itself — xUnit reads it through a discoverer — so the constructor
    // arguments are what there is to look at.
    private static bool IsSlow(Type type) =>
        type.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(TraitAttribute))
            .Any(a => a.ConstructorArguments.Count == 2
                && (string?)a.ConstructorArguments[0].Value == "Speed"
                && (string?)a.ConstructorArguments[1].Value == "Slow");

    private static bool CreatesADatabase(Type type) =>
        type.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(LocalDbFixture)));

    [Fact]
    public void Every_test_class_is_on_one_side_of_the_split_or_the_other()
    {
        // Trivially true by construction — a class is slow or it is not — so what this really asserts is
        // that the reflection above finds the classes at all. A typo in the discovery would make every
        // check in this file vacuous.
        Assert.True(TestClasses.Count >= 20, $"only found {TestClasses.Count} test classes; discovery is wrong");
    }

    // The rule the split is defined by, enforced rather than described: a class that takes the LocalDB
    // fixture can create databases, so it belongs in the slow half. The exceptions are listed by name so
    // that adding one is a deliberate edit rather than a silent drift.
    [Fact]
    public void A_class_that_takes_the_localdb_fixture_is_marked_slow()
    {
        // These take the fixture but only ever read the shared source and target pair through it.
        var readOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(SchemaReaderTests),
            nameof(DataCompareTests),
            nameof(EndToEndCompareTests),
            nameof(SchemaStepTests),
            nameof(FingerprintReaderTests),
            nameof(KeyProfileTests)
        };

        var wrong = TestClasses
            .Where(CreatesADatabase)
            .Where(t => !IsSlow(t) && !readOnly.Contains(t.Name))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(wrong);
    }

    // The other direction: a class marked slow that does not touch a database at all is mislabelled, and
    // would be sitting out the fast run for no reason.
    [Fact]
    public void Nothing_is_marked_slow_without_needing_a_database()
    {
        var wrong = TestClasses
            .Where(t => IsSlow(t) && !CreatesADatabase(t))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(wrong);
    }
}
