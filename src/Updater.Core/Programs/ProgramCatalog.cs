using System.Text.RegularExpressions;

namespace Updater.Core;

public sealed class ProgramCatalog
{
    public List<ProgramRule> Rules { get; set; } = [];
    public List<PopularGroup> Popular { get; set; } = [];
    public RuntimePack Runtimes { get; set; } = new();

    private static readonly Lazy<ProgramCatalog> _shared = new(() => Json.Embedded<ProgramCatalog>("programs.json"));
    public static ProgramCatalog Shared => _shared.Value;
}

public sealed class ProgramRule
{
    public string Match { get; set; } = "";
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool Skip { get; set; }
    /// <summary>Прямая ссылка на установщик — для программ, которых нет в winget.</summary>
    public string? Download { get; set; }
    public string? Url { get; set; }
    public string? Note { get; set; }

    private Regex? _rx;
    public bool Matches(string displayName) =>
        (_rx ??= new Regex(Match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).IsMatch(displayName);
}

public sealed class PopularGroup
{
    public string Group { get; set; } = "";
    public List<PopularApp> Items { get; set; } = [];
}

public sealed class PopularApp
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class RuntimePack
{
    public string Title { get; set; } = "";
    public string Note { get; set; } = "";
    public List<string> Items { get; set; } = [];
}
