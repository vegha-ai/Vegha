using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Vegha.App.Controls.Services;

namespace Vegha.App.Controls.Workspace;

/// <summary>Helpers that polish modal-dialog chrome. Avalonia exposes <c>CanResize</c> to
/// disable maximize, but the minimize button stays on Windows — strip it via Win32
/// SetWindowLong so dialogs only show the close button (matches the rest of the app's
/// modal UX). No-op on non-Windows platforms.</summary>
public static class DialogWindowExtensions
{
    private const int GWL_STYLE = -16;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Removes minimize + maximize buttons from <paramref name="window"/>'s title bar
    /// AND wraps the window content with the shared interface-zoom transform. Every dialog in
    /// the app calls this in its <c>Opened</c> handler, so co-locating the zoom attach here is
    /// the lowest-friction way to make the Settings → Appearance interface-zoom slider scale
    /// dialogs the same way it scales the main window.</summary>
    public static void RemoveMinimizeMaximize(this Window window)
    {
        ZoomHost.Attach(window);
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null) return;
            var hwnd = handle.Handle;
            if (hwnd == IntPtr.Zero) return;

            if (IntPtr.Size == 8)
            {
                var style = GetWindowLongPtr64(hwnd, GWL_STYLE).ToInt64();
                style &= ~(WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
                SetWindowLongPtr64(hwnd, GWL_STYLE, new IntPtr(style));
            }
            else
            {
                var style = GetWindowLong32(hwnd, GWL_STYLE);
                style &= ~(WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
                SetWindowLong32(hwnd, GWL_STYLE, style);
            }
        }
        catch
        {
            // Best-effort polish — the dialog still works with the minimize button visible.
        }
    }
}
