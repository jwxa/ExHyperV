using System;
using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

internal readonly record struct MultiMonitorUnlockRecovery(
    bool ShouldRecover,
    int ExpectedMonitorCount);

internal readonly record struct MultiMonitorTopology(
    int MonitorCount,
    int Left,
    int Top,
    int Width,
    int Height,
    string MonitorLayout)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public string Bounds => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

internal readonly record struct MultiMonitorPixelBounds(
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsValid => Width > 0 && Height > 0;
    public string Bounds => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

internal static class MultiMonitorContentAlignment
{
    public static MultiMonitorPixelBounds CalculateWindowBounds(
        MultiMonitorPixelBounds targetContent,
        MultiMonitorPixelBounds currentWindow,
        MultiMonitorPixelBounds currentContent)
    {
        if (!targetContent.IsValid)
            throw new ArgumentOutOfRangeException(nameof(targetContent));
        if (!currentWindow.IsValid)
            throw new ArgumentOutOfRangeException(nameof(currentWindow));
        if (!currentContent.IsValid)
            throw new ArgumentOutOfRangeException(nameof(currentContent));

        return new MultiMonitorPixelBounds(
            currentWindow.Left + targetContent.Left - currentContent.Left,
            currentWindow.Top + targetContent.Top - currentContent.Top,
            currentWindow.Right + targetContent.Right - currentContent.Right,
            currentWindow.Bottom + targetContent.Bottom - currentContent.Bottom);
    }
}

internal enum MultiMonitorLeaveDisposition
{
    UserRequestedWindowed,
    ConfirmPotentialUserLeave,
    PreserveIntent,
    PreserveIntentAndRecover,
}

internal static class MultiMonitorLeavePolicy
{
    public static MultiMonitorLeaveDisposition Resolve(
        bool userFullScreenHotKeyPressed,
        bool sessionTransitionPending,
        bool expectedSystemLeave,
        bool expectedRecoveryFailureLeave)
    {
        if (userFullScreenHotKeyPressed)
            return MultiMonitorLeaveDisposition.UserRequestedWindowed;
        if (sessionTransitionPending || expectedRecoveryFailureLeave)
            return MultiMonitorLeaveDisposition.PreserveIntent;
        if (expectedSystemLeave)
            return MultiMonitorLeaveDisposition.PreserveIntentAndRecover;
        return MultiMonitorLeaveDisposition.ConfirmPotentialUserLeave;
    }
}

internal static class MultiMonitorDisplayEventPolicy
{
    public static bool ShouldQueueDpiRecovery(
        bool useAllMonitors,
        bool potentialUserLeavePending,
        bool internalPlacementSuppressed) =>
        useAllMonitors
        && !potentialUserLeavePending
        && !internalPlacementSuppressed;
}

internal readonly record struct MultiMonitorWindowRestorePlan(
    bool NormalizeBeforeRestore,
    bool NoActivate,
    bool RestoreMinimized)
{
    public static MultiMonitorWindowRestorePlan Create(bool preserveMinimized) =>
        new(
            NormalizeBeforeRestore: !preserveMinimized,
            NoActivate: preserveMinimized,
            RestoreMinimized: preserveMinimized);
}

internal sealed class ExpectedSystemLeaveState
{
    private int _generation;

    public void Arm(int generation)
    {
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        Volatile.Write(ref _generation, generation);
    }

    public void Clear() => Volatile.Write(ref _generation, 0);

    public bool TryConsume()
    {
        int generation = Volatile.Read(ref _generation);
        return generation != 0
            && Interlocked.CompareExchange(ref _generation, 0, generation) == generation;
    }

    public void Expire(int generation)
    {
        if (generation > 0)
            Interlocked.CompareExchange(ref _generation, 0, generation);
    }
}

/// <summary>
/// Tracks the user's multi-monitor full-screen intent independently from the
/// transient full-screen state reported by mstscax during lock and display changes.
/// </summary>
internal sealed class MultiMonitorRecoveryState
{
    private int _leaveIntentGeneration;
    private bool _restoreAfterUnlock;
    private int _monitorCountBeforeLock;

    public MultiMonitorRecoveryState(bool fullScreenDesired)
    {
        FullScreenDesired = fullScreenDesired;
    }

    public bool FullScreenDesired { get; private set; }
    public bool SessionLocked { get; private set; }
    public int StableMonitorCount { get; private set; } = 1;

    public void RequestFullScreen()
    {
        InvalidatePendingLeave();
        FullScreenDesired = true;
    }

    public void RequestWindowed()
    {
        InvalidatePendingLeave();
        FullScreenDesired = false;
        _restoreAfterUnlock = false;
    }

    public int BeginPotentialUserLeave()
    {
        if (!FullScreenDesired) return 0;
        return unchecked(++_leaveIntentGeneration);
    }

    public bool ConfirmPotentialUserLeave(int generation, bool systemTransitionPending)
    {
        if (generation == 0
            || generation != _leaveIntentGeneration
            || SessionLocked
            || systemTransitionPending)
            return false;

        FullScreenDesired = false;
        _restoreAfterUnlock = false;
        return true;
    }

    public void InvalidatePendingLeave()
    {
        unchecked { _leaveIntentGeneration++; }
    }

    public void Lock(int currentMonitorCount)
    {
        InvalidatePendingLeave();
        SessionLocked = true;
        _restoreAfterUnlock = FullScreenDesired;
        _monitorCountBeforeLock = Math.Max(
            Math.Max(1, StableMonitorCount),
            currentMonitorCount);
    }

    public void RememberLockedTopology(int currentMonitorCount)
    {
        if (!SessionLocked) return;
        _restoreAfterUnlock |= FullScreenDesired;
        _monitorCountBeforeLock = Math.Max(
            _monitorCountBeforeLock,
            Math.Max(StableMonitorCount, currentMonitorCount));
    }

    public MultiMonitorUnlockRecovery Unlock()
    {
        InvalidatePendingLeave();
        SessionLocked = false;
        var recovery = new MultiMonitorUnlockRecovery(
            _restoreAfterUnlock && FullScreenDesired,
            Math.Max(1, _monitorCountBeforeLock));
        _restoreAfterUnlock = false;
        _monitorCountBeforeLock = 0;
        return recovery;
    }

    public void RecordStableTopology(int monitorCount)
    {
        if (monitorCount > 0) StableMonitorCount = monitorCount;
    }

    public int ResolveExpectedMonitorCount(int requestedMonitorCount, int currentMonitorCount) =>
        Math.Max(
            1,
            requestedMonitorCount > 0
                ? requestedMonitorCount
                : Math.Max(StableMonitorCount, currentMonitorCount));
}

internal static class WindowsSessionLockState
{
    private const int WtsCurrentSession = -1;
    private const int WtsSessionStateLock = 0;
    private const int WtsSessionStateUnlock = 1;
    private const int WtsInfoExLevel1 = 1;
    private const int WtsSessionInfoEx = 25;

    public static bool? QueryCurrent()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int sessionFlagsOffset = SessionFlagsOffsetForPointerSize(IntPtr.Size);
            if (!WTSQuerySessionInformation(
                    IntPtr.Zero,
                    WtsCurrentSession,
                    WtsSessionInfoEx,
                    out buffer,
                    out int bytesReturned)
                || buffer == IntPtr.Zero
                || bytesReturned < sessionFlagsOffset + sizeof(int)
                || Marshal.ReadInt32(buffer, 0) != WtsInfoExLevel1)
                return null;

            return Marshal.ReadInt32(buffer, sessionFlagsOffset) switch
            {
                WtsSessionStateLock => true,
                WtsSessionStateUnlock => false,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    internal static int SessionFlagsOffsetForPointerSize(int pointerSize) => pointerSize switch
    {
        // WTSINFOEX.Data is aligned to the largest WTSINFOEX_LEVEL1 member (LARGE_INTEGER).
        4 => 12,
        8 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(pointerSize)),
    };

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        int sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
