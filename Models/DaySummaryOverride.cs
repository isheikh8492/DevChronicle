namespace DevChronicle.Models;

public class DaySummaryOverride
{
    public int SessionId { get; set; }
    public DateTime Day { get; set; }
    public string BulletsText { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

