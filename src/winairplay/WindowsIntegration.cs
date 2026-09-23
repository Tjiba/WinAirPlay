using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinAirPlay;

internal static class WindowsIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static readonly int ShowMessage = RegisterWindowMessage("WinAirPlay.Native.v2.Show");
    public static bool StartAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("WinAirPlay") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("WinAirPlay", $"\"{Application.ExecutablePath}\" --startup");
            else key.DeleteValue("WinAirPlay", false);
        }
    }

    public static void ShowExistingWindow()
    {
        AllowSetForegroundWindow(-1);
        PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
