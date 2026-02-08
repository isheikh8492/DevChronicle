namespace DevChronicle.Models;

public class DaySummarizationPayload
{
    public int SessionId { get; set; }
    public DateTime Day { get; set; }
    public string MasterPrompt { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int MaxBullets { get; set; }
    public int MaxCompletionTokensPerCall { get; set; }
    public string PromptVersion { get; set; } = "v1";
    public string InputHash { get; set; } = string.Empty;
}
