using System.Collections.Generic;

namespace ReleaseReport.Shared;

public class Release
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Url { get; set; }

    public IList<WorkItem> WorkItems { get; set; } = new List<WorkItem>();
}