using Microsoft.EntityFrameworkCore;
using SmartFactory.MES.Data;
using SmartFactory.MES.Models.Jobs;

namespace SmartFactory.MES.Services.Jobs
{
    /// <inheritdoc cref="IJobRunRecorder"/>
    public class JobRunRecorder : IJobRunRecorder
    {
        private readonly ApplicationDbContext _db;

        public JobRunRecorder(ApplicationDbContext db) => _db = db;

        public Task<bool> HasSucceededOnAsync(string jobName, DateOnly jobDate, CancellationToken ct = default)
            => _db.ScheduledJobRuns.AsNoTracking()
                .AnyAsync(r => r.JobName == jobName && r.JobDate == jobDate && r.Succeeded, ct);

        public async Task RecordAsync(ScheduledJobRun run, CancellationToken ct = default)
        {
            _db.ScheduledJobRuns.Add(run);
            await _db.SaveChangesAsync(ct);
        }
    }
}
