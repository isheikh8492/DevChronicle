namespace DevChronicle.Models;

public class DaySummary
{
    public int SessionId { get; set; }
    public DateTime Day { get; set; }
    public string BulletsText { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = "v1";
    public string InputHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class ResumeSummary
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public DateTime RangeStart { get; set; }
    public DateTime RangeEnd { get; set; }
    public string BulletsText { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = "v1";
    public string InputHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
