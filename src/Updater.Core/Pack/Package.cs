namespace Updater.Core;

/// <summary>Устройство папки сборки.</summary>
public static class Package
{
    /// <summary>Название программы, которое видит пользователь (окно, папка сборки, exe, отчёт).</summary>
    public const string AppName = "Updater";
    public const string FolderName = AppName;
    public const string ExeName = AppName + ".exe";

    /// <summary>Журналы и файлы отката на этом ПК: %LOCALAPPDATA%\Updater.</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>Временная папка для скачиваний и служебных скриптов.</summary>
    public static string TempDir => Path.Combine(Path.GetTempPath(), AppName);

    public const string ReadmeName = "Как пользоваться.txt";
    public const string ReportName = "Отчёт.html";
    public const string JournalName = "Журнал.txt";
    public const string BitLockerName = "Ключи BitLocker.txt";

    public const string FilesDir = "Файлы";
    public const string AppsDir = "Программы";
    public const string InstallersDir = "Программы/Установщики";
    public const string GamesDir = "Игры";
    public const string WindowsDir = "Windows";
    public const string DriversDir = "Windows/Драйверы";
    public const string WifiDir = "Windows/Wi-Fi";
    public const string FontsDir = "Windows/Шрифты";

    public static string SectionDir(Section s) => s switch
    {
        Section.Files => FilesDir,
        Section.Games => GamesDir,
        Section.Windows => WindowsDir,
        _ => AppsDir,
    };

    public static string Abs(string packageRoot, string relative) =>
        Path.Combine(packageRoot, relative.Replace('/', '\\'));

    /// <summary>Сборки, сделанные до переименования программы, лежат в папке «Переезд» с описью pereezd.json.</summary>
    private const string LegacyFolderName = "Переезд";

    /// <summary>Есть ли сборка в папке (или в её подпапке «Updater»).</summary>
    public static string? Find(string dir)
    {
        try
        {
            if (ManifestPath(dir) is not null) return dir;
            foreach (var name in new[] { FolderName, LegacyFolderName })
            {
                var sub = Path.Combine(dir, name);
                if (ManifestPath(sub) is not null) return sub;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Файл описи в папке сборки: новый updater.json или прежний pereezd.json.</summary>
    public static string? ManifestPath(string packageRoot)
    {
        foreach (var name in new[] { PackageManifest.FileName, PackageManifest.LegacyFileName })
        {
            var p = Path.Combine(packageRoot, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Сборка рядом с программой, а если программу запустили не с флешки — на любой флешке или диске.</summary>
    public static string? Discover(string exeDir)
    {
        var near = Find(exeDir);
        if (near is not null) return near;
        var parent = Directory.GetParent(exeDir)?.FullName;
        if (parent is not null && Find(parent) is { } p) return p;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                if (Find(d.RootDirectory.FullName) is { } found) return found;
            }
            catch { }
        }
        return null;
    }

    public static PackageManifest? TryLoad(string packageRoot)
    {
        try { return ManifestPath(packageRoot) is { } path ? Json.Read<PackageManifest>(path) : null; }
        catch { return null; }
    }
}

/// <summary>Куда можно сложить вещи: флешки и диски, кроме системного.</summary>
public sealed class DriveOption
{
    public required string Root { get; init; }
    public required string Label { get; init; }
    public long Free { get; init; }
    public long Size { get; init; }
    public string Format { get; init; } = "";
    public bool Removable { get; init; }
    public bool IsSystem { get; init; }
    public string? CustomFolder { get; init; }

    public bool IsFat32 => Format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) && !Format.Equals("exFAT", StringComparison.OrdinalIgnoreCase);
    public string Destination => CustomFolder ?? Path.Combine(Root, Package.FolderName);

    public string Display => CustomFolder is not null
        ? $"{CustomFolder} · свободно {Fmt.Size(Free)}"
        : $"{Root.TrimEnd('\\')} {Label} · свободно {Fmt.Size(Free)} из {Fmt.Size(Size)}";

    public override string ToString() => Display;

    public static List<DriveOption> List(string systemDrive)
    {
        var list = new List<DriveOption>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Removable or DriveType.Fixed or DriveType.Network)) continue;
                bool removable = d.DriveType == DriveType.Removable;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (removable ? "Флешка" : "Диск") : d.VolumeLabel;
                list.Add(new DriveOption
                {
                    Root = d.RootDirectory.FullName,
                    Label = (removable ? "флешка · " : "") + label,
                    Free = d.AvailableFreeSpace,
                    Size = d.TotalSize,
                    Format = d.DriveFormat,
                    Removable = removable,
                    IsSystem = d.RootDirectory.FullName.Equals(systemDrive, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch { }
        }
        // Флешки первыми — обычно собирают на них.
        return list.OrderBy(d => d.IsSystem).ThenByDescending(d => d.Removable).ThenBy(d => d.Root).ToList();
    }

    public static DriveOption ForFolder(string folder)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder;
        var d = new DriveInfo(root);
        return new DriveOption
        {
            Root = root,
            Label = d.VolumeLabel,
            Free = d.AvailableFreeSpace,
            Size = d.TotalSize,
            Format = d.DriveFormat,
            Removable = d.DriveType == DriveType.Removable,
            CustomFolder = folder,
        };
    }
}
