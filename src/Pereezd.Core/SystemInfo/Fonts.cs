using Microsoft.Win32;

namespace Pereezd.Core;

/// <summary>Шрифты, которые ставил сам пользователь: в свою папку или в системную после установки Windows.</summary>
public static class Fonts
{
    private static readonly string[] FontExt = [".ttf", ".otf", ".ttc", ".otc", ".fon", ".fnt"];

    public sealed record FoundFont(string Name, string Path, bool PerUser, long Bytes);

    public static List<FoundFont> FindUserInstalled()
    {
        var list = new List<FoundFont>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Шрифты «для одного пользователя» (Windows 10 1809+): папка и записи в HKCU.
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (k is not null)
                foreach (var name in k.GetValueNames())
                {
                    if (k.GetValue(name) is not string file) continue;
                    if (!System.IO.Path.IsPathRooted(file)) continue;
                    if (!File.Exists(file) || !seen.Add(file)) continue;
                    list.Add(new FoundFont(CleanName(name), file, true, new FileInfo(file).Length));
                }
        }
        catch { }

        // 2. Системные шрифты, появившиеся заметно позже установки Windows.
        try
        {
            var windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var installed = InstallDate();
            if (installed is not null)
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (k is not null)
                    foreach (var name in k.GetValueNames())
                    {
                        if (k.GetValue(name) is not string file) continue;
                        var full = System.IO.Path.IsPathRooted(file) ? file : System.IO.Path.Combine(windowsFonts, file);
                        if (!File.Exists(full) || !seen.Add(full)) continue;
                        if (!FontExt.Contains(System.IO.Path.GetExtension(full).ToLowerInvariant())) continue;
                        var fi = new FileInfo(full);
                        // Шрифты самой Windows и её обновлений датированы задолго до установки или подписаны Microsoft;
                        // берём только то, что появилось через сутки после установки.
                        if (fi.CreationTimeUtc < installed.Value.AddDays(1)) continue;
                        if (IsMicrosoftFont(name)) continue;
                        list.Add(new FoundFont(CleanName(name), full, false, fi.Length));
                    }
            }
        }
        catch { }

        return list.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsMicrosoftFont(string name) =>
        name.StartsWith("Segoe", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Cascadia", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Sitka", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("HoloLens", StringComparison.OrdinalIgnoreCase);

    public static DateTime? InstallDate()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k?.GetValue("InstallDate") is int secs) return DateTime.UnixEpoch.AddSeconds((uint)secs);
        }
        catch { }
        return null;
    }

    private static string CleanName(string regName) =>
        regName.Replace(" (TrueType)", "").Replace(" (OpenType)", "").Replace(" (All res)", "").Trim();

    /// <summary>Поставить шрифт на новой винде: в системную папку (с правами) или себе в профиль.</summary>
    public static void Install(string file, string name, bool forAllUsers)
    {
        var fileName = System.IO.Path.GetFileName(file);
        var regName = name + (System.IO.Path.GetExtension(file).Equals(".otf", StringComparison.OrdinalIgnoreCase) ? " (OpenType)" : " (TrueType)");
        if (forAllUsers)
        {
            var dst = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fileName);
            if (!File.Exists(dst)) File.Copy(file, dst);
            using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            k.SetValue(regName, fileName);
            Native.AddFont(dst);
        }
        else
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
            Directory.CreateDirectory(dir);
            var dst = System.IO.Path.Combine(dir, fileName);
            if (!File.Exists(dst)) File.Copy(file, dst);
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts");
            k.SetValue(regName, dst);
            Native.AddFont(dst);
        }
    }
}
