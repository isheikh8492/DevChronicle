using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DevChronicle.Services;

public sealed class AnthropicSummarizationProvider : ISummarizationProvider
{
    private static readonly HttpClient Http = new HttpClient
    {
        BaseAddress = new Uri("https://api.anthropic.com/v1/")
    };

    public string ProviderId => "anthropic";

    public bool CanHandleModel(string modelName) =>
        !string.IsNullOrWhiteSpace(modelName) &&
        modelName.StartsWith("claude-", StringComparison.OrdinalIgnoreCase);

    public async Task<SummarizationProviderResponse> CompleteAsync(
        SummarizationProviderRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = request.Model,
            max_tokens = request.MaxCompletionTokens,
            temperature = request.Temperature,
            system = request.SystemPrompt,
            messages = new[]
            {
                new { role = "user", content = request.UserPrompt }
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "messages");
        httpRequest.Headers.Add("x-api-key", request.ApiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic API error: {response.StatusCode} - {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var contentBuilder = new StringBuilder();
        if (root.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in contentProp.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var typeProp) &&
                    typeProp.ValueKind == JsonValueKind.String &&
                    string.Equals(typeProp.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                    part.TryGetProperty("text", out var textProp) &&
                    textProp.ValueKind == JsonValueKind.String)
                {
                    contentBuilder.AppendLine(textProp.GetString());
                }
            }
        }

        var stopReason = root.TryGetProperty("stop_reason", out var stopReasonProp) &&
                         stopReasonProp.ValueKind == JsonValueKind.String
            ? stopReasonProp.GetString()
            : null;

        return new SummarizationProviderResponse(
            contentBuilder.ToString().Trim(),
            string.Equals(stopReason, "max_tokens", StringComparison.OrdinalIgnoreCase));
    }
}
