using System.Text.RegularExpressions;

namespace SqlClone.IntegrationTests;

/// <summary>
/// The catalog queries that depend on <see cref="SourceFeatures"/>. No container image older than SQL Server 2017
/// exists, so the case where the features are missing is checked here rather than against a real server.
/// </summary>
public sealed class SourceCatalogQueryTests
{
    private static readonly SourceFeatures None = new(false, false, false, false);
    private static readonly SourceFeatures All = new(true, true, true, true);

    [Fact]
    public void Without_the_features_the_queries_name_none_of_their_columns_or_views()
    {
        string[] queries = [SourceCatalog.TablesQuery(None), SourceCatalog.CopyableColumnsQuery(None)];

        foreach (var name in new[] { "is_external", "generated_always_type", "graph_type", "sys.sequences" })
            Assert.All(queries, q => Assert.DoesNotContain(name, q, StringComparison.OrdinalIgnoreCase));
        Assert.Null(SourceCatalog.SequencesQuery(None));
    }

    [Fact]
    public void With_every_feature_the_queries_are_unchanged()
    {
        // The queries as they were before the features were probed for.
        Assert.Equal(Normalize("""
            SELECT t.object_id, s.name, t.name
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0 AND t.is_external = 0
            ORDER BY t.object_id
            """), Normalize(SourceCatalog.TablesQuery(All)));

        Assert.Equal(Normalize("""
            SELECT s.name, sq.name, CONVERT(nvarchar(50), sq.current_value)
            FROM sys.sequences sq JOIN sys.schemas s ON s.schema_id = sq.schema_id
            WHERE sq.is_ms_shipped = 0 AND sq.last_used_value IS NOT NULL
            """), Normalize(SourceCatalog.SequencesQuery(All)!));

        Assert.Equal(Normalize("""
            SELECT c.name, ty.is_assembly_type, c.is_identity
            FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = @id
              AND c.is_computed = 0
              AND c.system_type_id <> 189
              AND c.generated_always_type = 0
              AND c.graph_type IS NULL
            ORDER BY c.column_id
            """), Normalize(SourceCatalog.CopyableColumnsQuery(All)));
    }

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();
}
