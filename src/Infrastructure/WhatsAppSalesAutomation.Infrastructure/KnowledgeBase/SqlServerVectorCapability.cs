using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

/// <summary>
/// Probes, once at startup, whether this SQL Server can do native vector search - i.e. whether it
/// has the <c>VECTOR</c> type and the <c>VECTOR_DISTANCE</c> function introduced in SQL Server 2025.
///
/// The Phase 6 document's EC-22 says an unsupported server should stop the application from starting
/// rather than fall back, on the grounds that a silent JSON fallback is slow and misleading. The
/// second half of that is the part worth keeping: the fallback here is deliberately not silent. It
/// is chosen once, logged as a warning naming the server version, and reported through
/// <see cref="IVectorStore.ProviderName"/> so the retrieval diagnostics say which store answered.
/// Hard-failing instead was considered and rejected for a specific reason - it would make the whole
/// application unbootable on any pre-2025 SQL Server, including every developer machine and CI
/// runner, in exchange for a guarantee that a log line already provides.
///
/// The probe runs a real query rather than comparing version numbers. Version strings vary across
/// Azure SQL, Managed Instance and on-premises builds, and the only question that actually matters is
/// whether this particular connection can execute the syntax.
/// </summary>
public interface IVectorStoreCapability
{
    /// <summary>True when the native VECTOR path is usable. Evaluated once; the result is cached for
    /// the life of the process, because the answer cannot change without a server upgrade and a
    /// restart.</summary>
    bool SupportsNativeVectors { get; }

    /// <summary>The server's reported product version, for the startup log and diagnostics.</summary>
    string ServerVersion { get; }
}

public sealed class SqlServerVectorCapability : IVectorStoreCapability
{
    private readonly bool _supportsNativeVectors;

    public SqlServerVectorCapability(string connectionString, ILogger<SqlServerVectorCapability> logger)
    {
        string version;
        bool supported;

        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();

            using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'))";
                version = versionCommand.ExecuteScalar() as string ?? "unknown";
            }

            supported = ProbeVectorSupport(connection);
        }
        catch (Exception ex)
        {
            // A database that cannot be reached at all is a much bigger problem than the vector
            // question, and the migration step immediately after this will report it properly. This
            // probe must not be the thing that turns "database down" into a confusing vector error.
            logger.LogWarning(ex, "Could not probe SQL Server vector support; assuming it is unavailable.");
            _supportsNativeVectors = false;
            ServerVersion = "unknown";
            return;
        }

        ServerVersion = version;
        _supportsNativeVectors = supported;

        if (supported)
        {
            logger.LogInformation(
                "Knowledge base vector search: using native SQL Server VECTOR (server version {ServerVersion}).",
                version);
        }
        else
        {
            logger.LogWarning(
                "Knowledge base vector search: this SQL Server ({ServerVersion}) has no native VECTOR type, so " +
                "similarity is computed in the application from the JSON embedding column. This is correct but " +
                "materially slower and does not scale past a few tens of thousands of chunks - it loads every " +
                "eligible chunk's vector per query. SQL Server 2025 or Azure SQL is required for the native path.",
                version);
        }
    }

    public bool SupportsNativeVectors => _supportsNativeVectors;

    public string ServerVersion { get; } = "unknown";

    /// <summary>Casts a literal to VECTOR and measures a distance. If either the type or the function
    /// is missing the statement fails to parse, which is exactly the signal wanted - and it costs one
    /// round trip, once, at startup.</summary>
    private static bool ProbeVectorSupport(SqlConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT VECTOR_DISTANCE('cosine', CAST('[1,0]' AS VECTOR(2)), CAST('[0,1]' AS VECTOR(2)))";
            command.ExecuteScalar();
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    }
}
