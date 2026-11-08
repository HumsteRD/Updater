using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Updater.Core;

public sealed class WinSettingsCatalog
{
    public List<WinSettingDef> Settings { get; set; } = [];

    private static readonly Lazy<WinSettingsCatalog> _shared = new(() => Json.Embedded<WinSettingsCatalog>("windows.json"));
    public static WinSettingsCatalog Shared => _shared.Value;

    public WinSettingDef? Find(string id) => Settings.FirstOrDefault(s => s.Id == id);
}

public sealed class WinSettingDef
{
    public string Id { get; set; } = "";
    public string Group { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Hint { get; set; }
    /// <summary>choice, raw, special.</summary>
    public string Kind { get; set; } = "choice";
    public string? Handler { get; set; }
    public List<RegRef> Values { get; set; } = [];
    public List<WinOption> Options { get; set; } = [];
    public string? Default { get; set; }
    public string? Recommended { get; set; }
    public string? Notify { get; set; }
    public bool Admin { get; set; }
    public int MinBuild { get; set; }
    public string? Display { get; set; }

    public bool IsSpecial => Kind == "special";
    public bool IsRaw => Kind == "raw";
    public bool AppliesTo(int build) => MinBuild == 0 || build >= MinBuild;

    public WinOption? Option(string? id) => id is null ? null : Options.FirstOrDefault(o => o.Id == id);
}

public sealed class RegRef
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "dword";
}

public sealed class WinOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public List<JsonElement?> Data { get; set; } = [];

    public RegData DataFor(int index, string type)
    {
        if (index >= Data.Count || Data[index] is not { } el || el.ValueKind == JsonValueKind.Null) return RegData.Absent();
        var text = el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText();
        return new RegData { Type = type, Value = text };
    }
}

/// <summary>Как было на старом ПК.</summary>
public sealed class CapturedSetting
{
    /// <summary>Совпавший вариант, «raw» для переноса как есть, null — не удалось прочитать.</summary>
    public string? Option { get; set; }
    public List<RegData> Values { get; set; } = [];
    public Dictionary<string, string> Extra { get; set; } = [];
    public string? Summary { get; set; }
}

public sealed class WindowsSnapshot
{
    public int Build { get; set; }
    public Dictionary<string, CapturedSetting> Settings { get; set; } = [];
}

/// <summary>Что выбрал пользователь: id варианта, <see cref="Old"/> — «как было», пусто — не трогать.</summary>
public sealed record SettingChoice(WinSettingDef Def, string OptionId)
{
    public const string Old = "old";
    public const string Keep = "";
}

public sealed class ApplyReport
{
    public int Applied { get; set; }
    public int Unchanged { get; set; }
    public List<string> Failed { get; } = [];
    public bool NeedsReboot { get; set; }
    public bool NeedsRelogin { get; set; }
    public string? RollbackFile { get; set; }
}

/// <summary>Файл отката: прежние значения всего, что поменяли на новой винде.</summary>
public sealed class RollbackFile
{
    public DateTime Created { get; set; }
    public List<RollbackEntry> Registry { get; set; } = [];
    public Dictionary<string, string> Special { get; set; } = [];
}

public sealed class RollbackEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public RegData Data { get; set; } = new();
}

/// <summary>
/// Запомнить настройки Windows на старом ПК и выставить их (или рекомендованные) на новом.
/// Перед каждой записью сохраняется прежнее значение — всё можно откатить.
/// </summary>
public sealed class WindowsSettings(IRegistryAccess reg, Journal log, bool sandbox = false)
{
    public const string ClassicMenuKey = @"HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private static readonly string[] StandardSchemes =
    [
        "381b4222-f694-41f0-9685-ff5bb260df2e", // сбалансированная
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", // высокая производительность
        "a1841308-3541-4fab-bc81-f71556f20b4a", // экономия энергии
        "e9a42b02-d5df-448d-aa00-03f14749eb61", // максимальная производительность (шаблон)
    ];

    public IRegistryAccess Registry => reg;
    public bool IsSandbox => sandbox;

    // ───────────────────────────── Чтение ─────────────────────────────

