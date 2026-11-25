using System;

namespace WPFFrontend;

// Only arm on a live exclusive window that subsequently minimizes. A paused
// game or a companion attaching to an already idle game needs no mode switch.
internal struct FullscreenResume
{
    private IntPtr liveWindow;
    private long minimizedFrame;
    private long restoredAt;

    public bool NeedsRecovery(IntPtr window, long frame, long now, bool minimized, bool foreground)
    {
        if (liveWindow != window) this = default;
        if (window == IntPtr.Zero || frame == 0) return false;
        if (minimized)
        {
            if (liveWindow == window) minimizedFrame = frame;
            restoredAt = 0;
            return false;
        }
        if (!foreground)
        {
            restoredAt = 0;
            return false;
        }
        if (frame != minimizedFrame)
        {
            minimizedFrame = restoredAt = 0;
            liveWindow = now - frame < 1500 ? window : IntPtr.Zero;
            return false;
        }
        if (restoredAt == 0) restoredAt = now;
        if (now - restoredAt < 4000) return false;
        this = default; // A new live presentation must precede any further attempt.
        return true;
    }
}
