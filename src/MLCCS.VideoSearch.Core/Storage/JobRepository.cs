using Microsoft.Data.Sqlite;
using MLCCS.VideoSearch.Core.Indexing;

namespace MLCCS.VideoSearch.Core.Storage;

public sealed class JobRepository(string connectionString)
{
    public async Task<DurableJobState?> ClaimAsync(string kind, string owner, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var recover = connection.CreateCommand(); recover.Transaction = transaction;
        recover.CommandText = "UPDATE jobs SET status='Queued',lease_owner=NULL,lease_expires_utc=NULL,stage='recovered' WHERE status='Running' AND lease_expires_utc < $now";
        recover.Parameters.AddWithValue("$now", now.ToString("O")); await recover.ExecuteNonQueryAsync(cancellationToken);
        var select = connection.CreateCommand(); select.Transaction = transaction;
        select.CommandText = "SELECT id,status,attempt,maximum_attempts,stage,progress,lease_owner,lease_expires_utc,error_code FROM jobs WHERE status='Queued' AND kind=$kind ORDER BY created_utc LIMIT 1";
        select.Parameters.AddWithValue("$kind", kind);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) { await transaction.CommitAsync(cancellationToken); return null; }
        var state = Read(reader).Lease(owner, now, lease); await reader.DisposeAsync();
        var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE jobs SET status=$status,attempt=$attempt,lease_owner=$owner,lease_expires_utc=$expires,updated_utc=$now WHERE id=$id AND status='Queued'";
        update.Parameters.AddWithValue("$status", state.Status.ToString()); update.Parameters.AddWithValue("$attempt", state.Attempt);
        update.Parameters.AddWithValue("$owner", (object?)state.LeaseOwner ?? DBNull.Value); update.Parameters.AddWithValue("$expires", state.LeaseExpiresUtc?.ToString("O") ?? (object)DBNull.Value);
        update.Parameters.AddWithValue("$now", now.ToString("O")); update.Parameters.AddWithValue("$id", state.Id.ToString());
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) { await transaction.RollbackAsync(cancellationToken); return null; }
        await transaction.CommitAsync(cancellationToken); return state;
    }

    public async Task CheckpointAsync(Guid id, string owner, string stage, double progress, string checkpointJson, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString); await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET stage=$stage,progress=$progress,checkpoint_json=$checkpoint,updated_utc=$now WHERE id=$id AND status='Running' AND lease_owner=$owner";
        command.Parameters.AddWithValue("$stage", stage); command.Parameters.AddWithValue("$progress", Math.Clamp(progress, 0, 1)); command.Parameters.AddWithValue("$checkpoint", checkpointJson);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$owner", owner);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("INDEX_LEASE_LOST");
    }

    private static DurableJobState Read(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), Enum.Parse<JobStatus>(r.GetString(1), true), r.GetInt32(2), r.GetInt32(3), r.GetString(4), r.GetDouble(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8));
}

