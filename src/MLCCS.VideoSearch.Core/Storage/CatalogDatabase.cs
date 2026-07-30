using System.Reflection;
using Microsoft.Data.Sqlite;

namespace MLCCS.VideoSearch.Core.Storage;

public sealed class CatalogDatabase(string databasePath)
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var pragmaText in new[] { "PRAGMA foreign_keys=ON", "PRAGMA journal_mode=WAL", "PRAGMA synchronous=FULL" })
        {
            var pragma = connection.CreateCommand(); pragma.CommandText = pragmaText;
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var assembly = typeof(CatalogDatabase).Assembly;
        var migrations = assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order().ToArray();
        if (migrations.Length == 0) throw new InvalidOperationException("No embedded database migrations were found.");
        foreach (var name in migrations)
        {
            await using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing migration {name}");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (statement.StartsWith("PRAGMA ", StringComparison.OrdinalIgnoreCase)) continue;
                var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
