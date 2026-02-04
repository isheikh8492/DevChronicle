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
}

public class AuthorFilter
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}