    /// <summary>Прочитать настройку из реестра и узнать, какому варианту она соответствует.</summary>
    public CapturedSetting ReadRegistry(WinSettingDef d)
    {
        var c = new CapturedSetting();
        foreach (var v in d.Values)
        {
            try { c.Values.Add(reg.Read(v.Key, v.Name)); }
            catch { c.Values.Add(RegData.Absent()); }
        }
        if (d.IsRaw)
        {
            c.Option = c.Values.All(v => v.IsAbsent) ? null : "raw";
        }
        else
        {
            foreach (var o in d.Options)
            {
                bool all = true;
                for (int i = 0; i < d.Values.Count && all; i++)
                    all = c.Values[i].SameAs(o.DataFor(i, d.Values[i].Type));
                if (all)
                {
                    c.Option = o.Id;
                    break;
                }
            }
            // Значений нет — Windows ведёт себя «по умолчанию».
            if (c.Option is null && c.Values.All(v => v.IsAbsent)) c.Option = d.Default;
        }
        c.Summary = Describe(d, c);
        return c;
    }

    public string Describe(WinSettingDef d, CapturedSetting c)
    {
        if (c.Summary is not null && d.IsSpecial) return c.Summary;
        if (c.Option is null) return "как в Windows по умолчанию";
        if (!d.IsRaw && c.Option != "raw") return d.Option(c.Option)?.Label ?? c.Option;
        if (d.Display == "color")
        {
            var v = c.Values.FirstOrDefault();
            if (v is { IsAbsent: false } && uint.TryParse(v.Value, out var abgr))
                return $"#{abgr & 0xFF:X2}{(abgr >> 8) & 0xFF:X2}{(abgr >> 16) & 0xFF:X2}";
            return "свой цвет";
        }
        if (d.Display is not null)
        {
            var args = c.Values.Select(v => (object)(v.IsAbsent ? "—" : v.Value ?? "")).ToArray();
            try { return string.Format(CultureInfo.InvariantCulture, d.Display, args); }
            catch { }
        }
        return string.Join(", ", c.Values.Where(v => !v.IsAbsent).Select(v => v.Value));
    }

