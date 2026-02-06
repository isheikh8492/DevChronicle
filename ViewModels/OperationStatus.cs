namespace DevChronicle.ViewModels;

public enum OperationState
{
    Idle = 0,
    Running = 1,
    Success = 2,
    Canceled = 3,
    Error = 4,
    NeedsInput = 5
}

public sealed record OperationStatus(
    OperationState State,
    string Message,
    int CurrentStep,
    int TotalSteps);

public static class OperationStatusFormatter
{
    public static string FormatProgress(string verb, int current, int total)
    {
        if (total > 0)
            return $"{verb} ({current}/{total})";

        return $"{verb}...";
    }

    public static string FormatTerminal(OperationState state, string detail)
    {
        return state switch
        {
            OperationState.Success => detail,
            OperationState.Canceled => detail,
            OperationState.Error => detail,
            OperationState.NeedsInput => detail,
            _ => detail
        };
    }
}
