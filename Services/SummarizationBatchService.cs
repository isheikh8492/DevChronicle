using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevChronicle.Models;

namespace DevChronicle.Services;

public class SummarizationBatchService
{
    public const string ResultsNotReadyErrorMessage = "Batch result files are not ready yet.";

    private static readonly HttpClient Http = new HttpClient
    {
        BaseAddress = new Uri("https://api.openai.com/v1/")
    };

    private readonly DatabaseService _databaseService;
    private readonly SummarizationService _summarizationService;

    public SummarizationBatchService(DatabaseService databaseService, SummarizationService summarizationService)
    {
        _databaseService = databaseService;
        _summarizationService = summarizationService;
    }

    public async Task<SummarizationBatch> SubmitPendingDaysBatchAsync(
        int sessionId,
        int maxBullets,
        int maxDaysPerSubmit,
        CancellationToken cancellationToken)
    {
        var apiKey = await _summarizationService.GetConfiguredApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Missing OPENAI_API_KEY. Set it in Settings or environment.");

        var days = (await _databaseService.GetDaysAsync(sessionId))
            .Where(d => d.Status == DayStatus.Mined)
            .OrderBy(d => d.Date)
            .Take(Math.Clamp(maxDaysPerSubmit, 1, 60))
            .ToList();

        if (days.Count == 0)
            throw new InvalidOperationException("No pending days to summarize.");

        var payloads = new List<DaySummarizationPayload>();
        foreach (var day in days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await _summarizationService.BuildDaySummarizationPayloadAsync(
                sessionId,
                day.Date,
                maxBullets,
                cancellationToken);
            if (payload != null)
                payloads.Add(payload);
        }

        if (payloads.Count == 0)
            throw new InvalidOperationException("No pending day payloads could be created.");

        var submitRunId = Guid.NewGuid().ToString("N");

        var lines = payloads.Select(p => JsonSerializer.Serialize(new
        {
            custom_id = BuildCustomId(p.SessionId, p.Day, submitRunId),
            method = "POST",
            url = "/v1/chat/completions",
            body = new
            {
                model = p.Model,
                messages = new[]
                {
                    new { role = "developer", content = p.MasterPrompt },
                    new { role = "user", content = p.Prompt }
                },
                temperature = 0.2,
                max_completion_tokens = p.MaxCompletionTokensPerCall,
                store = false
            }
        }));

        var jsonl = string.Join("\n", lines);
        var inputFileId = await UploadBatchInputFileAsync(jsonl, apiKey, cancellationToken);
        var openAiBatch = await CreateBatchAsync(inputFileId, apiKey, cancellationToken);

        var now = DateTime.UtcNow;
        var batch = new SummarizationBatch
        {
            SessionId = sessionId,
            OpenAiBatchId = openAiBatch.Id,
            Status = MapBatchStatus(openAiBatch.Status),
            InputFileId = inputFileId,
            OutputFileId = openAiBatch.OutputFileId,
            ErrorFileId = openAiBatch.ErrorFileId,
            CreatedAt = now,
            UpdatedAt = now,
            LastError = null
        };
        batch.Id = await _databaseService.CreateSummarizationBatchAsync(batch);

        var items = payloads.Select(p => new SummarizationBatchItem
        {
            BatchId = batch.Id,
            SessionId = p.SessionId,
            Day = p.Day,
            CustomId = BuildCustomId(p.SessionId, p.Day, submitRunId),
            Model = p.Model,
            PromptVersion = p.PromptVersion,
            InputHash = p.InputHash,
            MaxBullets = p.MaxBullets,
            Status = SummarizationBatchItemStatuses.Pending
        });
        await _databaseService.UpsertSummarizationBatchItemsAsync(items);

        return batch;
    }

    public async Task<SummarizationBatch> RefreshBatchStatusAsync(int localBatchId, CancellationToken cancellationToken)
    {
        var batch = await _databaseService.GetSummarizationBatchByIdAsync(localBatchId)
            ?? throw new InvalidOperationException($"Batch {localBatchId} not found.");
        if (string.IsNullOrWhiteSpace(batch.OpenAiBatchId))
            throw new InvalidOperationException($"Batch {localBatchId} has no OpenAI batch id.");

        var apiKey = await _summarizationService.GetConfiguredApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Missing OPENAI_API_KEY. Set it in Settings or environment.");

        var openAiBatch = await GetBatchAsync(batch.OpenAiBatchId, apiKey, cancellationToken);
        batch.Status = MapBatchStatus(openAiBatch.Status);
        batch.OutputFileId = openAiBatch.OutputFileId;
        batch.ErrorFileId = openAiBatch.ErrorFileId;
        batch.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(openAiBatch.LastError))
            batch.LastError = openAiBatch.LastError;

