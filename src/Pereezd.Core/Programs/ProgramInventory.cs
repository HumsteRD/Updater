using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pereezd.Core;

/// <summary>Что установлено: программы из «Программ и компонентов», приложения Store, игры Steam.</summary>
public static class ProgramInventory
{
    public sealed record ArpEntry(string Key, string Name, string? Publisher, string? Version, string? InstallLocation,
        string? Url, bool PerUser);

    /// <summary>
    /// То, что ставить заново не нужно или нельзя: обновления, драйверы (ими занимается отдельный раздел),
    /// античиты (их ставят сами игры), компоненты, которые приходят вместе с Windows или с другой программой.
    /// </summary>
    private static readonly Regex Ignore = new(
        @"anti-?cheat|easyanticheat|battleye|elytra|^microsoft edge|webview2|update health|pc health check|" +
        @"python launcher|^nvidia (graphics driver|графический драйвер|hd audio|аудиодрайвер|frameview|usbc|virtual audio|ngx|optimus|update)|" +
        @"^realtek|^intel\(r\)|^amd (software|chipset)|click-to-run|windows software development kit|" +
        @"^vs_|visual studio installer|^microsoft update|^windows driver package|^security update|^update for|" +
        @"^hotfix|^kb\d{6}|^microsoft (gameinput|windows desktop targeting|asp\.net core targeting)|" +
        @"^dotnet|ms-resource:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RuntimeRx = new(
        @"visual c\+\+.*redistributable|\.net (desktop )?runtime|windows desktop runtime|directx|physx|" +
        @"^microsoft \.net|xna framework|openal|vulkan run ?time|java\(tm\)|java \d+ update",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ArpEntry> ReadArp()
    {
        var list = new List<ArpEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(RegistryKey hive, string path, bool perUser)
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root is null) return;
                foreach (var keyName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(keyName);
                        if (k is null) continue;
                        var name = (k.GetValue("DisplayName") as string)?.Trim();
                        if (string.IsNullOrEmpty(name)) continue;
                        if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (k.GetValue("ParentKeyName") is string) continue;
                        if (k.GetValue("ReleaseType") is string rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;
                        var version = (k.GetValue("DisplayVersion") as string)?.Trim();
                        if (!seen.Add(name + "|" + version)) continue;
                        var url = (k.GetValue("URLInfoAbout") as string) ?? (k.GetValue("HelpLink") as string) ?? (k.GetValue("URLUpdateInfo") as string);
                        if (url is not null && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = null;
                        list.Add(new ArpEntry(keyName, name, (k.GetValue("Publisher") as string)?.Trim(), version,
                            (k.GetValue("InstallLocation") as string)?.Trim().Trim('"'), url, perUser));
                    }
                    catch { }
                }
            }
            catch { }
        }
        Read(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", false);
        Read(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", false);
        Read(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", true);
        return list;
    }

    public static bool IsSteamGame(ArpEntry e) => e.Key.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Список программ для сборки: с пакетом winget, где он нашёлся.</summary>
    public static List<ProgramRecord> Build(IEnumerable<ArpEntry> arp, WingetIndex index, PathResolver paths,
        ProgramCatalog? catalog = null, IReadOnlyDictionary<string, string>? wingetExport = null)
    {
        var result = new List<ProgramRecord>();
        foreach (var e in arp)
        {
            if (IsSteamGame(e) || Ignore.IsMatch(e.Name)) continue;

            string? id = null;
            // 1. Ручные правила каталога — для того, что индекс понимает неверно.
            var rule = catalog?.Rules.FirstOrDefault(r => r.Matches(e.Name));
            if (rule is not null)
            {
                if (rule.Skip) continue;
                id = rule.Id;
            }
            // 2. Экспорт winget со старого ПК (если winget там был) — самый точный источник.
            if (id is null && wingetExport is not null && wingetExport.TryGetValue(e.Name, out var exported)) id = exported;
            // 3. Встроенный индекс.
            id ??= index.Match(e.Key, e.Name, e.Publisher, e.Version)?.Id;

            bool off = !string.IsNullOrEmpty(e.InstallLocation) && Path.IsPathRooted(e.InstallLocation)
                       && !paths.OnSystemDrive(e.InstallLocation);
            result.Add(new ProgramRecord
            {
                Name = rule?.Name ?? CleanName(e.Name),
                Publisher = e.Publisher,
                Version = e.Version,
                WingetId = id,
                Source = id is not null ? "winget" : rule?.Download is not null ? "download" : null,
                Url = rule?.Url ?? e.Url,
                Download = rule?.Download,
                Note = rule?.Note,
                InstallLocation = e.InstallLocation,
                OffSystemDrive = off,
                IsRuntime = RuntimeRx.IsMatch(e.Name),
            });
        }

        // Несколько записей на один пакет (x64 и x86 одной библиотеки разными строками — нормально, но
        // одинаковые Id дважды ставить незачем).
        return result
            .GroupBy(p => p.WingetId ?? ("\0" + p.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.IsRuntime)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>«7-Zip 26.04 (x64)» → «7-Zip»: версия в названии только мешает читать список.</summary>
    public static string CleanName(string name)
    {
        var s = Regex.Replace(name, @"\s+v?\d+(\.\d+)+[\w\-\(\)]*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*\((x64|x86|64-bit|32-bit)\)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+version$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-');
        return s.Length > 0 ? s : name;
    }

    // ───────────────────────────── Приложения Store ─────────────────────────────

    public static async Task<List<StoreAppRecord>> ReadStoreAppsAsync(CancellationToken ct = default)
    {
        const string script =
            "Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.SignatureKind -eq 'Store' -and -not $_.NonRemovable -and -not $_.IsResourcePackage } | " +
            "Select-Object Name, PackageFamilyName | ConvertTo-Json -Compress";
        var r = await Proc.PowerShellAsync(script, ct).ConfigureAwait(false);
        var list = new List<StoreAppRecord>();
        if (!r.Ok || string.IsNullOrWhiteSpace(r.Output)) return list;
        try
        {
            using var doc = JsonDocument.Parse(r.Output.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];
            foreach (var it in items)
            {
                var name = it.GetProperty("Name").GetString() ?? "";
                var pfn = it.GetProperty("PackageFamilyName").GetString() ?? "";
                if (IsInboxApp(name)) continue;
                list.Add(new StoreAppRecord { Name = PrettyAppxName(name), FamilyName = pfn });
            }
        }
        catch { }
        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static readonly string[] InboxPrefixes =
        ["Microsoft.", "MicrosoftWindows.", "MicrosoftCorporationII.", "Windows.", "MSTeams", "Clipchamp.", "NVIDIACorp.", "RealtekSemiconductorCorp.", "AppUp.", "AdvancedMicroDevicesInc", "DolbyLaboratories.", "Disney."];

    private static readonly string[] InboxAllow = ["Microsoft.MinecraftUWP", "Microsoft.MinecraftJavaEdition", "Microsoft.PowerToys"];

    private static bool IsInboxApp(string name) =>
        !InboxAllow.Contains(name, StringComparer.OrdinalIgnoreCase) &&
        InboxPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>«OpenAI.Codex» → «Codex», «5319275A.WhatsAppDesktop» → «WhatsApp Desktop».</summary>
    public static string PrettyAppxName(string name)
    {
        var last = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        last = Regex.Replace(last, @"(?<=[a-z])(?=[A-Z])", " ");
        return last.Length > 0 ? last : name;
    }

    // ───────────────────────────── Steam ─────────────────────────────

    public static List<SteamGameRecord> ReadSteamGames(PathResolver paths)
    {
        var games = new List<SteamGameRecord>();
        var steam = paths.Get("Steam");
        if (steam is null || !Directory.Exists(steam)) return games;
        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                var root = Vdf.Parse(File.ReadAllText(vdf));
                if (root.TryGetValue("libraryfolders", out var lf) && lf is Dictionary<string, object> folders)
                    foreach (var (_, v) in folders)
                        if (v is Dictionary<string, object> f && f.TryGetValue("path", out var p) && p is string ps)
                            libs.Add(ps.Replace(@"\\", @"\"));
            }
            catch { }
        }
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var doc = Vdf.Parse(File.ReadAllText(acf));
                    if (!doc.TryGetValue("AppState", out var st) || st is not Dictionary<string, object> s) continue;
                    var id = int.TryParse(s.GetValueOrDefault("appid") as string, out var a) ? a : 0;
                    var name = s.GetValueOrDefault("name") as string ?? $"App {id}";
                    long.TryParse(s.GetValueOrDefault("SizeOnDisk") as string, out var size);
                    if (id == 228980) continue; // Steamworks Common Redistributables
                    if (games.Any(g => g.AppId == id)) continue;
                    games.Add(new SteamGameRecord { AppId = id, Name = name, Library = lib, Bytes = size });
                }
                catch { }
            }
        }
        return games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

/// <summary>Минимальный разбор формата KeyValues (VDF) Valve.</summary>
public static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        int i = 0;
        return ParseObject(text, ref i, topLevel: true);
    }

    private static Dictionary<string, object> ParseObject(string t, ref int i, bool topLevel)
    {
        var obj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) return obj;
            if (t[i] == '}')
            {
                i++;
                return obj;
            }
            var key = ReadString(t, ref i);
            if (key is null) return obj;
            SkipWs(t, ref i);
            if (i < t.Length && t[i] == '{')
            {
                i++;
                obj[key] = ParseObject(t, ref i, false);
            }
            else
            {
                var val = ReadString(t, ref i) ?? "";
                obj[key] = val;
            }
        }
    }

    private static void SkipWs(string t, ref int i)
    {
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i])) i++;
            else if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '/')
            {
                while (i < t.Length && t[i] != '\n') i++;
            }
            else break;
        }
    }

    private static string? ReadString(string t, ref int i)
    {
        SkipWs(t, ref i);
        if (i >= t.Length) return null;
        if (t[i] == '"')
        {
            i++;
            var sb = new System.Text.StringBuilder();
            while (i < t.Length && t[i] != '"')
            {
                if (t[i] == '\\' && i + 1 < t.Length)
                {
                    sb.Append(t[i + 1] switch { 'n' => '\n', 't' => '\t', _ => t[i + 1] });
                    i += 2;
                    continue;
                }
                sb.Append(t[i++]);
            }
            i++;
            return sb.ToString();
        }
        int start = i;
        while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != '{' && t[i] != '}') i++;
        return t[start..i];
    }
}
