using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HisaabKitaab.Services;

/// <summary>
/// Shows a desktop notification.
/// </summary>
public interface INotifier
{
    /// <summary>
    /// False when this system can't show desktop notifications.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Why notifications can't be shown (and how to fix it), or null if they can.
    /// </summary>
    string? UnavailableReason { get; }

    void Show(string title, string message);
}

public static class Notifiers
{
    /// <summary>
    /// The notifier for the platform we're running on.
    /// </summary>
    public static INotifier ForThisPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsNotifier()
        : OperatingSystem.IsLinux() ? new LinuxNotifier()
        : new NullNotifier();
}

public sealed class NullNotifier : INotifier
{
    public bool IsAvailable => false;

    public string? UnavailableReason => "Desktop notifications aren't supported on this system.";

    public void Show(string title, string message)
    {
    }
}

/// <summary>
/// Linux: <c>notify-send</c> (libnotify), present on most desktops.
/// </summary>
public sealed class LinuxNotifier : INotifier
{
    private readonly Lazy<bool> _available = new(() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(':', StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => File.Exists(Path.Combine(dir, "notify-send"))));

    public bool IsAvailable => _available.Value;

    public string? UnavailableReason => IsAvailable ? null
        : "Desktop notifications need notify-send (install libnotify-bin or your distribution's libnotify package).";

    public void Show(string title, string message)
    {
        if (!IsAvailable)
            return;

        try
        {
            var start = new ProcessStartInfo("notify-send") { UseShellExecute = false };
            start.ArgumentList.Add("--app-name=Hisaab Kitaab");
            start.ArgumentList.Add(title);
            start.ArgumentList.Add(message);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}

/// <summary>
/// Windows: a notification-area icon whose balloon Windows 10/11 shows as a
/// normal toast notification. Plain Win32, so no extra packages or
/// Windows-only build target. Needs the main window's handle first
/// (<see cref="AttachTo"/>); the icon is removed on <see cref="Dispose"/>.
/// </summary>
public sealed class WindowsNotifier : INotifier, IDisposable
{
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const int NIIF_USER = 0x4, NIIF_LARGE_ICON = 0x20;
    private const int IconId = 1;

    private readonly object _lock = new();
    private IntPtr _window;
    private IntPtr _icon;
    private bool _added;

    public bool IsAvailable => UnavailableReason is null;

    /// <summary>
    /// Windows can have notifications switched off for everything, in which case
    /// it silently drops them. Read from the same setting as Settings → System → Notifications.
    /// </summary>
    public string? UnavailableReason
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return "Not running on Windows.";

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
            return key?.GetValue("ToastEnabled") is int enabled && enabled == 0
                ? "Notifications are turned off in Windows. Turn them on in Settings → System → Notifications."
                : null;
        }
    }

    /// <summary>
    /// Whether Windows accepted the last notification (for diagnostics).
    /// </summary>
    public bool LastShowSucceeded { get; private set; }

    /// <summary>
    /// Call once the main window exists.
    /// </summary>
    public void AttachTo(IntPtr windowHandle)
    {
        lock (_lock)
        {
            _window = windowHandle;

            // The app's own icon from the .exe. ExtractIcon returns 1 (not 0) for
            // "no icons here", e.g. when run through dotnet.exe; fall back to Windows' default.
            _icon = ExtractIcon(IntPtr.Zero, Environment.ProcessPath ?? string.Empty, 0);
            if ((long)_icon <= 1)
            {
                _icon = IntPtr.Zero;
                _sharedIcon = LoadIcon(IntPtr.Zero, (IntPtr)IdiApplication);
            }
        }
    }

    // Shared system icons must not be destroyed, so they're kept apart from _icon.
    private IntPtr _sharedIcon;
    private const int IdiApplication = 32512;

    private IntPtr IconToUse => _icon != IntPtr.Zero ? _icon : _sharedIcon;

    public void Show(string title, string message)
    {
        if (!OperatingSystem.IsWindows())
            return;

        lock (_lock)
        {
            if (_window == IntPtr.Zero)
                return;

            var data = NewData(NIF_ICON | NIF_TIP);
            if (!_added)
                _added = Shell_NotifyIcon(NIM_ADD, ref data);

            data = NewData(NIF_ICON | NIF_TIP | NIF_INFO);
            data.szInfoTitle = Truncate(title, 63);
            data.szInfo = Truncate(message, 255);
            data.dwInfoFlags = IconToUse != IntPtr.Zero ? NIIF_USER | NIIF_LARGE_ICON : 0;
            data.hBalloonIcon = IconToUse;
            LastShowSucceeded = _added && Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_added)
            {
                var data = NewData(0);
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _added = false;
            }

            if (_icon != IntPtr.Zero)
            {
                DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }
        }
    }

    private NotifyIconData NewData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window,
        uID = IconId,
        uFlags = flags,
        hIcon = IconToUse,
        szTip = "Hisaab Kitaab",
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconW", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr instance, string exeFileName, int iconIndex);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);
}
