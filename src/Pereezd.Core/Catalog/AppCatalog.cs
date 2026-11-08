namespace Pereezd.Core;

/// <summary>Каталог программ, чьи настройки умеем переносить (Catalog/apps.json).</summary>
public sealed class AppCatalog
{
    public Dictionary<string, List<string>> ExcludeSets { get; set; } = [];
    public List<AppDef> Apps { get; set; } = [];

    private static readonly Lazy<AppCatalog> _shared = new(() => Json.Embedded<AppCatalog>("apps.json"));
    public static AppCatalog Shared => _shared.Value;

    public List<string> ExcludesFor(RootDef r)
    {
        var list = new List<string>(r.Exclude);
        if (r.ExcludeSet is not null && ExcludeSets.TryGetValue(r.ExcludeSet, out var set)) list.AddRange(set);
        return list;
    }
}

public sealed class AppDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Glyph { get; set; } = "";
    public string? Group { get; set; }
    /// <summary>apps (по умолчанию), games или windows.</summary>
    public string? Section { get; set; }
    public List<RootDef> Roots { get; set; } = [];
    public List<string> Registry { get; set; } = [];
    public List<string> Processes { get; set; } = [];
    public bool Sensitive { get; set; }
    public bool DefaultOff { get; set; }
    public string? Note { get; set; }
    public string? Reminder { get; set; }
    public string? WingetId { get; set; }
    public string? PasswordsUrl { get; set; }
    public string? Exe { get; set; }
    public string? After { get; set; }

    public Section SectionValue => Section?.ToLowerInvariant() switch
    {
        "games" => Core.Section.Games,
        "windows" => Core.Section.Windows,
        _ => Core.Section.Apps,
    };
}

public sealed class RootDef
{
    public string Path { get; set; } = "";
    public List<string> Exclude { get; set; } = [];
    public string? ExcludeSet { get; set; }
}

/// <summary>Сохранения игр в нестандартных местах (Catalog/games.json).</summary>
public sealed class GameCatalog
{
    public List<AppDef> Games { get; set; } = [];
    public Dictionary<string, string> UnrealNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> LocalLowSkip { get; set; } = [];
    public List<string> UnrealSkip { get; set; } = [];

    private static readonly Lazy<GameCatalog> _shared = new(() =>
    {
        var c = Json.Embedded<GameCatalog>("games.json");
        c.UnrealNames = new Dictionary<string, string>(c.UnrealNames, StringComparer.OrdinalIgnoreCase);
        return c;
    });

    public static GameCatalog Shared => _shared.Value;
}
