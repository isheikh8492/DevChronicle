namespace DevChronicle.Models;

public class CommitParent
{
    public int SessionId { get; set; }
    public string ChildSha { get; set; } = string.Empty;
    public string ParentSha { get; set; } = string.Empty;
    public int ParentOrder { get; set; }
}

public class CommitBranchLabel
{
    public int SessionId { get; set; }
    public string Sha { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public string LabelMethod { get; set; } = "name-rev";
    public DateTime CapturedAt { get; set; }
}

public class BranchSnapshot
{
    public int SessionId { get; set; }
    public DateTime CapturedAt { get; set; }
    public string RefName { get; set; } = string.Empty;
    public string HeadSha { get; set; } = string.Empty;
    public DateTime? HeadDate { get; set; }
    public bool IsRemote { get; set; }
}

public class IntegrationEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int SessionId { get; set; }
    public string? AnchorSha { get; set; }
    public DateTime OccurredAt { get; set; }
    public string Method { get; set; } = "MergeCommit";
    public string Confidence { get; set; } = "High";
    public string DetailsJson { get; set; } = "{}";
}

public class IntegrationEventCommit
{
    public string IntegrationEventId { get; set; } = string.Empty;
    public int SessionId { get; set; }
    public string Sha { get; set; } = string.Empty;
}

public class CommitBranchRow
{
    public string Sha { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTime AuthorDate { get; set; }
    public string? BranchName { get; set; }
}

public class ParsedCommit
{
    public Commit Commit { get; set; } = new();
    public List<string> Parents { get; set; } = new();
}
