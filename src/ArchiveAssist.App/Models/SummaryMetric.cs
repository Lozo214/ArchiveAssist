namespace ArchiveAssist.App.Models;

public sealed record SummaryMetric(string Metric, string Value, string? ReportFilter = null)
{
    public bool IsActionable => ReportFilter is not null;
    public string ActionHint => IsActionable ? "Double-click to view matching Report rows" : string.Empty;
}
