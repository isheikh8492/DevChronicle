namespace DevChronicle.Models;

public class Day
{
    public int SessionId { get; set; }
    public DateTime Date { get; set; }
    public int CommitCount { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public DayStatus Status { get; set; } = DayStatus.Mined;
}

public enum DayStatus
{
    Mined,
    Summarized,
    Approved
}
