namespace Updater.Core;

/// <summary>Официальные установщики, которые можно скачать на флешку заранее (Catalog/installers.json).</summary>
public sealed class InstallerCatalog
{
    public List<InstallerDef> Installers { get; set; } = [];

    private static readonly Lazy<InstallerCatalog> _shared = new(() => Json.Embedded<InstallerCatalog>("installers.json"));
    public static InstallerCatalog Shared => _shared.Value;

    private Dictionary<string, InstallerDef>? _byId;
    public InstallerDef? Find(string? wingetId) =>
        wingetId is not null && (_byId ??= Installers.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase)).TryGetValue(wingetId, out var d) ? d : null;
}

public sealed class InstallerDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>exe или msi.</summary>
    public string Type { get; set; } = "exe";
    /// <summary>Аргументы тихой установки; пусто — установщик откроется с вопросами.</summary>
    public string? Silent { get; set; }
    /// <summary>Из набора «первым делом» (браузер, клиент игр) — предлагается скачать даже без установленной программы.</summary>
    public bool Bootstrap { get; set; }

    public string FileName(string? stemOverride = null) => Fmt.SafeName(stemOverride ?? Name) + "." + (Type == "msi" ? "msi" : "exe");
}
