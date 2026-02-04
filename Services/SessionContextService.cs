using CommunityToolkit.Mvvm.ComponentModel;
using DevChronicle.Models;

namespace DevChronicle.Services;

/// <summary>
/// Singleton service that manages the currently active session across the application.
/// This enables all ViewModels and pages to access the current session context.
/// </summary>
public partial class SessionContextService : ObservableObject
{
    [ObservableProperty]
    private Session? currentSession;

    /// <summary>
    /// Event fired when the current session changes (set or cleared).
    /// </summary>
    public event EventHandler<Session?>? CurrentSessionChanged;

    /// <summary>
    /// Sets the currently active session and notifies listeners.
    /// </summary>
    /// <param name="session">The session to set as current, or null to clear</param>
    public void SetCurrentSession(Session? session)
    {
        if (CurrentSession?.Id == session?.Id)
            return; // Same session, no change needed

        CurrentSession = session;
        CurrentSessionChanged?.Invoke(this, session);
    }

    /// <summary>
    /// Clears the current session context.
    /// </summary>
    public void ClearSession()
    {
        SetCurrentSession(null);
    }

    /// <summary>
    /// Checks if a session is currently set.
    /// </summary>
    public bool HasActiveSession => CurrentSession != null;
}