    /// <summary>Снимок всех настроек старого ПК. Файлы (обои, схема питания) кладутся в windowsDir.</summary>
    public async Task<WindowsSnapshot> CaptureAsync(string windowsDir, CancellationToken ct = default)
    {
        var snap = new WindowsSnapshot { Build = Native.WindowsBuild };
        foreach (var d in WinSettingsCatalog.Shared.Settings)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var c = d.IsSpecial ? await CaptureSpecialAsync(d, windowsDir, ct).ConfigureAwait(false) : ReadRegistry(d);
                if (c is not null) snap.Settings[d.Id] = c;
            }
            catch (Exception ex)
            {
                log.Warn($"Настройка «{d.Title}» не прочиталась: {ex.Message}");
            }
        }
        return snap;
    }

    /// <summary>Как настройка выглядит на этом ПК сейчас (для подсказки «сейчас: …»).</summary>
    public async Task<CapturedSetting?> ReadCurrentAsync(WinSettingDef d, CancellationToken ct = default)
    {
        try
        {
            if (!d.IsSpecial) return ReadRegistry(d);
            return d.Handler switch
            {
                "classicmenu" => ClassicMenuState(),
                "timezone" => new CapturedSetting { Option = "raw", Summary = TimeZoneInfo.Local.DisplayName, Extra = { ["id"] = TimeZoneInfo.Local.Id } },
                "computername" => new CapturedSetting { Option = "raw", Summary = Environment.MachineName, Extra = { ["name"] = Environment.MachineName } },
                "power" => await ReadPowerAsync(ct).ConfigureAwait(false),
                "wallpaper" => new CapturedSetting
                {
                    Option = "raw",
                    Summary = Path.GetFileName(reg.Read(@"HKCU\Control Panel\Desktop", "WallPaper").Value ?? "") is { Length: > 0 } wp
                              && !wp.StartsWith("TranscodedWallpaper", StringComparison.OrdinalIgnoreCase) ? wp : "картинка с рабочего стола",
                },
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<CapturedSetting?> CaptureSpecialAsync(WinSettingDef d, string windowsDir, CancellationToken ct)
    {
        switch (d.Handler)
        {
            case "classicmenu":
                return ClassicMenuState();

            case "wallpaper":
            {
                var path = reg.Read(@"HKCU\Control Panel\Desktop", "WallPaper").Value;
                string? source = null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) source = path;
                var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (source is null && File.Exists(transcoded)) source = transcoded;
                if (source is null) return null;
                var ext = Path.GetExtension(source);
                if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
                Directory.CreateDirectory(windowsDir);
                var name = "обои" + ext.ToLowerInvariant();
                File.Copy(source, Path.Combine(windowsDir, name), overwrite: true);
                // «TranscodedWallpaper» — служебная копия Windows, а не имя картинки.
                var original = Path.GetFileName(path ?? "");
                if (original.Length == 0 || original.StartsWith("TranscodedWallpaper", StringComparison.OrdinalIgnoreCase)) original = name;
                var c = new CapturedSetting { Option = "raw", Summary = original == name ? "картинка с рабочего стола" : original };
                c.Extra["file"] = name;
                c.Extra["original"] = original;
                c.Extra["style"] = reg.Read(@"HKCU\Control Panel\Desktop", "WallpaperStyle").Value ?? "10";
                c.Extra["tile"] = reg.Read(@"HKCU\Control Panel\Desktop", "TileWallpaper").Value ?? "0";
                return c;
            }

            case "languages":
            {
                var r = await Proc.PowerShellAsync(LanguageListScript, ct).ConfigureAwait(false);
                var lines = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => l.Contains('|')).ToList();
                if (lines.Count == 0) return null;
                var c = new CapturedSetting { Option = "raw" };
                c.Extra["list"] = string.Join(";", lines);
                c.Summary = string.Join(", ", lines.Select(l => LanguageName(l.Split('|')[0])));
                return c;
            }

            case "timezone":
            {
                var tz = TimeZoneInfo.Local;
                var c = new CapturedSetting { Option = "raw", Summary = tz.DisplayName };
                c.Extra["id"] = tz.Id;
                return c;
            }

            case "power":
            {
                var c = await ReadPowerAsync(ct).ConfigureAwait(false);
                if (c is null) return null;
                var guid = c.Extra["guid"];
                if (!StandardSchemes.Contains(guid, StringComparer.OrdinalIgnoreCase) && !sandbox)
                {
                    Directory.CreateDirectory(windowsDir);
                    var file = Path.Combine(windowsDir, "электропитание.pow");
                    var r = await Proc.RunAsync(Proc.System32("powercfg.exe"), $"/export \"{file}\" {guid}", ct).ConfigureAwait(false);
                    if (r.Ok && File.Exists(file)) c.Extra["file"] = "электропитание.pow";
                }
                return c;
            }

            case "computername":
            {
                var c = new CapturedSetting { Option = "raw", Summary = Environment.MachineName };
                c.Extra["name"] = Environment.MachineName;
                return c;
            }

            case "env":
            {
                var c = new CapturedSetting { Option = "raw" };
                c.Extra["@profile"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                int count = 0;
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
                if (k is null) return null;
                foreach (var name in k.GetValueNames())
                {
                    if (IsVolatileEnv(name)) continue;
                    var data = reg.Read(@"HKCU\Environment", name);
                    if (data.IsAbsent) continue;
                    c.Extra[name] = data.Value ?? "";
                    c.Extra["@type:" + name] = data.Type;
                    count++;
                }
                if (count == 0) return null;
                var names = c.Extra.Keys.Where(k2 => !k2.StartsWith('@')).ToList();
                c.Summary = string.Join(", ", names.Take(4)) + (names.Count > 4 ? $" и ещё {names.Count - 4}" : "");
                return c;
            }
        }
        return null;
    }

    private static bool IsVolatileEnv(string name) =>
        name.Equals("TEMP", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TMP", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase);

    private CapturedSetting ClassicMenuState()
    {
        bool classic = reg.KeyExists(ClassicMenuKey + @"\InprocServer32");
        return new CapturedSetting
        {
            Option = classic ? "classic" : "modern",
            Summary = classic ? "Классическое" : "Новое, сокращённое",
        };
    }

    public static IReadOnlyList<(string Id, string Label)> SpecialOptions(WinSettingDef d) => d.Handler switch
    {
        "classicmenu" => [("classic", "Классическое"), ("modern", "Новое, сокращённое")],
        "power" => [("balanced", "Сбалансированная"), ("high", "Высокая производительность"), ("ultimate", "Максимальная производительность")],
        _ => [],
    };

    private static async Task<CapturedSetting?> ReadPowerAsync(CancellationToken ct)
    {
        var r = await Proc.RunAsync(Proc.System32("powercfg.exe"), "/getactivescheme", ct).ConfigureAwait(false);
        var m = Regex.Match(r.Output, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)");
        if (!m.Success) return null;
        var c = new CapturedSetting { Option = "raw", Summary = m.Groups[2].Value.Trim() };
        c.Extra["guid"] = m.Groups[1].Value.ToLowerInvariant();
        c.Extra["name"] = m.Groups[2].Value.Trim();
        return c;
    }

    private static string LanguageName(string tag)
    {
        try { return CultureInfo.GetCultureInfo(tag).DisplayName; }
        catch { return tag; }
    }

    // ───────────────────────────── Запись ─────────────────────────────

    public async Task<ApplyReport> ApplyAsync(IEnumerable<SettingChoice> choices, WindowsSnapshot? old, string? packageRoot,
        string rollbackPath, CancellationToken ct = default)
    {
        var report = new ApplyReport { RollbackFile = rollbackPath };
        var rollback = new RollbackFile { Created = DateTime.Now };
        var notify = new HashSet<string>();
        bool admin = Native.IsAdmin();

        foreach (var choice in choices)
        {
            ct.ThrowIfCancellationRequested();
            var d = choice.Def;
            if (choice.OptionId == SettingChoice.Keep) continue;
            if (d.Admin && !admin && !sandbox)
            {
                report.Failed.Add($"{d.Title}: нужны права администратора");
                continue;
            }
            try
            {
                bool changed = d.IsSpecial
                    ? await ApplySpecialAsync(d, choice.OptionId, old?.Settings.GetValueOrDefault(d.Id), packageRoot, rollback, ct).ConfigureAwait(false)
                    : ApplyRegistry(d, choice.OptionId, old?.Settings.GetValueOrDefault(d.Id), rollback);
                if (changed)
                {
                    report.Applied++;
                    if (d.Notify is not null) notify.Add(d.Notify);
                    log.Ok($"{d.Title}: {OptionLabel(d, choice.OptionId, old)}");
                }
                else report.Unchanged++;
            }
            catch (Exception ex)
            {
                report.Failed.Add($"{d.Title}: {Friendly(ex)}");
                log.Warn($"{d.Title}: не получилось — {Friendly(ex)}");
            }
            // Файл отката пишется после каждой настройки: если программа упадёт, откатить можно будет всё сделанное.
            SaveRollback(rollbackPath, rollback);
        }

        if (!sandbox) RunNotifications(notify);
        report.NeedsReboot = notify.Contains("reboot");
        report.NeedsRelogin = notify.Contains("relogin");
        return report;
    }

    public string OptionLabel(WinSettingDef d, string optionId, WindowsSnapshot? old)
    {
        if (optionId == SettingChoice.Old)
            return "как было — " + (old?.Settings.GetValueOrDefault(d.Id)?.Summary ?? "");
        return d.Option(optionId)?.Label ?? SpecialOptions(d).FirstOrDefault(o => o.Id == optionId).Label ?? optionId;
    }

    private static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Windows не дала изменить этот параметр",
        System.Security.SecurityException => "Windows не дала изменить этот параметр",
        _ => ex.Message,
    };

    private bool ApplyRegistry(WinSettingDef d, string optionId, CapturedSetting? old, RollbackFile rollback)
    {
        List<RegData> target;
        if (optionId == SettingChoice.Old)
        {
            if (old is null || old.Values.Count != d.Values.Count) return false;
            target = old.Values;
        }
        else
        {
            var o = d.Option(optionId) ?? throw new InvalidOperationException("Нет варианта " + optionId);
            target = d.Values.Select((v, i) => o.DataFor(i, v.Type)).ToList();
        }

        bool changed = false;
        for (int i = 0; i < d.Values.Count; i++)
        {
            var v = d.Values[i];
            var want = target[i];
            // «Как было», но на старом ПК значения не было — ничего не удаляем: на новой винде его тоже нет
            // или оно задано системой.
            if (optionId == SettingChoice.Old && want.IsAbsent) continue;
            var now = reg.Read(v.Key, v.Name);
            if (now.SameAs(want)) continue;
            rollback.Registry.Add(new RollbackEntry { Key = v.Key, Name = v.Name, Data = now });
            reg.Write(v.Key, v.Name, want);
            changed = true;
        }
        return changed;
    }

    private async Task<bool> ApplySpecialAsync(WinSettingDef d, string optionId, CapturedSetting? old, string? packageRoot,
        RollbackFile rollback, CancellationToken ct)
    {
        switch (d.Handler)
        {
            case "classicmenu":
            {
                bool wantClassic = optionId == SettingChoice.Old ? old?.Option == "classic" : optionId == "classic";
                bool isClassic = reg.KeyExists(ClassicMenuKey + @"\InprocServer32");
                if (wantClassic == isClassic) return false;
                rollback.Special["classicmenu"] = isClassic ? "classic" : "modern";
                if (wantClassic)
                {
                    reg.EnsureKey(ClassicMenuKey + @"\InprocServer32");
                    reg.Write(ClassicMenuKey + @"\InprocServer32", "", new RegData { Type = "sz", Value = "" });
                }
                else reg.DeleteKeyTree(ClassicMenuKey);
                return true;
            }

            case "env":
            {
                if (old is null) return false;
                var oldProfile = old.Extra.GetValueOrDefault("@profile") ?? "";
                var newProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                bool changed = false;
                foreach (var (name, rawValue) in old.Extra)
                {
                    if (name.StartsWith('@')) continue;
                    var type = old.Extra.GetValueOrDefault("@type:" + name) ?? "sz";
                    var value = oldProfile.Length > 0 ? ReplaceIgnoreCase(rawValue, oldProfile, newProfile) : rawValue;
                    var now = reg.Read(@"HKCU\Environment", name);
                    string? result;
                    if (name.Equals("Path", StringComparison.OrdinalIgnoreCase))
                    {
                        var have = (now.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
                        var add = value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                            .Where(p => !have.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
                        if (add.Count == 0) continue;
                        result = string.Join(";", have.Concat(add));
                        type = now.IsAbsent ? type : now.Type;
                    }
                    else
                    {
                        if (!now.IsAbsent) continue; // своё значение новой винды не трогаем
                        result = value;
                    }
                    rollback.Registry.Add(new RollbackEntry { Key = @"HKCU\Environment", Name = name, Data = now });
                    reg.Write(@"HKCU\Environment", name, new RegData { Type = type, Value = result });
                    changed = true;
                }
                return changed;
            }
        }

        // Остальные особые настройки меняют систему напрямую — в песочнице их не трогаем.
        if (sandbox) return false;

        switch (d.Handler)
        {
            case "wallpaper":
            {
                if (old is null || packageRoot is null || !old.Extra.TryGetValue("file", out var file)) return false;
                var src = Path.Combine(packageRoot, "Windows", file);
                if (!File.Exists(src)) return false;
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Обои");
                Directory.CreateDirectory(dir);
                var original = old.Extra.GetValueOrDefault("original") ?? file;
                if (string.IsNullOrEmpty(Path.GetExtension(original))) original += Path.GetExtension(file);
                var dst = Path.Combine(dir, Fmt.SafeName(original));
                File.Copy(src, dst, overwrite: true);
                rollback.Special["wallpaper"] = reg.Read(@"HKCU\Control Panel\Desktop", "WallPaper").Value ?? "";
                reg.Write(@"HKCU\Control Panel\Desktop", "WallpaperStyle", new RegData { Type = "sz", Value = old.Extra.GetValueOrDefault("style") ?? "10" });
                reg.Write(@"HKCU\Control Panel\Desktop", "TileWallpaper", new RegData { Type = "sz", Value = old.Extra.GetValueOrDefault("tile") ?? "0" });
                return Native.SetWallpaper(dst);
            }

            case "languages":
            {
                if (old is null || !old.Extra.TryGetValue("list", out var list)) return false;
                var current = await Proc.PowerShellAsync(LanguageListScript, ct).ConfigureAwait(false);
                var now = string.Join(";", current.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Contains('|')));
                if (string.Equals(now, list, StringComparison.OrdinalIgnoreCase)) return false;
                rollback.Special["languages"] = now;
                var r = await Proc.PowerShellAsync(LanguageScript(list), ct).ConfigureAwait(false);
                if (!r.Ok) throw new InvalidOperationException(FirstLine(r.All));
                return true;
            }

            case "timezone":
            {
                if (old is null || !old.Extra.TryGetValue("id", out var id)) return false;
                if (TimeZoneInfo.Local.Id == id) return false;
                rollback.Special["timezone"] = TimeZoneInfo.Local.Id;
                var r = await Proc.RunAsync(Proc.System32("tzutil.exe"), $"/s \"{id}\"", ct).ConfigureAwait(false);
                if (!r.Ok) throw new InvalidOperationException(FirstLine(r.All));
                TimeZoneInfo.ClearCachedData();
                return true;
            }

            case "computername":
            {
                if (old is null || !old.Extra.TryGetValue("name", out var name)) return false;
                if (string.Equals(Environment.MachineName, name, StringComparison.OrdinalIgnoreCase)) return false;
                rollback.Special["computername"] = Environment.MachineName;
                var r = await Proc.PowerShellAsync($"Rename-Computer -NewName '{name.Replace("'", "''")}' -Force -ErrorAction Stop", ct).ConfigureAwait(false);
                if (!r.Ok) throw new InvalidOperationException(FirstLine(r.All));
                return true;
            }

            case "power":
            {
                var current = await ReadPowerAsync(ct).ConfigureAwait(false);
                string? guid = optionId switch
                {
                    "balanced" => StandardSchemes[0],
                    "high" => StandardSchemes[1],
                    "ultimate" => StandardSchemes[3],
                    _ => old?.Extra.GetValueOrDefault("guid"),
                };
                if (guid is null) return false;
                if (current?.Extra.GetValueOrDefault("guid") is { } cur && cur.Equals(guid, StringComparison.OrdinalIgnoreCase)) return false;
                if (current is not null) rollback.Special["power"] = current.Extra["guid"];

                var powercfg = Proc.System32("powercfg.exe");
                var set = await Proc.RunAsync(powercfg, $"/setactive {guid}", ct).ConfigureAwait(false);
                if (set.Ok) return true;

                // Схемы нет: своя схема старого ПК — импортируем из файла, стандартную — создаём из шаблона.
                if (optionId == SettingChoice.Old && old?.Extra.GetValueOrDefault("file") is { } powFile && packageRoot is not null)
                {
                    var path = Path.Combine(packageRoot, "Windows", powFile);
                    var newGuid = Guid.NewGuid().ToString();
                    var imp = await Proc.RunAsync(powercfg, $"/import \"{path}\" {newGuid}", ct).ConfigureAwait(false);
                    if (!imp.Ok) throw new InvalidOperationException(FirstLine(imp.All));
                    var act = await Proc.RunAsync(powercfg, $"/setactive {newGuid}", ct).ConfigureAwait(false);
                    return act.Ok;
                }
                var template = StandardSchemes.Contains(guid, StringComparer.OrdinalIgnoreCase) ? guid : StandardSchemes[1];
                var dup = await Proc.RunAsync(powercfg, $"/duplicatescheme {template}", ct).ConfigureAwait(false);
                var m = Regex.Match(dup.Output, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                if (!m.Success) throw new InvalidOperationException("Не удалось создать схему электропитания");
                var on = await Proc.RunAsync(powercfg, $"/setactive {m.Value}", ct).ConfigureAwait(false);
                return on.Ok;
            }
        }
        return false;
    }

    /// <summary>
    /// Языки и раскладки: «тег|раскладка,раскладка» на строку. Get-WinUserLanguageList отдаёт список одним
    /// объектом: и через конвейер, и через «foreach ($x in Get-…)» получался один «язык» из всех сразу.
    /// Перебирать нужно сам список, сохранённый в переменную.
    /// </summary>
    public const string LanguageListScript =
        "$languages = Get-WinUserLanguageList; foreach ($x in $languages) { $x.LanguageTag + '|' + ($x.InputMethodTips -join ',') }";

    /// <summary>Скрипт восстановления раскладок: те же языки и те же раскладки в том же порядке.</summary>
    public static string LanguageScript(string list)
    {
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference = 'Stop'; $l = $null; ");
        foreach (var entry in list.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('|');
            var tag = parts[0].Replace("'", "");
            var tips = parts.Length > 1 ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
            sb.Append($"if ($null -eq $l) {{ $l = New-WinUserLanguageList '{tag}' }} else {{ $l.Add('{tag}') }}; ");
            sb.Append($"$x = $l | Where-Object LanguageTag -eq '{tag}' | Select-Object -First 1; ");
            if (tips.Length > 0)
            {
                sb.Append("$x.InputMethodTips.Clear(); ");
                foreach (var t in tips) sb.Append($"$x.InputMethodTips.Add('{t.Replace("'", "")}'); ");
            }
        }
        sb.Append("Set-WinUserLanguageList $l -Force");
        return sb.ToString();
    }

    private void RunNotifications(HashSet<string> notify)
    {
        if (notify.Contains("theme")) Native.BroadcastSettingChange("ImmersiveColorSet");
        if (notify.Contains("env")) Native.BroadcastSettingChange("Environment");
        if (notify.Contains("intl")) Native.BroadcastSettingChange("intl");
        if (notify.Contains("mouse"))
        {
            int Get(string name, int def) =>
                int.TryParse(reg.Read(@"HKCU\Control Panel\Mouse", name).Value, out var v) ? v : def;
            int wheel = int.TryParse(reg.Read(@"HKCU\Control Panel\Desktop", "WheelScrollLines").Value, out var w) ? w : 3;
            Native.ApplyMouse(Get("MouseThreshold1", 6), Get("MouseThreshold2", 10), Get("MouseSpeed", 1),
                Get("MouseSensitivity", 10), wheel, Get("SwapMouseButtons", 0) == 1);
        }
        if (notify.Contains("keyboard"))
        {
            int delay = int.TryParse(reg.Read(@"HKCU\Control Panel\Keyboard", "KeyboardDelay").Value, out var dl) ? dl : 1;
            int speed = int.TryParse(reg.Read(@"HKCU\Control Panel\Keyboard", "KeyboardSpeed").Value, out var sp) ? sp : 31;
            Native.ApplyKeyboard(delay, speed);
        }
        if (notify.Contains("startmenu") && !notify.Contains("explorer")) Native.RestartStartMenu();
        if (notify.Contains("explorer"))
        {
            Native.RestartStartMenu();
            Native.RestartExplorer();
        }
    }

    // ───────────────────────────── Откат ─────────────────────────────

    private static void SaveRollback(string path, RollbackFile rollback)
    {
        if (rollback.Registry.Count == 0 && rollback.Special.Count == 0) return;
        try { Json.Write(path, rollback); } catch { }
    }

    public async Task<int> RollbackAsync(string path, CancellationToken ct = default)
    {
        var rb = Json.Read<RollbackFile>(path);
        int n = 0;
        // В обратном порядке: если значение меняли дважды, вернётся самое первое.
        for (int i = rb.Registry.Count - 1; i >= 0; i--)
        {
            var e = rb.Registry[i];
            try
            {
                reg.Write(e.Key, e.Name, e.Data);
                n++;
            }
            catch (Exception ex)
            {
                log.Warn($"Откат {e.Key}\\{e.Name}: {ex.Message}");
            }
        }
        if (rb.Special.TryGetValue("classicmenu", out var cm))
        {
            if (cm == "classic")
            {
                reg.EnsureKey(ClassicMenuKey + @"\InprocServer32");
                reg.Write(ClassicMenuKey + @"\InprocServer32", "", new RegData { Type = "sz", Value = "" });
            }
            else reg.DeleteKeyTree(ClassicMenuKey);
            n++;
        }
        if (!sandbox)
        {
            if (rb.Special.TryGetValue("timezone", out var tz))
                await Proc.RunAsync(Proc.System32("tzutil.exe"), $"/s \"{tz}\"", ct).ConfigureAwait(false);
            if (rb.Special.TryGetValue("power", out var pw))
                await Proc.RunAsync(Proc.System32("powercfg.exe"), $"/setactive {pw}", ct).ConfigureAwait(false);
            if (rb.Special.TryGetValue("languages", out var langs) && langs.Length > 0)
                await Proc.PowerShellAsync(LanguageScript(langs), ct).ConfigureAwait(false);
            if (rb.Special.TryGetValue("wallpaper", out var wp) && File.Exists(wp))
                Native.SetWallpaper(wp);
            Native.BroadcastSettingChange("ImmersiveColorSet");
            Native.RestartExplorer();
        }
        return n;
    }

    private static string ReplaceIgnoreCase(string s, string from, string to) =>
        from.Length == 0 ? s : Regex.Replace(s, Regex.Escape(from), to.Replace("$", "$$"), RegexOptions.IgnoreCase);

    private static string FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "ошибка";
}
