using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DevChronicle.Services;

public sealed class OpenAiSummarizationProvider : ISummarizationProvider
{
    private static readonly HttpClient Http = new HttpClient
    {
        BaseAddress = new Uri("https://api.openai.com/v1/")
    };

    public string ProviderId => "openai";

    public bool CanHandleModel(string modelName) =>
        !string.IsNullOrWhiteSpace(modelName) &&
        !modelName.StartsWith("claude-", StringComparison.OrdinalIgnoreCase);

    public async Task<SummarizationProviderResponse> CompleteAsync(
        SummarizationProviderRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = request.Model,
            messages = new[]
            {
                new { role = "developer", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            },
            temperature = request.Temperature,
            max_completion_tokens = request.MaxCompletionTokens,
            store = false
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI API error: {response.StatusCode} - {json}");

        using var doc = JsonDocument.Parse(json);
        var choice0 = doc.RootElement.GetProperty("choices")[0];
        var content = choice0.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        var finishReason = choice0.TryGetProperty("finish_reason", out var finishReasonProp) &&
                           finishReasonProp.ValueKind == JsonValueKind.String
            ? finishReasonProp.GetString()
            : null;

        return new SummarizationProviderResponse(
            content,
            string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase));
    }
}
