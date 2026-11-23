namespace Paygate.Infrastructure.Persistence;

public enum DatabaseProvider
{
    /// <summary>File-based, zero setup. The default for <c>dotnet run</c>.</summary>
    Sqlite,

    /// <summary>The production target, used by docker compose and the integration tests.</summary>
    Postgres,
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public const string ConnectionStringName = "Paygate";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>Create the schema on start-up if it does not exist.</summary>
    public bool CreateSchemaOnStartup { get; set; } = true;
}
