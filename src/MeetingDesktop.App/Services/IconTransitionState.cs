namespace MeetingDesktop.App.Services;

internal interface IIconOpacityLayer : IDisposable
{
    bool SetAlpha(byte alpha);
    bool Restore();
}

/// <summary>Animations never hold up the folder switch. Hide completes the outgoing fade immediately.</summary>
internal sealed class IconTransitionState(Func<IIconOpacityLayer?> acquire, Func<bool>? cancelled = null) : IDisposable
{
    private IIconOpacityLayer? _layer;
    private string? _id;
    private long _started, _fadeStarted;
    private byte _alpha = 255, _revealFrom, _fadeFrom;
    private bool _hidden, _revealing, _restoring;
    internal bool Active => _layer is not null;
    internal bool Animating => Active && !_restoring && (!_hidden || _revealing);

    public bool Begin(string id, long now)
    {
        if (cancelled?.Invoke() == true) { Dispose(); return false; }
        if (_restoring) { Dispose(); if (Active) return false; }
        // A new switch can start during the previous reveal. Transfer the lease
        // without briefly resetting the icons to full opacity.
        if (_layer is null) { _layer = acquire(); _alpha = 255; }
        if (_layer is null) return false;
        _id = id;
        _started = _fadeStarted = now;
        _fadeFrom = _alpha;
        _hidden = _revealing = false;
        return Apply(_alpha);
    }

    public bool Hide(string id)
    {
        if (_layer is null || id != _id || _restoring) return false;
        _hidden = true;
        _revealing = false;
        return Apply(0);
    }

    public void End(string id, long now)
    {
        if (_layer is null || id != _id || _restoring) return;
        _revealing = true;
        _fadeStarted = now;
        _revealFrom = _alpha;
    }

    public void Tick(long now)
    {
        if (_layer is null) return;
        if (cancelled?.Invoke() == true) { Dispose(); return; }
        if (_restoring) { Dispose(); return; }
        if (now - _started >= 30000) { Dispose(); return; }
        if (_revealing)
        {
            double t = Math.Clamp((now - _fadeStarted) / 160d, 0, 1);
            if (t >= 1) { Dispose(); return; }
            Apply((byte)(_revealFrom + (255 - _revealFrom) * (1 - Math.Pow(1 - t, 3))));
        }
        else if (!_hidden)
        {
            double t = Math.Clamp((now - _started) / 100d, 0, 1);
            if (Apply((byte)(_fadeFrom * (1 - t))) && t >= 1) _hidden = true;
        }
        else
        {
            // Explorer can reset layered attributes while binding the new folder.
            // SetAlpha verifies the current value and only writes when it changed.
            Apply(0);
        }
    }

    private bool Apply(byte alpha)
    {
        if (cancelled?.Invoke() == true) { Dispose(); return false; }
        if (_layer?.SetAlpha(alpha) != true) { Dispose(); return false; }
        if (cancelled?.Invoke() == true) { Dispose(); return false; }
        _alpha = alpha;
        return true;
    }

    public void Dispose()
    {
        _restoring = true;
        if (_layer?.Restore() == false) return;
        _layer = null;
        _id = null;
        _restoring = false;
    }
}
