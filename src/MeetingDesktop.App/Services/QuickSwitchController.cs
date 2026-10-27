using MeetingDesktop.Core.Models;

namespace MeetingDesktop.App.Services;

internal readonly record struct QuickSwitchState(SessionState Session, bool HasRecord, bool IsBusy, bool MatchingRecord = false);

internal sealed class QuickSwitchController(
    Func<QuickSwitchState> readState, Func<Task> prepare, Func<Task> restore, Action review)
{
    private bool _running;

    public async Task ExecuteAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            QuickSwitchState state = readState();
            if (state.IsBusy) return;
            if (state.Session == SessionState.Normal && !state.HasRecord)
                await prepare();
            else if (state.Session == SessionState.Swapped && state.HasRecord && state.MatchingRecord)
                await restore();
            else
                review();
        }
        finally { _running = false; }
    }
}
