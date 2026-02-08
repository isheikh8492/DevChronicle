using DevChronicle.Models;
using Xunit;

namespace DevChronicle.Tests;

public class SummarizationBatchPersistenceTests
{
    [Fact]
    public async Task SummarizationBatch_CanCreateAndQueryActive()
    {
        using var testDb = TestDb.Create();
        var db = testDb.Db;

        var sessionId = await db.CreateSessionAsync(new Session
        {
            Name = "batch-persistence",
            RepoPath = "C:\\repo",
            MainBranch = "main",
            CreatedAt = DateTime.UtcNow
        });

        var batch = new SummarizationBatch
        {
            SessionId = sessionId,
            OpenAiBatchId = "batch_123",
            Status = SummarizationBatchStatuses.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var id = await db.CreateSummarizationBatchAsync(batch);
        var active = await db.GetActiveSummarizationBatchesAsync();
        Assert.Contains(active, b => b.Id == id && b.OpenAiBatchId == "batch_123");

        var fetched = await db.GetSummarizationBatchByOpenAiIdAsync("batch_123");
        Assert.NotNull(fetched);
        Assert.Equal(id, fetched!.Id);
    }

    [Fact]
    public async Task SummarizationBatchItems_UpsertAndStatusUpdate_Works()
    {
        using var testDb = TestDb.Create();
        var db = testDb.Db;

        var sessionId = await db.CreateSessionAsync(new Session
        {
            Name = "batch-items",
            RepoPath = "C:\\repo",
            MainBranch = "main",
            CreatedAt = DateTime.UtcNow
        });

        var batchId = await db.CreateSummarizationBatchAsync(new SummarizationBatch
        {
            SessionId = sessionId,
            OpenAiBatchId = "batch_items_123",
            Status = SummarizationBatchStatuses.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var day = new DateTime(2026, 2, 1);
        await db.UpsertSummarizationBatchItemsAsync(new[]
        {
            new SummarizationBatchItem
            {
                BatchId = batchId,
                SessionId = sessionId,
                Day = day,
                CustomId = $"session:{sessionId}:day:{day:yyyy-MM-dd}",
                Model = "gpt-4o-mini",
                PromptVersion = "v2",
                InputHash = "abc123",
                MaxBullets = 6,
                Status = SummarizationBatchItemStatuses.Pending
            }
        });

        await db.UpdateSummarizationBatchItemStatusAsync(
            batchId,
            day,
            SummarizationBatchItemStatuses.Succeeded,
            null);

        var items = await db.GetSummarizationBatchItemsAsync(batchId);
        Assert.Single(items);
        Assert.Equal(SummarizationBatchItemStatuses.Succeeded, items[0].Status);
        Assert.Equal("gpt-4o-mini", items[0].Model);
        Assert.Equal("v2", items[0].PromptVersion);
        Assert.Equal("abc123", items[0].InputHash);
    }
}
