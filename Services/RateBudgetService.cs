namespace DevChronicle.Services;

public sealed class RateBudgetService
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const double SafetyFactor = 0.8;
    private readonly object _gate = new();
    private readonly List<BudgetReservation> _reservations = new();

    public async Task WaitForCapacityAsync(
        string providerId,
        string model,
        int estimatedInputTokens,
        int reservedOutputTokens,
        CancellationToken cancellationToken)
    {
        var requestedInput = Math.Max(1, estimatedInputTokens);
        var requestedOutput = Math.Max(1, reservedOutputTokens);

        while (true)
        {
            TimeSpan delay;

            lock (_gate)
            {
                var now = DateTime.UtcNow;
                Prune(now);

                var limits = ResolveLimits(providerId, model);
                var reqLimit = Math.Max(1, (int)Math.Floor(limits.RequestsPerMinute * SafetyFactor));
                var inLimit = Math.Max(1, (int)Math.Floor(limits.InputTokensPerMinute * SafetyFactor));
                var outLimit = Math.Max(1, (int)Math.Floor(limits.OutputTokensPerMinute * SafetyFactor));
                // Prevent impossible waits: if one request is larger than a full minute budget,
                // reserve at most one-minute capacity so it can proceed when the window clears.
                var safeInput = Math.Min(requestedInput, inLimit);
                var safeOutput = Math.Min(requestedOutput, outLimit);

                var requestCount = _reservations.Count;
                var inputTotal = _reservations.Sum(r => r.InputTokens);
                var outputTotal = _reservations.Sum(r => r.OutputTokens);

                if (requestCount + 1 <= reqLimit &&
                    inputTotal + safeInput <= inLimit &&
                    outputTotal + safeOutput <= outLimit)
                {
                    _reservations.Add(new BudgetReservation(now, safeInput, safeOutput));
                    return;
                }

                if (_reservations.Count == 0)
                {
                    delay = TimeSpan.FromMilliseconds(300);
                }
                else
                {
                    var nextExpiry = _reservations
                        .Select(r => (r.Timestamp + Window) - now)
                        .OrderBy(t => t)
                        .FirstOrDefault();
                    delay = nextExpiry <= TimeSpan.Zero
                        ? TimeSpan.FromMilliseconds(300)
                        : nextExpiry + TimeSpan.FromMilliseconds(50);
                }
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    private void Prune(DateTime now)
    {
        _reservations.RemoveAll(r => now - r.Timestamp >= Window);
    }

    private static ModelRateLimits ResolveLimits(string providerId, string model)
    {
        var normalizedProvider = (providerId ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedModel = (model ?? string.Empty).Trim().ToLowerInvariant();

        // Conservative defaults. Anthropic limits map to the screenshot values.
        if (normalizedProvider == "anthropic")
        {
            if (normalizedModel.Contains("haiku", StringComparison.Ordinal))
                return new ModelRateLimits(50, 50_000, 10_000);

            if (normalizedModel.Contains("sonnet", StringComparison.Ordinal) ||
                normalizedModel.Contains("opus", StringComparison.Ordinal))
                return new ModelRateLimits(50, 30_000, 8_000);

            return new ModelRateLimits(40, 20_000, 6_000);
        }

        // Generic OpenAI fallback (conservative). Can be tuned per project later.
        return new ModelRateLimits(30, 20_000, 6_000);
    }
}

internal sealed record BudgetReservation(DateTime Timestamp, int InputTokens, int OutputTokens);
internal sealed record ModelRateLimits(int RequestsPerMinute, int InputTokensPerMinute, int OutputTokensPerMinute);
