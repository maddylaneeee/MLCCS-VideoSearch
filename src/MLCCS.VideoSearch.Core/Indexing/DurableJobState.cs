namespace MLCCS.VideoSearch.Core.Indexing;

public enum JobStatus { Queued, Running, Paused, Completed, Skipped, FailedAwaitingConfirmation, FailedConfirmed, Cancelled }

public sealed record DurableJobState(
    Guid Id, JobStatus Status, int Attempt, int MaximumAttempts, string Stage, double Progress,
    string? LeaseOwner, DateTimeOffset? LeaseExpiresUtc, string? ErrorCode)
{
    public bool IsInitialTerminal => Status is JobStatus.Completed or JobStatus.Skipped or JobStatus.FailedConfirmed;

    public DurableJobState Recover(DateTimeOffset now) =>
        Status == JobStatus.Running && LeaseExpiresUtc <= now
            ? this with { Status = JobStatus.Queued, LeaseOwner = null, LeaseExpiresUtc = null, Stage = "recovered" }
            : this;

    public DurableJobState Lease(string owner, DateTimeOffset now, TimeSpan duration)
    {
        if (Status != JobStatus.Queued) throw new InvalidOperationException("Only queued jobs may be leased.");
        if (Attempt >= MaximumAttempts) return this with { Status = JobStatus.FailedAwaitingConfirmation, ErrorCode = "INDEX_RETRY_EXHAUSTED" };
        return this with { Status = JobStatus.Running, Attempt = Attempt + 1, LeaseOwner = owner, LeaseExpiresUtc = now + duration };
    }
}

