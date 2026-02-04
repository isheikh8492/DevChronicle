namespace DevChronicle.Models;

public class Checkpoint
{
    public int SessionId { get; set; }
    public CheckpointPhase Phase { get; set; }
    public string CursorKey { get; set; } = string.Empty;
    public string CursorValue { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

public enum CheckpointPhase
{
    Mine,
    Summarize,
    Resume
}
