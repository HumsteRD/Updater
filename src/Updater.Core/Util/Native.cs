using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Updater.Core;

/// <summary>Вызовы Windows, без которых изменения не вступают в силу до перезахода.</summary>
public static class Native
{
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;
    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPI_SETMOUSESPEED = 0x0071;
    private const uint SPI_SETWHEELSCROLLLINES = 0x0069;
    private const uint SPI_SETMOUSEBUTTONSWAP = 0x0021;
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPI_SETKEYBOARDDELAY = 0x0017;
    private const uint SPI_SETKEYBOARDSPEED = 0x000B;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_FONTCHANGE = 0x001D;
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] vparam, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr vparam, uint winIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfoString(uint action, uint param, string vparam, uint winIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string? lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResource(string file);

    public static void BroadcastSettingChange(string area)
    {
        try { SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, area, SMTO_ABORTIFHUNG, 3000, out _); }
        catch { }
    }

    public static void BroadcastFontChange()
    {
        try { SendMessageTimeout(HWND_BROADCAST, WM_FONTCHANGE, IntPtr.Zero, null, SMTO_ABORTIFHUNG, 3000, out _); }
        catch { }
    }

    public static void AddFont(string file)
    {
        try { AddFontResource(file); } catch { }
    }

    /// <summary>Ускорение мыши: порог1, порог2, ускорение (0/1).</summary>
    public static void ApplyMouse(int threshold1, int threshold2, int accel, int speed, int wheelLines, bool swap)
    {
        try
        {
            SystemParametersInfo(SPI_SETMOUSE, 0, [threshold1, threshold2, accel], SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            if (speed is >= 1 and <= 20)
                SystemParametersInfo(SPI_SETMOUSESPEED, 0, new IntPtr(speed), SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            if (wheelLines > 0)
                SystemParametersInfo(SPI_SETWHEELSCROLLLINES, (uint)wheelLines, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            SystemParametersInfo(SPI_SETMOUSEBUTTONSWAP, swap ? 1u : 0u, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch { }
    }

    public static void ApplyKeyboard(int delay, int speed)
    {
        try
        {
            if (delay is >= 0 and <= 3) SystemParametersInfo(SPI_SETKEYBOARDDELAY, (uint)delay, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            if (speed is >= 0 and <= 31) SystemParametersInfo(SPI_SETKEYBOARDSPEED, (uint)speed, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch { }
    }

    public static bool SetWallpaper(string file) =>
        SystemParametersInfoString(SPI_SETDESKWALLPAPER, 0, file, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    /// <summary>
    /// Запрос прав даст права этой же учётной записи: она — администратор, просто сейчас с урезанным токеном UAC.
    /// false — обычная учётная запись: окно UAC попросит пароль другого человека, и программа увидит его папки,
    /// а не твои. Тогда работаем без прав и не спрашиваем.
    /// </summary>
    public static bool CanElevateAsSelf()
    {
        if (IsAdmin()) return false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            // TokenElevationType (18): 1 — без связанного токена (обычный пользователь или UAC выключен), 3 — урезанный.
            if (!GetTokenInformation(id.Token, 18, out int type, sizeof(int), out _)) return true;
            return type == 3;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Коротко для журнала и проверок: «админ», «админ без прав (UAC)», «обычная учётная запись».</summary>
    public static string AccountKind() => IsAdmin() ? "админ" : CanElevateAsSelf() ? "админ без прав (UAC)" : "обычная учётная запись";

    public static int WindowsBuild => Environment.OSVersion.Version.Build;

    public static string WindowsName()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = k?.GetValue("ProductName") as string ?? "Windows";
            var display = k?.GetValue("DisplayVersion") as string;
            // Реестр Windows 11 до сих пор называет себя «Windows 10».
            if (WindowsBuild >= 22000) product = product.Replace("Windows 10", "Windows 11");
            return display is null ? $"{product} ({WindowsBuild})" : $"{product} {display} ({WindowsBuild})";
        }
        catch
        {
            return $"Windows ({WindowsBuild})";
        }
    }

    /// <summary>
    /// Перезапуск Проводника: процесс завершается, и Windows сама поднимает оболочку заново
    /// от имени пользователя (без прав администратора, которые есть у нас).
    /// </summary>
    public static void RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); } catch { }
            }
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                if (Process.GetProcessesByName("explorer").Length > 0) return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { }
    }

    public static void RestartStartMenu()
    {
        foreach (var name in new[] { "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); } catch { }
            }
    }
}
