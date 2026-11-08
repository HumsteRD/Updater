using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Pereezd.Core;

/// <summary>
/// Места хранятся в сборке не абсолютными путями, а через метки: «{AppData}\Telegram Desktop\tdata».
/// Так файлы встают на место, даже если на новой винде другое имя пользователя или «Документы» на другом диске.
/// </summary>
public sealed class PathResolver
{
    public static readonly string[] Tokens =
    [
        "Profile", "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "SavedGames",
        "AppData", "LocalAppData", "LocalLow", "ProgramData", "ProgramFiles", "ProgramFilesX86",
        "Public", "PublicDocuments", "Windows", "SystemDrive", "Steam", "UserFonts",
    ];

    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSandbox { get; private init; }
    public string? SandboxRoot { get; private init; }

    public IReadOnlyDictionary<string, string> Map => _map;

    private PathResolver() { }

    /// <summary>Настоящие папки текущего пользователя.</summary>
    public static PathResolver Live()
    {
        var r = new PathResolver();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sysDrive = Path.GetPathRoot(windows) ?? @"C:\";

        r._map["Profile"] = profile;
        r._map["Desktop"] = KnownFolders.Get(KnownFolders.Desktop) ?? Path.Combine(profile, "Desktop");
        r._map["Documents"] = KnownFolders.Get(KnownFolders.Documents) ?? Path.Combine(profile, "Documents");
        r._map["Downloads"] = KnownFolders.Get(KnownFolders.Downloads) ?? Path.Combine(profile, "Downloads");
        r._map["Pictures"] = KnownFolders.Get(KnownFolders.Pictures) ?? Path.Combine(profile, "Pictures");
        r._map["Videos"] = KnownFolders.Get(KnownFolders.Videos) ?? Path.Combine(profile, "Videos");
        r._map["Music"] = KnownFolders.Get(KnownFolders.Music) ?? Path.Combine(profile, "Music");
        r._map["SavedGames"] = KnownFolders.Get(KnownFolders.SavedGames) ?? Path.Combine(profile, "Saved Games");
        r._map["AppData"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        r._map["LocalAppData"] = local;
        r._map["LocalLow"] = KnownFolders.Get(KnownFolders.LocalLow) ?? Path.Combine(profile, "AppData", "LocalLow");
        r._map["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        r._map["ProgramFiles"] = Environment.GetEnvironmentVariable("ProgramW6432")
                                 ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        r._map["ProgramFilesX86"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        r._map["Public"] = KnownFolders.Get(KnownFolders.Public) ?? Path.Combine(sysDrive, "Users", "Public");
        r._map["PublicDocuments"] = KnownFolders.Get(KnownFolders.PublicDocuments) ?? Path.Combine(sysDrive, "Users", "Public", "Documents");
        r._map["Windows"] = windows;
        r._map["SystemDrive"] = sysDrive;
        r._map["UserFonts"] = Path.Combine(local, "Microsoft", "Windows", "Fonts");
        r._map["Steam"] = SteamPath() ?? Path.Combine(r._map["ProgramFilesX86"], "Steam");
        return r;
    }

    /// <summary>Песочница для проверок: каждая метка — своя папка внутри root, настоящие папки не трогаются.</summary>
    public static PathResolver Sandbox(string root)
    {
        var r = new PathResolver { IsSandbox = true, SandboxRoot = root };
        foreach (var t in Tokens)
            r._map[t] = Path.Combine(root, t == "SystemDrive" ? "C" : t);
        r._map["SystemDrive"] += Path.DirectorySeparatorChar;
        return r;
    }

    public string? Get(string token) => _map.TryGetValue(token, out var p) ? p : null;

    public string SystemDrive => _map["SystemDrive"];

    /// <summary>«{AppData}\X» → настоящий путь. Пути без метки возвращаются как есть.</summary>
    public string Resolve(string tokenPath)
    {
        if (string.IsNullOrEmpty(tokenPath) || tokenPath[0] != '{') return tokenPath;
        int end = tokenPath.IndexOf('}');
        if (end < 0) return tokenPath;
        var token = tokenPath[1..end];
        if (!_map.TryGetValue(token, out var basePath))
            throw new InvalidOperationException($"Неизвестная метка пути {{{token}}}");
        var rest = tokenPath[(end + 1)..].TrimStart('\\', '/');
        return rest.Length == 0 ? basePath : Path.Combine(basePath, rest.Replace('/', '\\'));
    }

    public bool TryResolve(string tokenPath, out string path)
    {
        try
        {
            path = Resolve(tokenPath);
            return true;
        }
        catch
        {
            path = "";
            return false;
        }
    }

    /// <summary>Настоящий путь → путь с самой «узкой» подходящей меткой. Чужие диски остаются абсолютными.</summary>
    public string Tokenize(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath).TrimEnd('\\');
        string? bestToken = null;
        int bestLen = -1;
        foreach (var (token, basePath) in _map)
        {
            var b = basePath.TrimEnd('\\');
            if (b.Length <= bestLen) continue;
            if (full.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase))
            {
                bestToken = token;
                bestLen = b.Length;
            }
        }
        if (bestToken is null) return full;
        var rest = full.Length > bestLen ? full[(bestLen + 1)..] : "";
        return rest.Length == 0 ? "{" + bestToken + "}" : "{" + bestToken + "}\\" + rest;
    }

    /// <summary>Лежит ли путь на системном диске (то, что сотрёт переустановка).</summary>
    public bool OnSystemDrive(string absolutePath) =>
        Path.GetFullPath(absolutePath).StartsWith(SystemDrive, StringComparison.OrdinalIgnoreCase);

    public static string? SteamPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is string s && s.Length > 0)
                return Path.GetFullPath(s.Replace('/', '\\'));
            using var m = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (m?.GetValue("InstallPath") is string s2 && s2.Length > 0) return s2;
        }
        catch { }
        return null;
    }
}

public static class KnownFolders
{
    public static readonly Guid Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    public static readonly Guid Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    public static readonly Guid Music = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    public static readonly Guid SavedGames = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");
    public static readonly Guid LocalLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");
    public static readonly Guid Public = new("DFDF76A2-C82A-4D63-906A-5644AC457385");
    public static readonly Guid PublicDocuments = new("ED4824AF-DCE4-45A8-81E2-FC7965083634");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private const uint KF_FLAG_DONT_VERIFY = 0x00004000;

    public static string? Get(Guid id)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            int hr = SHGetKnownFolderPath(id, KF_FLAG_DONT_VERIFY, IntPtr.Zero, out ptr);
            if (hr != 0 || ptr == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(ptr);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
    }
}
