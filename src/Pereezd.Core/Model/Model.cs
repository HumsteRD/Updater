using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Pereezd.Core;

public enum Section { Files, Programs, Apps, Games, Windows, Important }

public enum ItemKind
{
    /// <summary>Папка копируется как есть, файл в файл: забрать её можно и без программы.</summary>
    Folder,
    /// <summary>Настройки программы или сохранения игры — один zip на пункт: на флешку пишется быстрее, чем тысячи мелких файлов.</summary>
    Archive,
    Programs,
    /// <summary>Скачать официальные установщики на флешку (на случай без интернета или без winget).</summary>
    Installers,
    WindowsSettings,
    Wifi,
    Drivers,
    Fonts,
    BitLocker,
    /// <summary>Только совет или предупреждение, переносить нечего.</summary>
    Note,
}

public enum ItemStatus { Pending, Running, Done, Warning, Failed, Skipped }

/// <summary>Откуда брать файлы пункта: путь с меткой и маски исключения.</summary>
public sealed class SourceRoot
{
    public string Path { get; set; } = "";
    public List<string> Exclude { get; set; } = [];
    /// <summary>Только файлы верхнего уровня, без подпапок (корень профиля).</summary>
    public bool TopOnly { get; set; }

    public SourceRoot() { }

    public SourceRoot(string path, IEnumerable<string>? exclude = null, bool topOnly = false)
    {
        Path = path;
        if (exclude is not null) Exclude = [.. exclude];
        TopOnly = topOnly;
    }
}

/// <summary>Строка плана: что можно забрать (на старой винде) или вернуть (на новой).</summary>
public sealed class PlanItem : Observable
{
    public required string Id { get; init; }
    public Section Section { get; init; }
    public ItemKind Kind { get; set; }
    public required string Title { get; init; }
    public string Glyph { get; init; } = "";
    public string? Note { get; set; }
    public List<SourceRoot> Roots { get; init; } = [];
    public string[] Processes { get; init; } = [];
    public string[] Registry { get; init; } = [];
    public bool Sensitive { get; init; }
    /// <summary>Нужно не всем (мелкие настройки утилит, инструменты разработчика) — заранее не отмечено.</summary>
    public bool Optional { get; init; }
    /// <summary>Категория из каталога: «Браузеры», «Общение», «Игры»…</summary>
    public string? Category { get; init; }
    /// <summary>Порядок появления: при группировке сохраняется порядок каталога.</summary>
    public int Order { get; set; }
    public string GroupTitle => Optional ? "По желанию" : "Главное";
    public int GroupOrder => Optional ? 1 : 0;
    public string? WingetId { get; init; }
    /// <summary>Что сделать после восстановления: перезапустить Проводник и т. п.</summary>
    public string? After { get; init; }
    /// <summary>Страница экспорта паролей браузера (chrome://password-manager/settings).</summary>
    public string? PasswordsUrl { get; init; }
    public string? BrowserExe { get; init; }
    /// <summary>Пункт из сборки (при восстановлении).</summary>
    public StoredItem? Stored { get; init; }
    /// <summary>Дочерние строки: программы, драйверы, шрифты.</summary>
    public ObservableCollection<SubItem> Children { get; } = [];
    public object? Payload { get; set; }

    private string? _detail;
    public string? Detail { get => _detail; set => Set(ref _detail, value); }

    private string? _warning;
    /// <summary>Короткая пометка-чип: «Удалится при переустановке».</summary>
    public string? Warning { get => _warning; set => Set(ref _warning, value); }

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private bool _selectable = true;
    public bool Selectable { get => _selectable; set => Set(ref _selectable, value); }

    private bool _hidden;
    /// <summary>Пусто — показывать нечего (папка игры без файлов и т. п.).</summary>
    public bool Hidden { get => _hidden; set => Set(ref _hidden, value); }

    private long _bytes;
    public long Bytes
    {
        get => _bytes;
        set
        {
            if (Set(ref _bytes, value)) Raise(nameof(SizeText));
        }
    }

    private int _files;
    public int Files { get => _files; set => Set(ref _files, value); }

    private bool _measured;
    public bool Measured
    {
        get => _measured;
        set
        {
            if (Set(ref _measured, value)) Raise(nameof(SizeText));
        }
    }

    private string? _sizeOverride;
    /// <summary>Подпись вместо размера: «41 программа».</summary>
    public string? SizeOverride
    {
        get => _sizeOverride;
        set
        {
            if (Set(ref _sizeOverride, value)) Raise(nameof(SizeText));
        }
    }

    public string SizeText => SizeOverride ?? (Measured ? Fmt.Size(Bytes) : Kind == ItemKind.Note ? "" : "…");

    private ItemStatus _status;
    public ItemStatus Status { get => _status; set => Set(ref _status, value); }

    private string? _statusText;
    public string? StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public bool IsData => Kind is ItemKind.Folder or ItemKind.Archive;

    public override string ToString() => $"{Section}/{Id}: {Title} ({SizeText})";
}

