namespace DevChronicle.Models;

public class Session
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RepoPath { get; set; } = string.Empty;
    public string MainBranch { get; set; } = "main";
    public DateTime CreatedAt { get; set; }
    public string AuthorFiltersJson { get; set; } = "[]";
    public string OptionsJson { get; set; } = "{}";
    public DateTime? RangeStart { get; set; }
    public DateTime? RangeEnd { get; set; }
}

public class SessionOptions
{
    public bool IncludeMerges { get; set; }
    public bool IncludeDiffs { get; set; }
    public int WindowSizeDays { get; set; } = 14;
    public int MaxBulletsPerDay { get; set; } = 6;
    public int MaxTokens { get; set; } = 2000;
    public string BackfillOrder { get; set; } = "OldestFirst";
    public int OverlapDays { get; set; } = 1;
    public bool FillGapsFirst { get; set; }
    public RefScope RefScope { get; set; } = RefScope.LocalBranchesOnly;
    public bool TrackIntegrations { get; set; } = true;
}

public enum RefScope
{
    LocalBranchesOnly = 0,
    LocalPlusRemotes = 1,
    AllRefs = 2
}

public class AuthorFilter
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}
