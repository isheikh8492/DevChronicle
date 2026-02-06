using System;
using System.Collections.Generic;
using System.Threading;

namespace DevChronicle.Services;

public enum ExportFormat
{
    MarkdownAndJson = 0,
    MarkdownOnly = 1,
    JsonOnly = 2
}

public class ExportRequest
{
    public required IReadOnlyList<int> SessionIds { get; init; }
    public bool ExportDiary { get; init; } = true;
    public bool ExportArchive { get; init; } = true;
    public ExportFormat Format { get; init; } = ExportFormat.MarkdownAndJson;
    public required string OutputDirectory { get; init; }
    public string? DiaryFileName { get; init; }
    public string? ArchiveFileName { get; init; }
    public bool HideRepoPathsInMarkdown { get; init; } = true;
    public bool IncludePlaceholders { get; init; } = true;
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;
}

public class ExportResult
{
    public bool Succeeded { get; init; }
    public bool Canceled { get; init; }
    public List<string> FilesWritten { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public string? ErrorMessage { get; init; }
}

public class UpdateDiaryRequest
{
    public required string DiaryPath { get; init; }
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;
}

public class DiaryDiff
{
    public int New { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int Extra { get; init; }
    public bool IsStale { get; init; }
}

public class DiaryPreviewResult
{
    public required ExportResult Result { get; init; }
    public required DiaryDiff Diff { get; init; }
    public required List<int> BoundSessionIds { get; init; }
}