        await _databaseService.UpdateSummarizationBatchAsync(batch);
        return batch;
    }

    public async Task<BatchApplyResult> ApplyBatchResultsAsync(int localBatchId, CancellationToken cancellationToken)
    {
        var batch = await _databaseService.GetSummarizationBatchByIdAsync(localBatchId)
            ?? throw new InvalidOperationException($"Batch {localBatchId} not found.");

        var items = await _databaseService.GetSummarizationBatchItemsAsync(localBatchId);
        if (items.Count == 0)
            return new BatchApplyResult(0, 0, "No batch items to apply.");

        var apiKey = await _summarizationService.GetConfiguredApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Missing OPENAI_API_KEY. Set it in Settings or environment.");

        // Refresh once right before apply so we do not miss newly materialized output/error file ids.
        if (!string.IsNullOrWhiteSpace(batch.OpenAiBatchId))
        {
            var latest = await GetBatchAsync(batch.OpenAiBatchId, apiKey, cancellationToken);
            batch.Status = MapBatchStatus(latest.Status);
            batch.OutputFileId = latest.OutputFileId;
            batch.ErrorFileId = latest.ErrorFileId;
            batch.UpdatedAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(latest.LastError))
                batch.LastError = latest.LastError;
            await _databaseService.UpdateSummarizationBatchAsync(batch);
        }

        if (string.IsNullOrWhiteSpace(batch.OutputFileId) && string.IsNullOrWhiteSpace(batch.ErrorFileId))
            return new BatchApplyResult(0, 0, ResultsNotReadyErrorMessage);

        var outputLines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var outputErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(batch.OutputFileId))
        {
            var outputJsonl = await DownloadFileContentAsync(batch.OutputFileId, apiKey, cancellationToken);
            ParseOutputJsonl(outputJsonl, outputLines, outputErrors);
        }

        if (!string.IsNullOrWhiteSpace(batch.ErrorFileId))
        {
            var errorJsonl = await DownloadFileContentAsync(batch.ErrorFileId, apiKey, cancellationToken);
            ParseErrorJsonl(errorJsonl, outputErrors);
        }

        if (outputLines.Count == 0 && outputErrors.Count == 0)
            return new BatchApplyResult(0, 0, ResultsNotReadyErrorMessage);

        var successCount = 0;
        var failedCount = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(item.Status, SummarizationBatchItemStatuses.Succeeded, StringComparison.OrdinalIgnoreCase))
            {
                successCount++;
                continue;
            }

            if (!outputLines.TryGetValue(item.CustomId, out var content))
            {
                var missingErr = outputErrors.TryGetValue(item.CustomId, out var knownError)
                    ? knownError
                    : "Missing output for custom_id.";
                await _databaseService.UpdateSummarizationBatchItemStatusAsync(
                    item.BatchId, item.Day, SummarizationBatchItemStatuses.Failed, missingErr);
                failedCount++;
                continue;
            }

            var bullets = _summarizationService.ValidateBulletsForStorage(content, item.MaxBullets);
            if (bullets.Count == 0)
            {
                await _databaseService.UpdateSummarizationBatchItemStatusAsync(
                    item.BatchId, item.Day, SummarizationBatchItemStatuses.Failed, "No valid bullet output.");
                failedCount++;
                continue;
            }

            var payload = new DaySummarizationPayload
            {
                SessionId = item.SessionId,
                Day = item.Day,
                PromptVersion = item.PromptVersion,
                InputHash = item.InputHash,
                Model = item.Model,
                MaxBullets = item.MaxBullets
            };
            await _summarizationService.StoreDaySummaryAsync(payload, bullets);
            await _databaseService.UpdateSummarizationBatchItemStatusAsync(
                item.BatchId, item.Day, SummarizationBatchItemStatuses.Succeeded, null);
            successCount++;
        }

        batch.Status = failedCount > 0 ? SummarizationBatchStatuses.PartialFailure : SummarizationBatchStatuses.Completed;
        batch.UpdatedAt = DateTime.UtcNow;
        if (failedCount > 0 && string.IsNullOrWhiteSpace(batch.LastError))
            batch.LastError = "One or more batch items failed.";
        await _databaseService.UpdateSummarizationBatchAsync(batch);

        return new BatchApplyResult(successCount, failedCount, null);
    }

    public async Task<List<SummarizationBatch>> GetActiveBatchesAsync()
    {
        return await _databaseService.GetActiveSummarizationBatchesAsync();
    }

    public async Task<int> CancelActiveBatchesForSessionAsync(int sessionId, CancellationToken cancellationToken)
    {
        var batches = (await _databaseService.GetActiveSummarizationBatchesAsync())
            .Where(b => b.SessionId == sessionId)
            .ToList();

        var canceled = 0;
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await CancelBatchAsync(batch.Id, cancellationToken))
                canceled++;
        }

        return canceled;
    }

    public async Task<bool> CancelBatchAsync(int localBatchId, CancellationToken cancellationToken)
    {
        var batch = await _databaseService.GetSummarizationBatchByIdAsync(localBatchId);
        if (batch == null)
            return false;

        if (SummarizationBatchStatuses.IsTerminal(batch.Status))
            return false;

        var apiKey = await _summarizationService.GetConfiguredApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Missing OPENAI_API_KEY. Set it in Settings or environment.");

        if (!string.IsNullOrWhiteSpace(batch.OpenAiBatchId))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"batches/{batch.OpenAiBatchId}/cancel");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // OpenAI can race to a terminal state between local polling intervals.
                // In that case cancel returns 409 (cannot cancel failed/completed/cancelled batch).
                // Treat it as a normal terminal transition instead of surfacing a stop failure.
                if ((int)response.StatusCode == 409 &&
                    body.Contains("Cannot cancel a batch with status", StringComparison.OrdinalIgnoreCase))
                {
                    var latest = await GetBatchAsync(batch.OpenAiBatchId, apiKey, cancellationToken);
                    batch.Status = MapBatchStatus(latest.Status);
                    batch.OutputFileId = latest.OutputFileId;
                    batch.ErrorFileId = latest.ErrorFileId;
                    batch.UpdatedAt = DateTime.UtcNow;
                    batch.LastError = latest.LastError ?? batch.LastError;
                    await _databaseService.UpdateSummarizationBatchAsync(batch);
                    await _databaseService.MarkPendingSummarizationBatchItemsFailedAsync(
                        batch.Id,
                        "Batch became terminal before cancel completed.");
                    await TryApplyAvailableResultsAsync(batch.Id, cancellationToken);
                    return false;
                }

                throw new InvalidOperationException($"OpenAI API error: {response.StatusCode} - {body}");
            }
        }

        batch.Status = SummarizationBatchStatuses.Canceled;
        batch.UpdatedAt = DateTime.UtcNow;
        batch.LastError = "Canceled by user.";
        await _databaseService.UpdateSummarizationBatchAsync(batch);
        await _databaseService.MarkPendingSummarizationBatchItemsFailedAsync(batch.Id, "Canceled by user.");
        await TryApplyAvailableResultsAsync(batch.Id, cancellationToken);
        return true;
    }

    private static string BuildCustomId(int sessionId, DateTime day, string runId) =>
        $"session:{sessionId}:day:{day:yyyy-MM-dd}:run:{runId}";

    private async Task<string> UploadBatchInputFileAsync(string jsonl, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "files");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("batch"), "purpose");
        content.Add(new StringContent(jsonl, Encoding.UTF8), "file", "summarization-batch.jsonl");
        request.Content = content;

        using var response = await Http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI API error: {response.StatusCode} - {json}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException("Missing file id in upload response.");
    }

    private async Task<OpenAiBatchSnapshot> CreateBatchAsync(string inputFileId, string apiKey, CancellationToken cancellationToken)
    {
        var payload = new
        {
            input_file_id = inputFileId,
            endpoint = "/v1/chat/completions",
            completion_window = "24h"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "batches");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI API error: {response.StatusCode} - {json}");

        return ParseBatchSnapshot(json);
    }

    private async Task<OpenAiBatchSnapshot> GetBatchAsync(string openAiBatchId, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"batches/{openAiBatchId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await Http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI API error: {response.StatusCode} - {json}");

        return ParseBatchSnapshot(json);
    }

    private async Task<string> DownloadFileContentAsync(string fileId, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"files/{fileId}/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await Http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI API error: {response.StatusCode} - {content}");

        return content;
    }

    private static OpenAiBatchSnapshot ParseBatchSnapshot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
        var outputFileId = root.TryGetProperty("output_file_id", out var outputProp) ? outputProp.GetString() : null;
        var errorFileId = root.TryGetProperty("error_file_id", out var errorProp) ? errorProp.GetString() : null;
        string? lastError = null;
        if (root.TryGetProperty("errors", out var errorsProp) && errorsProp.ValueKind == JsonValueKind.Object)
            lastError = errorsProp.ToString();

        return new OpenAiBatchSnapshot(
            Id: root.GetProperty("id").GetString() ?? string.Empty,
            Status: status ?? string.Empty,
            OutputFileId: outputFileId,
            ErrorFileId: errorFileId,
            LastError: lastError);
    }

    private static string MapBatchStatus(string openAiStatus)
    {
        var status = openAiStatus?.ToLowerInvariant() ?? string.Empty;
        return status switch
        {
            "validating" => SummarizationBatchStatuses.Submitting,
            "in_progress" => SummarizationBatchStatuses.Running,
            "finalizing" => SummarizationBatchStatuses.Applying,
            "completed" => SummarizationBatchStatuses.Completed,
            "failed" => SummarizationBatchStatuses.Failed,
            "expired" => SummarizationBatchStatuses.Failed,
            "cancelled" => SummarizationBatchStatuses.Canceled,
            "cancelling" => SummarizationBatchStatuses.Canceled,
            _ => SummarizationBatchStatuses.Queued
        };
    }

    private static void ParseOutputJsonl(
        string jsonl,
        IDictionary<string, string> outputs,
        IDictionary<string, string> errors)
    {
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("custom_id", out var customIdProp))
                continue;
            var customId = customIdProp.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(customId))
                continue;

            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind != JsonValueKind.Null && errorProp.ValueKind != JsonValueKind.Undefined)
            {
                errors[customId] = errorProp.ToString();
                continue;
            }

            if (!root.TryGetProperty("response", out var responseProp) || responseProp.ValueKind != JsonValueKind.Object)
            {
                errors[customId] = "Missing response payload.";
                continue;
            }

            if (!responseProp.TryGetProperty("status_code", out var statusCodeProp) || statusCodeProp.ValueKind != JsonValueKind.Number)
            {
                errors[customId] = "Missing response status code.";
                continue;
            }

            var statusCode = statusCodeProp.GetInt32();
            if (statusCode < 200 || statusCode >= 300)
            {
                string details = $"HTTP {statusCode} returned in batch item response.";
                if (responseProp.TryGetProperty("body", out var errorBodyProp) &&
                    errorBodyProp.ValueKind == JsonValueKind.Object &&
                    errorBodyProp.TryGetProperty("error", out var apiErrorProp))
                {
                    details = apiErrorProp.ToString() ?? details;
                }

                errors[customId] = details;
                continue;
            }

            if (!responseProp.TryGetProperty("body", out var bodyProp) || bodyProp.ValueKind != JsonValueKind.Object)
            {
                errors[customId] = "Missing response body.";
                continue;
            }

            if (!bodyProp.TryGetProperty("choices", out var choicesProp) || choicesProp.ValueKind != JsonValueKind.Array || choicesProp.GetArrayLength() == 0)
            {
                errors[customId] = "No choices in response body.";
                continue;
            }

            var first = choicesProp[0];
            if (!first.TryGetProperty("message", out var messageProp) || messageProp.ValueKind != JsonValueKind.Object)
            {
                errors[customId] = "No message in first choice.";
                continue;
            }

            if (!messageProp.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.String)
            {
                errors[customId] = "No content in first choice message.";
                continue;
            }

            outputs[customId] = contentProp.GetString() ?? string.Empty;
        }
    }

    private static void ParseErrorJsonl(string jsonl, IDictionary<string, string> errors)
    {
        var lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("custom_id", out var customIdProp))
                continue;
            var customId = customIdProp.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(customId))
                continue;

            var errorText = root.TryGetProperty("error", out var errorProp)
                ? errorProp.ToString()
                : "Batch item failed without error details.";
            errors[customId] = errorText;
        }
    }

    private async Task TryApplyAvailableResultsAsync(int localBatchId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ApplyBatchResultsAsync(localBatchId, cancellationToken);
            if (string.Equals(result.ErrorMessage, ResultsNotReadyErrorMessage, StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch
        {
            // Best-effort salvage path during cancellation; stop flow should not fail if apply is unavailable.
        }
    }
}

public sealed record OpenAiBatchSnapshot(
    string Id,
    string Status,
    string? OutputFileId,
    string? ErrorFileId,
    string? LastError);

public sealed record BatchApplyResult(int Succeeded, int Failed, string? ErrorMessage);
