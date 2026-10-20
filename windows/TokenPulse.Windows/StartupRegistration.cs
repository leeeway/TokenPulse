using Microsoft.Win32;

namespace TokenPulse.Windows;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TokenPulseWindows";
    public static object? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName);
    }
    public static void Write(object? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (value is null) key.DeleteValue(ValueName, false);
        else key.SetValue(ValueName, value);
    }
    public static void Apply(bool enabled) => Write(enabled
        ? $"\"{Environment.ProcessPath ?? throw new IOException("无法确定程序路径")}\" --background" : null);
}
