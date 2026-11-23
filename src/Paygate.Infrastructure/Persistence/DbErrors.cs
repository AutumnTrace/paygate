using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Paygate.Infrastructure.Persistence;

internal static class DbErrors
{
    private const int SqliteConstraint = 19;

    /// <summary>True when the insert lost a race on a primary key or unique index.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException switch
    {
        PostgresException pg => pg.SqlState == PostgresErrorCodes.UniqueViolation,
        SqliteException sqlite => sqlite.SqliteErrorCode == SqliteConstraint,
        _ => false,
    };
}