/// <summary>Отмечаемая строка внутри пункта: программа, драйвер, шрифт, игра Steam.</summary>
public sealed class SubItem : Observable
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public string? Chip { get; init; }
    public bool ChipIsGood { get; init; }
    public long Bytes { get; init; }
    public object? Tag { get; init; }
    /// <summary>Ссылка «где взять» (сайт программы, страница в Store).</summary>
    public string? Link { get; init; }
    public string? LinkText { get; init; }

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private bool _selectable = true;
    public bool Selectable { get => _selectable; set => Set(ref _selectable, value); }

    private ItemStatus _status;
    public ItemStatus Status { get => _status; set => Set(ref _status, value); }

    private string? _statusText;
    public string? StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public string SizeText => Bytes > 0 ? Fmt.Size(Bytes) : "";
}

// ───────────────────────────── Сборка на диске ─────────────────────────────

/// <summary>Опись сборки — updater.json в её корне.</summary>
public sealed class PackageManifest
{
    public const string FileName = "updater.json";
    /// <summary>Так опись называлась до переименования программы.</summary>
    public const string LegacyFileName = "pereezd.json";
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public string App { get; set; } = "";
    /// <summary>collecting — сборы не закончены; complete — всё сложено.</summary>
    public string Status { get; set; } = "collecting";
    public DateTime Created { get; set; }
    public DateTime? Finished { get; set; }
    public SourcePc Source { get; set; } = new();
    public List<StoredItem> Items { get; set; } = [];
    public List<ProgramRecord> Programs { get; set; } = [];
    public List<StoreAppRecord> StoreApps { get; set; } = [];
    public List<DriverRecord> Drivers { get; set; } = [];
    public List<FontRecord> Fonts { get; set; } = [];
    public List<string> WifiProfiles { get; set; } = [];
    public WindowsSnapshot? Windows { get; set; }
    public List<SteamGameRecord> SteamGames { get; set; } = [];
    public List<string> Reminders { get; set; } = [];

    [JsonIgnore] public bool IsComplete => Status == "complete";
    [JsonIgnore] public long TotalBytes => Items.Sum(i => i.Bytes);
}

public sealed class SourcePc
{
    public string Computer { get; set; } = "";
    public string User { get; set; } = "";
    public string Windows { get; set; } = "";
    public int Build { get; set; }
    public string Profile { get; set; } = "";
    public Dictionary<string, string> Folders { get; set; } = [];
}

public sealed class StoredItem
{
    public string Id { get; set; } = "";
    public Section Section { get; set; }
    public ItemKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string? Note { get; set; }
    public string Glyph { get; set; } = "";
    /// <summary>Путь внутри сборки (папка или zip), через «/».</summary>
    public string Stored { get; set; } = "";
    public List<StoredRoot> Roots { get; set; } = [];
    public List<string> RegFiles { get; set; } = [];
    public string[] Processes { get; set; } = [];
    public bool Sensitive { get; set; }
    public string? WingetId { get; set; }
    public string? After { get; set; }
    public long Bytes { get; set; }
    public int Files { get; set; }
    public bool Complete { get; set; }
    public int FailedCount { get; set; }
    public List<string> Failed { get; set; } = [];
}

public sealed class StoredRoot
{
    /// <summary>Куда вернуть: путь с меткой.</summary>
    public string Target { get; set; } = "";
    /// <summary>Префикс в zip («0», «1») или подпапка у пункта-папки.</summary>
    public string Entry { get; set; } = "";
    public bool IsFile { get; set; }
}

public sealed class ProgramRecord
{
    public string Name { get; set; } = "";
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? WingetId { get; set; }
    /// <summary>winget или msstore.</summary>
    public string? Source { get; set; }
    public string? Url { get; set; }
    /// <summary>Прямая ссылка на установщик (для программ, которых нет в winget).</summary>
    public string? Download { get; set; }
    public string? Note { get; set; }
    public string? InstallLocation { get; set; }
    /// <summary>Установщик, скачанный на флешку: путь внутри сборки.</summary>
    public string? LocalInstaller { get; set; }
    /// <summary>Аргументы тихой установки локального установщика.</summary>
    public string? SilentArgs { get; set; }
    public bool LocalIsMsi { get; set; }

    /// <summary>Можно поставить без ручной возни: winget, прямая ссылка или установщик на флешке.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AutoInstallable => WingetId is not null || Download is not null || LocalInstaller is not null;
    /// <summary>Стоит не на системном диске — переустановка его не тронет.</summary>
    public bool OffSystemDrive { get; set; }
    public bool IsRuntime { get; set; }
    public string? Group { get; set; }
}

public sealed class StoreAppRecord
{
    public string Name { get; set; } = "";
    public string FamilyName { get; set; } = "";
}

public sealed class DriverRecord
{
    public string Inf { get; set; } = "";
    public string Package { get; set; } = "";
    public string Device { get; set; } = "";
    public string Class { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Version { get; set; } = "";
    public long Bytes { get; set; }
    public bool Stored { get; set; }
}

public sealed class FontRecord
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public bool PerUser { get; set; }
}

public sealed class SteamGameRecord
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public string Library { get; set; } = "";
    public long Bytes { get; set; }
}
