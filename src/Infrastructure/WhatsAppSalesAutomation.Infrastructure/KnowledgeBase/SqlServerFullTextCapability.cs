using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

/// <summary>Whether the keyword leg can run through SQL Server Full-Text Search: the feature is
/// installed AND the chunk table's full-text index exists.</summary>
public interface IKeywordSearchCapability
{
    bool SupportsFullText { get; }
}

/// <summary>
/// Probes once at startup, like <see cref="SqlServerVectorCapability"/>, and for the same reasons: ask
/// the server the question that matters rather than comparing version strings, decide once, and log
/// the outcome loudly rather than degrading silently.
///
/// Both halves are checked. The feature being installed does not mean the index exists (the migration
/// only creates it where it can), and an index existing on a server whose feature was later removed
/// would fail at query time rather than here.
/// </summary>
public sealed class SqlServerFullTextCapability : IKeywordSearchCapability
{
    public SqlServerFullTextCapability(string connectionString, ILogger<SqlServerFullTextCapability> logger)
    {
        var installed = false;
        var indexed = false;

        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT CAST(ISNULL(SERVERPROPERTY('IsFullTextInstalled'), 0) AS int),
       CASE WHEN EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('KnowledgeBaseChunks')) THEN 1 ELSE 0 END";

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                installed = reader.GetInt32(0) == 1;
                indexed = reader.GetInt32(1) == 1;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not probe SQL Server Full-Text Search; using in-application keyword search.");
        }

        SupportsFullText = installed && indexed;

        if (SupportsFullText)
        {
            logger.LogInformation("Knowledge base keyword search: using SQL Server Full-Text Search.");
        }
        else
        {
            logger.LogWarning(
                "Knowledge base keyword search: Full-Text Search is {State}, so keyword matching runs in the " +
                "application (BM25 over eligible chunks). Correct, but it reads every eligible chunk's text per " +
                "query and does not scale to very large knowledge bases. Install the Full-Text feature and " +
                "re-run migrations to use the native path.",
                installed ? "installed but the index is missing" : "not installed");
        }
    }

    public bool SupportsFullText { get; }
}
