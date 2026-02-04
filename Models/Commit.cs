namespace DevChronicle.Models;

public class Commit
{
    public int SessionId { get; set; }
    public string Sha { get; set; } = string.Empty;
    public DateTime AuthorDate { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public string FilesJson { get; set; } = "[]";
    public bool IsMerge { get; set; }
    public bool? ReachableFromMain { get; set; }
}

public class CommitFile
{
    public string Path { get; set; } = string.Empty;
    public int Additions { get; set; }
    public int Deletions { get; set; }
}
