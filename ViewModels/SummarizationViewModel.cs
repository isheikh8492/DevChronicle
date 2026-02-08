using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChronicle.Models;
using DevChronicle.Services;

namespace DevChronicle.ViewModels;

/// <summary>
/// Transient UI projection over SummarizationRunnerService state.
/// </summary>
public partial class SummarizationViewModel : ObservableObject, IDisposable
{
    private readonly SummarizationRunnerService _runner;
    private bool _disposed;

    [ObservableProperty]
    private int daysSummarized;

    [ObservableProperty]
    private int pendingDays;

    [ObservableProperty]
    private string status = "Idle";

    [ObservableProperty]
    private OperationState operationState = OperationState.Idle;

    [ObservableProperty]
    private string recoverActionText = string.Empty;

    [ObservableProperty]
    private bool isSummarizing;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private int progressCurrentStep;

    [ObservableProperty]
    private int progressTotalSteps;

    public bool IsProgressIndeterminate => IsSummarizing && Progress <= 0;
    public bool HasRecoverableIssue => !string.IsNullOrWhiteSpace(RecoverActionText);
    public bool HasProgressCounter => ProgressTotalSteps > 0;
    public bool ShowStatusPanel => IsSummarizing || OperationState != OperationState.Idle;
    public string ProgressCounterText => ProgressTotalSteps > 0 ? $"{ProgressCurrentStep}/{ProgressTotalSteps}" : string.Empty;

    public event EventHandler<DateTime>? DaySummarized;

    public SummarizationViewModel(SummarizationRunnerService runner)
    {
        _runner = runner;
        _runner.StateChanged += OnRunnerStateChanged;
        _runner.DaySummarized += OnRunnerDaySummarized;
        SyncFromRunner();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _runner.StateChanged -= OnRunnerStateChanged;
        _runner.DaySummarized -= OnRunnerDaySummarized;
        _disposed = true;
    }

    [RelayCommand]
    private async Task SummarizePendingDaysAsync()
    {
        await _runner.SummarizePendingDaysAsync();
    }

    [RelayCommand]
    private async Task SummarizeSelectedDayAsync(DayViewModel? day)
    {
        if (day == null)
        {
            OperationState = OperationState.NeedsInput;
            RecoverActionText = "Select a day and retry.";
            Status = OperationStatusFormatter.FormatTerminal(OperationState.NeedsInput, "No day selected.");
            return;
        }

        await _runner.SummarizeSelectedDayAsync(day.Date);
    }

    [RelayCommand]
    private void Stop()
    {
        _runner.Stop();
    }

    public async Task UpdatePendingCountAsync()
    {
        await _runner.UpdatePendingCountAsync();
    }

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    partial void OnIsSummarizingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ShowStatusPanel));
    }

    partial void OnProgressCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressCounterText));
        OnPropertyChanged(nameof(HasProgressCounter));
    }

    partial void OnProgressTotalStepsChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressCounterText));
        OnPropertyChanged(nameof(HasProgressCounter));
    }

    partial void OnRecoverActionTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasRecoverableIssue));
    }

    partial void OnOperationStateChanged(OperationState value)
    {
        OnPropertyChanged(nameof(ShowStatusPanel));
    }

    private void OnRunnerStateChanged(object? sender, EventArgs e)
    {
        SyncFromRunner();
    }

    private void OnRunnerDaySummarized(object? sender, DateTime day)
    {
        DaySummarized?.Invoke(this, day);
    }

    private void SyncFromRunner()
    {
        DaysSummarized = _runner.DaysSummarized;
        PendingDays = _runner.PendingDays;
        Status = _runner.Status;
        OperationState = _runner.OperationState;
        RecoverActionText = _runner.RecoverActionText;
        IsSummarizing = _runner.IsSummarizing;
        Progress = _runner.Progress;
        ProgressCurrentStep = _runner.ProgressCurrentStep;
        ProgressTotalSteps = _runner.ProgressTotalSteps;
    }
}
