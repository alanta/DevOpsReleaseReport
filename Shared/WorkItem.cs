using System.Collections.Generic;

namespace ReleaseReport.Shared;

public class WorkItem
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Url { get; set; }
    public IList<WorkItem>? Tasks { get; set; }
}