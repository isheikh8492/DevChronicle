namespace DevChronicle.Services;

public interface ISummarizationProvider
{
    string ProviderId { get; }

    bool CanHandleModel(string modelName);

    Task<SummarizationProviderResponse> CompleteAsync(
        SummarizationProviderRequest request,
        CancellationToken cancellationToken);
}

public sealed record SummarizationProviderRequest(
    string ApiKey,
    string Model,
    string SystemPrompt,
    string UserPrompt,
    double Temperature,
    int MaxCompletionTokens);

public sealed record SummarizationProviderResponse(
    string Content,
    bool ReachedMaxTokens);
