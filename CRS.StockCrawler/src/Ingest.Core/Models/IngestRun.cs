using Ingest.Core.Enums;

namespace Ingest.Core.Models;

public sealed class IngestRun
{
    public long RunId { get; set; }
    public string JobName { get; set; } = "";
    public ProviderId? Provider { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int? OkCount { get; set; }
    public int? ErrCount { get; set; }
    public int? RowsWritten { get; set; }
    public string? Note { get; set; }
}
