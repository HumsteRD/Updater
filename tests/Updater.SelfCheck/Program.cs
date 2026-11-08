using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Updater.Core;

Console.OutputEncoding = Encoding.UTF8;

// Настоящая сборка этого ПК, но только из несекретных мелочей — для проверки экранов распаковки.
if (Array.IndexOf(args, "--demo-package") is var di and >= 0 && di + 1 < args.Length)
{
    var target = Path.GetFullPath(args[di + 1]);
    var log = new Journal();
    var scanner = new Scanner(PathResolver.Live(), log);
    var items = new List<PlanItem>();
    await scanner.RunAsync(items.Add);
    string[] keep =
    [
        "windows.settings", "windows.drivers", "windows.fonts", "win.quickaccess", "win.taskbarpins", "win.startpins", "win.hosts",
        "app.obs", "app.soundpad", "app.afterburner", "app.qbittorrent", "app.everything", "app.7zip", "app.vscode", "app.fi",
        "app.velocity", "app.steam", "game.tarkov", "game.insomniac", "game.dota2cfg", "files.profile.root",
    ];
    // Дополнительные пункты через --include id1,id2 (например, files.profile.dev — много мелких файлов).
    if (Array.IndexOf(args, "--include") is var ii and >= 0 && ii + 1 < args.Length)
        keep = [.. keep, .. args[ii + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    // Скачать на флешку установщики через --installers id1,id2 (например, Valve.Steam,Microsoft.VCRedist.2015+.x86).
    var wantInstallers = Array.IndexOf(args, "--installers") is var ij and >= 0 && ij + 1 < args.Length
        ? args[ij + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
        : [];
    foreach (var i in items)
    {
        bool take = keep.Contains(i.Id) || (i.Section == Section.Games && i.Bytes < 50_000_000);
        if (i.Selectable) i.Selected = take && !i.Hidden;
        if (i.Kind == ItemKind.Drivers)
            foreach (var c in i.Children) c.Selected = c.Tag is DriverRecord d && d.Class.Equals("NET", StringComparison.OrdinalIgnoreCase) && Drivers.DefaultSelected(d);
        if (i.Kind == ItemKind.Installers && wantInstallers.Count > 0)
        {
            i.Selected = true;
            foreach (var c in i.Children) c.Selected = wantInstallers.Contains(c.Id);
        }
    }
    var state = new JobState();
    var m = await new Collector(PathResolver.Live(), log, state).RunAsync(items, scanner.Result,
        new CollectOptions { PackageRoot = target }, CancellationToken.None);
    log.Detach();
    foreach (var l in log.All) Console.WriteLine(l);
    Console.WriteLine($"Сборка: {target} — {m.Items.Count} пунктов, {Fmt.Size(m.TotalBytes)}, статус {m.Status}");
    return m.IsComplete ? 0 : 1;
}

var checks = new Checks(args.Contains("--live"));
await checks.RunAllAsync();
Console.WriteLine();
Console.WriteLine($"ИТОГО: пройдено {checks.Passed}, провалено {checks.Failed}");
return checks.Failed == 0 ? 0 : 1;

/// <summary>
/// Самопроверка Updater. Всё, что пишет, — во временной папке и в ветке реестра HKCU\Software\Updater.SelfCheck,
/// которая удаляется в конце. Настоящую систему только читает.
/// </summary>
sealed class Checks(bool live)
{
    public int Passed, Failed;

    void Check(bool ok, string name, string? detail = null)
    {
        if (ok) Passed++;
        else Failed++;
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(ok ? "  ✓ " : "  × ");
        Console.ResetColor();
        Console.WriteLine(name + (detail is null ? "" : $"  [{detail}]"));
    }

    static void Title(string t)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("── " + t);
        Console.ResetColor();
    }

    public async Task RunAllAsync()
    {
        Formatting();
        Globs();
        Paths();
        Catalogs();
        Index();
        Inventory();
        MainstreamPrograms();
        OnlyWhatWasThere();
        WingetCodes();
        await SelfUpdateAsync();
        await SettingsSandboxAsync();
        await EndToEndAsync();
        if (live) await LiveScanAsync();
        if (live) await LiveUpdateAsync();
    }

    // ───────────────────────────── Мелочи ─────────────────────────────

    void Formatting()
    {
        Title("ФОРМАТЫ");
        Check(Fmt.Size(0) == "0 Б", "0 байт");
        Check(Fmt.Size(1536) == "1,5 КБ", "1,5 КБ", Fmt.Size(1536));
        Check(Fmt.Size(5L * 1024 * 1024 * 1024) == "5 ГБ", "5 ГБ", Fmt.Size(5L * 1024 * 1024 * 1024));
        Check(Fmt.Files(1) == "1 файл" && Fmt.Files(3) == "3 файла" && Fmt.Files(11) == "11 файлов" && Fmt.Files(21) == "21 файл", "склонения", $"{Fmt.Files(1)}, {Fmt.Files(3)}, {Fmt.Files(11)}, {Fmt.Files(21)}");
        Check(Fmt.Duration(TimeSpan.FromMinutes(83)) == "1 ч 23 мин", "длительность", Fmt.Duration(TimeSpan.FromMinutes(83)));
        Check(Fmt.SafeName("a:b/c*?") == "a_b_c__", "имя файла", Fmt.SafeName("a:b/c*?"));
    }

    void Globs()
    {
        Title("МАСКИ ИСКЛЮЧЕНИЙ");
        var g = new GlobSet(["Cache", "*.tmp", "**/Service Worker/CacheStorage", "/????????????????????????????????", "tdata/user_data"]);
        Check(g.Matches(@"Default\Cache"), "имя на любой глубине");
        Check(g.Matches(@"x\y\file.TMP"), "маска расширения без учёта регистра");
        Check(g.Matches(@"Default\Service Worker\CacheStorage"), "путь с **");
        Check(!g.Matches(@"Default\Service Worker\Database"), "соседняя папка не исключается");
        Check(g.Matches("adcocjohghhfpidemphmcmlmhnfgikei"), "/маска — в корне");
        Check(!g.Matches(@"Default\Extensions\adcocjohghhfpidemphmcmlmhnfgikei"), "/маска — не глубже корня");
        Check(g.Matches(@"tdata\user_data") && !g.Matches(@"x\tdata\user_data"), "путь от корня");
    }

    void Paths()
    {
        Title("ПУТИ И МЕТКИ");
        var sb = PathResolver.Sandbox(@"C:\sb");
        Check(sb.Resolve(@"{AppData}\Telegram Desktop\tdata") == @"C:\sb\AppData\Telegram Desktop\tdata", "метка → путь (песочница)");
        Check(sb.Tokenize(@"C:\sb\LocalAppData\Google\Chrome") == @"{LocalAppData}\Google\Chrome", "путь → самая узкая метка");
        Check(sb.Tokenize(@"D:\Games") == @"D:\Games", "чужой диск остаётся абсолютным");
        var liveP = PathResolver.Live();
        var missing = PathResolver.Tokens.Where(t => string.IsNullOrEmpty(liveP.Get(t))).ToList();
        Check(missing.Count == 0, "все метки на этом ПК определяются", missing.Count == 0 ? liveP.Get("Downloads") : string.Join(",", missing));
        Check(liveP.Tokenize(Path.Combine(liveP.Get("LocalLow")!, "Redbeet Interactive")) == @"{LocalLow}\Redbeet Interactive", "LocalLow приоритетнее Profile");
    }

    void Catalogs()
    {
        Title("КАТАЛОГИ");
        var names = Json.EmbeddedNames().ToList();
        Check(names.Contains("apps.json") && names.Contains("games.json") && names.Contains("windows.json") && names.Contains("programs.json") && names.Contains("winget-index.tsv.gz"),
            "встроены все каталоги", string.Join(", ", names));

        var apps = AppCatalog.Shared;
        var games = GameCatalog.Shared;
        var all = apps.Apps.Concat(games.Games).ToList();
        Check(all.Select(a => a.Id).Distinct().Count() == all.Count, $"id программ и игр уникальны ({all.Count})");
        var sb = PathResolver.Sandbox(@"C:\sb");
        var badPaths = all.SelectMany(a => a.Roots).Where(r => !sb.TryResolve(r.Path, out _) || !r.Path.StartsWith('{')).Select(r => r.Path).ToList();
        Check(badPaths.Count == 0, "все пути каталога через известные метки", string.Join("; ", badPaths));
        string[] optionalIds = ["everything", "7zip", "sharex", "qbittorrent", "claudecode", "codex", "velocity"];
        string[] coreIds = ["chrome", "firefox", "telegram", "discord", "steam", "obs", "office", "fi", "ssh"];
        var wrongTier = optionalIds.Where(id => apps.Apps.First(a => a.Id == id) is { DefaultOff: false })
            .Concat(coreIds.Where(id => apps.Apps.First(a => a.Id == id) is { DefaultOff: true })).ToList();
        Check(wrongTier.Count == 0, "мелочи и инструменты разработчика — «по желанию», главное отмечено сразу", string.Join(", ", wrongTier));
        Check(!apps.Apps.Any(a => a.Group is "Свои программы"), "в каталоге нет личных групп");
        var badSets = all.SelectMany(a => a.Roots).Where(r => r.ExcludeSet is not null && !apps.ExcludeSets.ContainsKey(r.ExcludeSet)).Select(r => r.ExcludeSet).ToList();
        Check(badSets.Count == 0, "наборы исключений существуют", string.Join(",", badSets));
        Check(all.All(a => a.Roots.Count > 0 || a.Registry.Count > 0), "у каждого пункта есть что брать");

        var ws = WinSettingsCatalog.Shared.Settings;
        Check(ws.Select(s => s.Id).Distinct().Count() == ws.Count, $"id настроек Windows уникальны ({ws.Count})");
        var problems = new List<string>();
        foreach (var s in ws)
        {
            if (s.Kind is not ("choice" or "raw" or "special")) problems.Add($"{s.Id}: kind");
            if (s.IsSpecial && s.Handler is null) problems.Add($"{s.Id}: нет handler");
            if (!s.IsSpecial && s.Values.Count == 0) problems.Add($"{s.Id}: нет значений");
            foreach (var v in s.Values)
            {
                if (!v.Key.StartsWith(@"HKCU\") && !v.Key.StartsWith(@"HKLM\")) problems.Add($"{s.Id}: раздел {v.Key}");
                if (v.Type is not ("dword" or "qword" or "sz" or "expand" or "multi" or "binary")) problems.Add($"{s.Id}: тип {v.Type}");
                if (v.Key.StartsWith(@"HKLM\") && !s.Admin) problems.Add($"{s.Id}: HKLM без admin");
            }
            if (s.Kind == "choice")
            {
                if (s.Options.Count < 2) problems.Add($"{s.Id}: мало вариантов");
                foreach (var o in s.Options)
                    if (o.Data.Count != s.Values.Count) problems.Add($"{s.Id}/{o.Id}: данных {o.Data.Count}, значений {s.Values.Count}");
                if (s.Default is not null && s.Option(s.Default) is null) problems.Add($"{s.Id}: default {s.Default}");
                if (s.Recommended is not null && s.Option(s.Recommended) is null) problems.Add($"{s.Id}: recommended {s.Recommended}");
            }
        }
        Check(problems.Count == 0, "настройки Windows описаны без ошибок", string.Join("; ", problems));

        var pc = ProgramCatalog.Shared;
        var ids = pc.Popular.SelectMany(g => g.Items).Select(a => a.Id).Concat(pc.Runtimes.Items).Concat(pc.Rules.Where(r => r.Id is not null).Select(r => r.Id!)).ToList();
        var unknown = ids.Where(id => !WingetIndex.Shared.Contains(id)).ToList();
        Check(unknown.Count == 0, $"все {ids.Count} Id программ есть в индексе winget", string.Join(", ", unknown));
        Check(pc.Rules.All(r => { try { _ = r.Matches("x"); return true; } catch { return false; } }), "правила программ компилируются");

        var inst = InstallerCatalog.Shared.Installers;
        var instBad = inst.Where(i => !WingetIndex.Shared.Contains(i.Id) || !i.Url.StartsWith("https://") || i.Type is not ("exe" or "msi")).Select(i => i.Id).ToList();
        Check(inst.Count > 0 && instBad.Count == 0, $"установщики: {inst.Count} шт., все с winget-Id и https", string.Join(", ", instBad));
        Check(inst.All(i => i.FileName().EndsWith(".exe") || i.FileName().EndsWith(".msi")), "у установщиков есть имя файла");
    }

    void Index()
    {
        Title("ИНДЕКС WINGET");
        var sw = Stopwatch.StartNew();
        var idx = WingetIndex.Shared;
        Check(idx.Count > 10000, $"загружен: {idx.Count} пакетов за {sw.ElapsedMilliseconds} мс");
        (string Key, string Name, string? Pub, string? Ver, string? Want)[] cases =
        [
            ("7-Zip", "7-Zip 26.04 (x64)", "Igor Pavlov", "26.04", "7zip.7zip"),
            ("Steam", "Steam", "Valve Corporation", "2.10.91.91", "Valve.Steam"),
            ("Git_is1", "Git", "The Git Development Community", "2.56.0.4", "Git.Git"),
            ("{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1", "Microsoft Visual Studio Code (User)", "Microsoft Corporation", "1.143.1", "Microsoft.VisualStudioCode"),
            ("{5f75c346-3aaf-4f17-8545-2a21a740bb7a}", "Python 3.13.0 (64-bit)", "Python Software Foundation", "3.13.150.0", "Python.Python.3.13"),
            ("Google Chrome", "Google Chrome", "Google LLC", "156.0", "Google.Chrome"),
            ("qBittorrent", "qBittorrent", "The qBittorrent project", "5.2.4", "qBittorrent.qBittorrent"),
            ("Everything", "Everything 1.4.1.1032 (x86)", "voidtools", "1.4.1.1032", "voidtools.Everything"),
            ("Afterburner", "MSI Afterburner 4.6.6", "MSI Co., LTD", "4.6.6", "Guru3D.Afterburner"),
            ("Discord", "Discord", "Discord Inc.", "1.0.9267", "Discord.Discord"),
            ("BraveSoftware Brave-Browser", "Brave", "Авторы Brave", "157.1", "Brave.Brave"),
            ("{282cf187-a021-420e-8c80-5d74141ed8f6}", "Python 3.15.1 (64-bit)", "Python Software Foundation", "3.15.1150.0", null),
            ("TurboTop_is1", "TurboTop 2.8", "Savard Software", "2.8.0.21", "SavardSoftware.TurboTop"),
        ];
        foreach (var c in cases)
        {
            var got = idx.Match(c.Key, c.Name, c.Pub, c.Ver)?.Id;
            Check(string.Equals(got, c.Want, StringComparison.OrdinalIgnoreCase), $"{c.Name} → {c.Want ?? "вручную"}", got ?? "null");
        }
        Check(WingetIndex.NormalizeName("7-Zip 26.04 (x64)", out _) == "7zip", "нормализация имени", WingetIndex.NormalizeName("7-Zip 26.04 (x64)", out _));
        Check(WingetIndex.NormalizePublisher("Igor Pavlov") == "igorpavlov" && WingetIndex.NormalizePublisher("Valve Corporation") == "valve", "нормализация издателя");
    }

    void Inventory()
    {
        Title("ПРОГРАММЫ ЭТОГО ПК (только чтение)");
        var sw = Stopwatch.StartNew();
        var arp = ProgramInventory.ReadArp();
        var list = ProgramInventory.Build(arp, WingetIndex.Shared, PathResolver.Live(), ProgramCatalog.Shared);
        int auto = list.Count(p => p.WingetId is not null || p.Download is not null);
        Check(arp.Count > 0 && list.Count > 0, $"найдено {list.Count} программ, сами встанут {auto} ({sw.ElapsedMilliseconds} мс)");
        Console.WriteLine("     авто:    " + string.Join(", ", list.Where(p => p.WingetId is not null).Select(p => $"{p.Name}={p.WingetId}")));
        Console.WriteLine("     скачать: " + string.Join(", ", list.Where(p => p.Download is not null).Select(p => p.Name)));
        Console.WriteLine("     вручную: " + string.Join(", ", list.Where(p => p.WingetId is null && p.Download is null).Select(p => p.Name + (p.OffSystemDrive ? " [не C:]" : ""))));
        Check(!list.Any(p => p.Name.StartsWith("Dota 2") && p.Name.Length < 7), "игры Steam не попадают в программы");
        var steam = ProgramInventory.ReadSteamGames(PathResolver.Live());
        Console.WriteLine("     Steam:   " + string.Join(", ", steam.Select(g => $"{g.Name} ({Fmt.Size(g.Bytes)}, {g.Library})")));
        Check(true, $"игры Steam: {steam.Count}");

        var vdf = Vdf.Parse("\"libraryfolders\"\n{\n \"0\" { \"path\" \"C:\\\\Program Files (x86)\\\\Steam\" }\n \"1\" { \"path\" \"E:\\\\SteamLibrary\" }\n}");
        var folders = (Dictionary<string, object>)vdf["libraryfolders"];
        Check(((Dictionary<string, object>)folders["1"])["path"] as string == @"E:\SteamLibrary", "разбор VDF");
    }

    /// <summary>Популярные программы так, как они записаны в «Программах и компонентах» у обычных людей.</summary>
    void MainstreamPrograms()
    {
        Title("ПОПУЛЯРНЫЕ ПРОГРАММЫ");
        (string Key, string Name, string? Pub, string? Ver, string? Want, string? Url)[] cases =
        [
            ("Google Chrome", "Google Chrome", "Google LLC", "142.0.7444.60", "Google.Chrome", null),
            ("Mozilla Firefox 145.0 (x64 ru)", "Mozilla Firefox (x64 ru)", "Mozilla", "145.0", "Mozilla.Firefox.ru", null),
            ("Mozilla Firefox 145.0 (x64 en-US)", "Mozilla Firefox (x64 en-US)", "Mozilla", "145.0", "Mozilla.Firefox", null),
            ("YandexBrowser", "Yandex", "ООО «ЯНДЕКС»", "25.10.1.1173", "Yandex.Browser", null),
            ("Opera GX 123.0.5669.47", "Opera GX Stable 123.0.5669.47", "Opera Software", "123.0.5669.47", "Opera.OperaGX", null),
            ("Opera 123.0.5669.47", "Opera Stable 123.0.5669.47", "Opera Software", "123.0.5669.47", "Opera.Opera", null),
            ("BraveSoftware Brave-Browser", "Brave", "Brave Software Inc", "142.1.84.132", "Brave.Brave", null),
            ("{53F49750-6209-4FBF-9CA8-7A333C87D1ED}_is1", "Telegram Desktop", "Telegram FZ-LLC", "6.2.4", "Telegram.TelegramDesktop", null),
            ("Discord", "Discord", "Discord Inc.", "1.0.9212", "Discord.Discord", null),
            ("Viber", "Viber", "Viber Media S.à r.l.", "26.1.0.0", "Rakuten.Viber", null),
            ("ZoomUMX", "Zoom Workplace (64-bit)", "Zoom Communications, Inc.", "6.6.5", "Zoom.Zoom", null),
            ("Spotify", "Spotify", "Spotify AB", "1.2.75.510", "Spotify.Spotify", null),
            ("Яндекс Музыка", "Яндекс Музыка", "ООО «ЯНДЕКС»", "5.70.0", "Yandex.Music", null),
            ("VLC media player", "VLC media player", "VideoLAN", "3.0.21", "VideoLAN.VLC", null),
            ("WinRAR archiver", "WinRAR 7.13 (64-разрядная)", "win.rar GmbH", "7.13.0", "RARLab.WinRAR", null),
            ("Notepad++", "Notepad++ (64-bit x64)", "Notepad++ Team", "8.8.7", "Notepad++.Notepad++", null),
            ("OBS Studio", "OBS Studio", "OBS Project", "32.0.2", "OBSProject.OBSStudio", null),
            ("{A2AF8E8A-4C80-4F5B-BB9C-3E0C2A9B7D11}", "Adobe Acrobat (64-bit)", "Adobe", "25.001.20844", "Adobe.Acrobat.Reader.64-bit", null),
            ("Steam", "Steam", "Valve Corporation", "2.10.91.91", "Valve.Steam", null),
            ("{A2C7B1D0-0D5E-4F2C-9E34-5B7F1A6C2D11}", "Epic Games Launcher", "Epic Games, Inc.", "1.3.149.0", "EpicGames.EpicGamesLauncher", null),
            ("Battle.net", "Battle.net", "Blizzard Entertainment", "2.43.1", "Blizzard.BattleNet", null),
            ("Uplay", "Ubisoft Connect", "Ubisoft", "168.2", "Ubisoft.Connect", null),
            ("{7258BA11-600C-430E-A759-27E2C691A335}_is1", "GOG GALAXY", "GOG.com", "2.0.86", "GOG.Galaxy", null),
            ("{0F1B6F3A-6E2C-4D3C-8D77-1A6B3D0E6C55}", "EA app", "Electronic Arts", "13.590.0.6102", "ElectronicArts.EADesktop", null),
            ("AnyDesk", "AnyDesk", "AnyDesk Software GmbH", "9.6.2", "AnyDesk.AnyDesk", null),
            ("TeamViewer", "TeamViewer", "TeamViewer", "15.71.4", "TeamViewer.TeamViewer", null),
            ("O365HomePremRetail - ru-ru", "Microsoft 365 - ru-ru", "Microsoft Corporation", "16.0.19328.20190", null, "https://www.office.com/"),
            ("ProPlus2021Retail - ru-ru", "Microsoft Office профессиональный плюс 2021 - ru-ru", "Microsoft Corporation", "16.0.19328.20190", null, "https://www.office.com/"),
            ("uTorrent", "µTorrent", "BitTorrent Limited", "3.6.0.47206", null, "https://www.utorrent.com/"),
            ("Punto Switcher", "Punto Switcher 4.5.0", "Yandex", "4.5.0.583", null, "https://yandex.ru/soft/punto/"),
        ];
        var sb = PathResolver.Sandbox(@"C:\sb");
        foreach (var c in cases)
        {
            var list = ProgramInventory.Build([new(c.Key, c.Name, c.Pub, c.Ver, null, null, false)], WingetIndex.Shared, sb, ProgramCatalog.Shared);
            var p = list.SingleOrDefault();
            bool ok = p is not null && string.Equals(p.WingetId, c.Want, StringComparison.OrdinalIgnoreCase) && (c.Url is null || p.Url == c.Url);
            Check(ok, $"{c.Name} → {c.Want ?? c.Url}", p is null ? "пропущено" : $"{p.WingetId ?? "—"}, {p.Url ?? "—"}");
        }
        // Компоненты Office не должны стать отдельными строками.
        var parts = ProgramInventory.Build([new("x", "Microsoft Office Proofing Tools 2016 - Russian", "Microsoft Corporation", "16.0", null, null, false)],
            WingetIndex.Shared, sb, ProgramCatalog.Shared);
        Check(parts.Count == 0, "компоненты Office не попадают в список");
    }

    /// <summary>На новой винде ставится то, что было на старой: нет Steam — нет и Steam; библиотеки — только бывшие.</summary>
    void OnlyWhatWasThere()
    {
        Title("НА НОВОЙ ВИНДЕ — ТОЛЬКО ТО, ЧТО БЫЛО");
        var root = Path.Combine(Path.GetTempPath(), "updater-only-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var m = new PackageManifest();
            m.Programs.Add(new ProgramRecord { Name = "Google Chrome", WingetId = "Google.Chrome", Source = "winget" });
            m.Programs.Add(new ProgramRecord { Name = "Telegram Desktop", WingetId = "Telegram.TelegramDesktop", Source = "winget" });
            m.Programs.Add(new ProgramRecord { Name = "Microsoft Visual C++ 2015-2022 Redistributable (x64)", WingetId = "Microsoft.VCRedist.2015+.x64", IsRuntime = true });
            var plan = RestorePlan.Build(m, root, PathResolver.Sandbox(Path.Combine(root, "new")));
            var picked = RestorePlan.SelectedPrograms(plan).Select(c => c.Id).OrderBy(x => x).ToList();
            Check(picked.SequenceEqual(new[] { "Google.Chrome", "Microsoft.VCRedist.2015+.x64", "Telegram.TelegramDesktop" }),
                "ставятся Chrome, Telegram и одна бывшая библиотека", string.Join(", ", picked));
            var popular = plan.First(i => i.Id == RestorePlan.PopularId);
            Check(popular.Children.Any(c => c.Id == "Valve.Steam" && !c.Selected), "Steam не было — он только предлагается, не отмечен");
            Check(!popular.Children.Any(c => c.Id == "Google.Chrome"), "в «популярных» нет того, что и так встанет");
            var runtimes = plan.First(i => i.Id == RestorePlan.RuntimesId);
            Check(runtimes.Children.Count(c => c.Selected) == 1 && runtimes.Children.Any(c => !c.Selected && c.Chip == "по желанию"),
                "из набора библиотек отмечена только бывшая, остальные — по желанию", runtimes.SizeOverride);

            // Пустая старая винда — на новой ничего не ставится само.
            var empty = RestorePlan.Build(new PackageManifest(), root, PathResolver.Sandbox(Path.Combine(root, "new")));
            Check(RestorePlan.SelectedPrograms(empty).Count == 0, "без программ на старом ПК ничего не ставится без спроса");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    void WingetCodes()
    {
        Title("ОТВЕТЫ WINGET");
        Check(Winget.Interpret(0, "").Outcome == InstallOutcome.Installed, "0 → установлено");
        Check(Winget.Interpret(unchecked((int)0x8A15002B), "").Outcome == InstallOutcome.AlreadyInstalled, "0x8A15002B → уже стоит");
        Check(Winget.Interpret(unchecked((int)0x8A150061), "").Outcome == InstallOutcome.AlreadyInstalled, "0x8A150061 → уже стоит");
        Check(Winget.Interpret(unchecked((int)0x8A150056), "").Outcome == InstallOutcome.NeedsUserContext, "0x8A150056 → без прав администратора");
        Check(Winget.Interpret(unchecked((int)0x8A150109), "").Outcome == InstallOutcome.NeedsReboot, "0x8A150109 → после перезагрузки");
        Check(Winget.Interpret(unchecked((int)0x8A150014), "").Outcome == InstallOutcome.NotFound, "0x8A150014 → не найден");
        Check(Winget.CleanLine("  ██████████████▒▒▒▒▒▒  42.1 MB / 100 MB") == "42.1 MB / 100 MB", "строка прогресса без полоски", Winget.CleanLine("  ██████████████▒▒▒▒▒▒  42.1 MB / 100 MB"));
        Check(Winget.InstallArgs("Valve.Steam", null).Contains("--source winget") && Winget.InstallArgs("X", null).Contains("--accept-package-agreements"), "аргументы установки");

        // Архив зависимостей winget устроен как в релизе: папки x64, x86, arm64. Берём только x64-пакеты.
        var tmp = Path.Combine(Path.GetTempPath(), "updater-wgdeps-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var zipPath = Path.Combine(tmp, "deps.zip");
            using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                foreach (var n in new[] { "x64/Microsoft.VCLibs.140.00_14.0.33519.0_x64.appx", "x64/Microsoft.WindowsAppRuntime.1.8_8000.616.304.0_x64.appx",
                                          "x86/Microsoft.VCLibs.140.00_14.0.33519.0_x86.appx", "arm64/Microsoft.VCLibs.140.00_14.0.33519.0_arm64.appx", "x64/readme.txt" })
                {
                    using var s = z.CreateEntry(n).Open();
                    s.Write("PK"u8);
                }
            var got = Winget.ExtractX64Packages(zipPath, Path.Combine(tmp, "out")).Select(Path.GetFileName).OrderBy(x => x).ToList();
            Check(got.Count == 2 && got.All(n => n!.Contains("_x64.appx")), "из архива зависимостей winget берутся только пакеты x64", string.Join(", ", got));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // ───────────────────────────── Самообновление ─────────────────────────────

    async Task SelfUpdateAsync()
    {
        Title("САМООБНОВЛЕНИЕ");
        Check(SelfUpdater.ParseVersion("v1.2.3") == new Version(1, 2, 3) && SelfUpdater.ParseVersion("1.2") == new Version(1, 2, 0), "версия из тега");
        Check(SelfUpdater.ParseVersion("v1.2.3-beta") is null && SelfUpdater.ParseVersion("latest") is null && SelfUpdater.ParseVersion(null) is null, "тег-мусор и предвыпуск не версия");
        Check(SelfUpdater.IsNewer(new(1, 1, 0), new(1, 0, 0)) && !SelfUpdater.IsNewer(new(1, 0, 0), new(1, 0, 0)) && !SelfUpdater.IsNewer(new(1, 0, 0), new(1, 1, 0))
              && !SelfUpdater.IsNewer(new(1, 0, 0, 7), new(1, 0, 0)) && SelfUpdater.IsNewer(new(1, 10, 0), new(1, 9, 0)), "сравнение версий (1.10 новее 1.9)");
        Check(SelfUpdater.Current >= new Version(1, 0, 0), $"своя версия: {SelfUpdater.Current.ToString(3)}");

        const string P = SelfUpdater.DownloadPrefix;
        string Json(string tag = "v1.1.0", bool pre = false, bool draft = false, string exeUrl = P + "v1.1.0/Updater.exe", bool sum = true, long size = 62_000_000) =>
            $$"""
            {"tag_name":"{{tag}}","prerelease":{{(pre ? "true" : "false")}},"draft":{{(draft ? "true" : "false")}},
             "html_url":"https://github.com/HumsteRD/Updater/releases/tag/{{tag}}","body":"- Пункт\n## What's Changed",
             "assets":[{"name":"Updater.exe","size":{{size}},"browser_download_url":"{{exeUrl}}"}
                       {{(sum ? $",{{\"name\":\"Updater.exe.sha256\",\"size\":80,\"browser_download_url\":\"{P}{tag}/Updater.exe.sha256\"}}" : "")}}]}
            """;
        SelfUpdater.Release? R(string json) { using var d = System.Text.Json.JsonDocument.Parse(json); return SelfUpdater.Parse(d.RootElement); }
        var ok = R(Json());
        Check(ok is { Tag: "v1.1.0", ExeSize: 62_000_000 } && ok.Version == new Version(1, 1, 0) && ok.ExeUrl.StartsWith(P) && ok.ChecksumUrl.EndsWith(".sha256"),
            "выпуск с GitHub разобран", ok is null ? "null" : $"{ok.Version} {ok.ExeUrl}");
        Check(R(Json(exeUrl: "https://evil.example.com/Updater.exe")) is null, "чужая ссылка на exe отброшена");
        Check(R(Json(exeUrl: P + "v1.1.0/../../x/Updater.exe")) is null, "ссылка с «..» отброшена");
        Check(R(Json(exeUrl: "https://github.com/someone/Updater/releases/download/v1.1.0/Updater.exe")) is null, "exe из чужого репозитория отброшен");
        Check(R(Json(pre: true)) is null && R(Json(draft: true)) is null, "предвыпуск и черновик пропускаются");
        Check(R(Json(sum: false)) is null, "без контрольной суммы не обновляемся");
        Check(R(Json(size: 0)) is null && R(Json(tag: "nightly")) is null, "без размера или с непонятным тегом — нет");
        Check(UpdateSummary(), "«Что нового» — пункты до служебной части GitHub");

        const string Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Check(SelfUpdater.ParseChecksum(Hex + "  Updater.exe\n") == Hex.ToUpperInvariant() && SelfUpdater.ParseChecksum(Hex) is not null, "контрольная сумма читается");
        Check(SelfUpdater.ParseChecksum("xyz") is null && SelfUpdater.ParseChecksum(Hex[..63] + "g") is null && SelfUpdater.ParseChecksum(Hex + "ff") is null, "битая контрольная сумма — нет");

        var tmp = Path.Combine(Path.GetTempPath(), "updater-self-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var exe = Path.Combine(tmp, "Updater.exe");
            var dl = exe + SelfUpdater.DownloadSuffix;
            File.WriteAllText(exe, "old");
            File.WriteAllText(dl, "new");
            var sha = SelfUpdater.Sha256(dl);
            Check(SelfUpdater.VerifyChecksum(dl, sha) && !SelfUpdater.VerifyChecksum(exe, sha), "сверка SHA-256");
            Check(SelfUpdater.Verify(dl, 3, sha, new(1, 1, 0)) == "В скачанном файле не та программа или не та версия", "не-exe с верной суммой всё равно не принимается");
            Check(SelfUpdater.Verify(dl, 99, sha, new(1, 1, 0))!.StartsWith("Скачалось не целиком"), "недокачанный файл не принимается");
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Check(!SelfUpdater.VerifyBinary(cmd, new(1, 0, 0)), "чужой exe (не Updater) не принимается");
            // Самопроверка собрана с теми же сведениями о продукте, что и Updater.exe, — на ней видно сверку версии внутри файла.
            var self = Environment.ProcessPath!;
            var cur = SelfUpdater.Current;
            Check(SelfUpdater.VerifyBinary(self, cur) && !SelfUpdater.VerifyBinary(self, new(cur.Major, cur.Minor + 1, 0)),
                "версия внутри exe: своя принимается, старше заявленной — нет", Path.GetFileName(self));
            Check(SelfUpdater.CanWriteNextTo(exe) && Directory.GetFiles(tmp).Length == 2, "проверка записи не оставляет следов");

            SelfUpdater.Apply(exe, dl);
            Check(File.ReadAllText(exe) == "new" && File.ReadAllText(exe + SelfUpdater.OldSuffix) == "old" && !File.Exists(dl), "замена: новый на месте, прежний — .old");

            // Прежний .old занят (не закрылась старая копия) — замена всё равно проходит под другим именем.
            File.WriteAllText(dl, "newer");
            using (new FileStream(exe + SelfUpdater.OldSuffix, FileMode.Open, FileAccess.Read, FileShare.Read))
                SelfUpdater.Apply(exe, dl);
            Check(File.ReadAllText(exe) == "newer" && Directory.GetFiles(tmp, "Updater.exe.*.old").Length == 1, "занятый .old не мешает замене");

            // Нет скачанного файла — ничего не трогаем.
            bool threw = false;
            try { SelfUpdater.Apply(exe, dl); } catch (UpdateException) { threw = true; }
            Check(threw && File.ReadAllText(exe) == "newer", "без скачанного файла exe не тронут");

            // Второй шаг замены не удался (скачанный файл занят) — откат.
            File.WriteAllText(dl, "locked");
            threw = false;
            using (new FileStream(dl, FileMode.Open, FileAccess.Read, FileShare.None))
                try { SelfUpdater.Apply(exe, dl); } catch (UpdateException) { threw = true; }
            Check(threw && File.ReadAllText(exe) == "newer", "сбой замены — прежний exe вернулся на место");

            File.WriteAllText(dl + FileCopier.TempSuffix, "partial");
            await SelfUpdater.CleanupLeftoversAsync(exe, attempts: 2);
            var left = Directory.GetFiles(tmp).Select(Path.GetFileName).ToList();
            Check(left.SequenceEqual(["Updater.exe"]), "хвосты обновления убраны", string.Join(", ", left));
        }
        catch (Exception ex)
        {
            Check(false, "проверка замены упала", ex.Message);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    static bool UpdateSummary()
    {
        var s = SelfUpdater.SummarizeNotes("Что нового:\n- Пункт **один**\n* Пункт `два`\n- Ссылка https://x\n## What's Changed\n* fix by @a in https://github.com/x");
        return s == "•  Пункт один\n•  Пункт два";
    }

    async Task LiveUpdateAsync()
    {
        Title("ОБНОВЛЕНИЯ НА GITHUB (сеть)");
        var (r, err) = await SelfUpdater.GetLatestAsync();
        Check(r is not null && r.Version >= new Version(1, 0, 0), r is null ? "последний выпуск не получен" : $"последний выпуск: {r.Tag}, {Fmt.Size(r.ExeSize)}", err ?? r?.ExeUrl);
    }

    // ───────────────────────────── Настройки Windows в песочнице реестра ─────────────────────────────

    async Task SettingsSandboxAsync()
    {
        Title("НАСТРОЙКИ WINDOWS (песочница реестра)");
        var reg = new SandboxRegistry();
        reg.Wipe();
        var log = new Journal();
        try
        {
            var engine = new WindowsSettings(reg, log, sandbox: true);
            var cat = WinSettingsCatalog.Shared;
            var ext = cat.Find("explorer.extensions")!;
            var key = ext.Values[0];
            reg.Write(key.Key, key.Name, new RegData { Type = "dword", Value = "1" });
            Check(engine.ReadRegistry(ext).Option == "hide", "чтение: расширения скрыты");

            var tmp = Path.Combine(Path.GetTempPath(), $"updater-rb-{Guid.NewGuid():N}.json");
            var rep = await engine.ApplyAsync([new SettingChoice(ext, "show")], null, null, tmp);
            Check(rep.Applied == 1 && reg.Read(key.Key, key.Name).Value == "0", "запись: показать расширения");
            var rep2 = await engine.ApplyAsync([new SettingChoice(ext, "show")], null, null, tmp + "2");
            Check(rep2.Applied == 0 && rep2.Unchanged == 1, "повторная запись того же — без изменений");
            await engine.RollbackAsync(tmp);
            Check(reg.Read(key.Key, key.Name).Value == "1", "откат вернул прежнее значение");

            // Отсутствующее значение = вариант по умолчанию.
            var align = cat.Find("taskbar.align")!;
            Check(engine.ReadRegistry(align).Option == "center", "нет значения → по умолчанию (по центру)");

            // «Как было» для сырого значения.
            var speed = cat.Find("input.mouseSpeed")!;
            reg.Write(speed.Values[0].Key, speed.Values[0].Name, new RegData { Type = "sz", Value = "14" });
            var snap = new WindowsSnapshot { Settings = { [speed.Id] = engine.ReadRegistry(speed) } };
            Check(snap.Settings[speed.Id].Summary == "14 из 20", "сводка сырого значения", snap.Settings[speed.Id].Summary);
            reg.Write(speed.Values[0].Key, speed.Values[0].Name, new RegData { Type = "sz", Value = "10" });
            await engine.ApplyAsync([new SettingChoice(speed, SettingChoice.Old)], snap, null, tmp + "3");
            Check(reg.Read(speed.Values[0].Key, speed.Values[0].Name).Value == "14", "«как было» вернуло 14");

            // Несколько значений: ускорение мыши.
            var accel = cat.Find("input.mouseAccel")!;
            await engine.ApplyAsync([new SettingChoice(accel, "off")], null, null, tmp + "4");
            Check(engine.ReadRegistry(accel).Option == "off", "ускорение мыши выключено (3 значения)");

            // Удаление значения (null в данных).
            var web = cat.Find("start.webSearch")!;
            await engine.ApplyAsync([new SettingChoice(web, "off")], null, null, tmp + "5");
            Check(engine.ReadRegistry(web).Option == "off", "поиск без Bing");
            await engine.ApplyAsync([new SettingChoice(web, "on")], null, null, tmp + "6");
            Check(reg.Read(web.Values[0].Key, web.Values[0].Name).IsAbsent, "значение политики удалено");

            // Классическое меню: целая ветка.
            var menu = cat.Find("explorer.classicMenu")!;
            await engine.ApplyAsync([new SettingChoice(menu, "classic")], null, null, tmp + "7");
            Check(reg.KeyExists(WindowsSettings.ClassicMenuKey + @"\InprocServer32"), "классическое меню включено");
            await engine.RollbackAsync(tmp + "7");
            Check(!reg.KeyExists(WindowsSettings.ClassicMenuKey), "откат убрал ветку");

            // Переменные среды: PATH дополняется, профиль старого пользователя меняется на новый.
            var env = cat.Find("system.env")!;
            reg.Write(@"HKCU\Environment", "Path", new RegData { Type = "expand", Value = @"C:\Windows" });
            var oldEnv = new CapturedSetting { Option = "raw" };
            oldEnv.Extra["@profile"] = @"C:\Users\OldName";
            oldEnv.Extra["Path"] = @"C:\Users\OldName\bin;C:\Tools";
            oldEnv.Extra["@type:Path"] = "expand";
            oldEnv.Extra["MYVAR"] = "42";
            var envSnap = new WindowsSnapshot { Settings = { [env.Id] = oldEnv } };
            await engine.ApplyAsync([new SettingChoice(env, SettingChoice.Old)], envSnap, null, tmp + "8");
            var path = reg.Read(@"HKCU\Environment", "Path").Value ?? "";
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Check(path == $@"C:\Windows;{profile}\bin;C:\Tools", "PATH дополнен с новым профилем", path);
            Check(reg.Read(@"HKCU\Environment", "MYVAR").Value == "42", "своя переменная добавлена");

            Check(LanguageScriptOk(), "скрипт раскладок собирается");

            // Чтение раскладок этого ПК (только чтение): каждая строка — один язык с раскладками.
            var langs = await Proc.PowerShellAsync(WindowsSettings.LanguageListScript);
            var langLines = langs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Check(langs.Ok && langLines.Length > 0 && langLines.All(l => l.Contains('|') && l.IndexOf('|') > 0 && l.IndexOf('|') < l.Length - 1),
                "раскладки читаются по одной на строку", string.Join(" ; ", langLines));
            var bad = await Proc.PowerShellAsync("Write-Error 'проверка ошибки'; exit 1");
            Check(!bad.Ok && bad.All.Contains("проверка ошибки") && !bad.All.Contains("CLIXML"), "ошибки PowerShell приходят текстом", bad.All.Trim());

            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(tmp) + "*")) FileCopier.TryDelete(f);
        }
        finally
        {
            reg.Wipe();
        }
        Check(!new LiveRegistry().KeyExists(@"HKCU\Software\Updater.SelfCheck"), "ветка песочницы удалена");

        // Чтение настоящих настроек этого ПК — только чтение.
        var liveEngine = new WindowsSettings(new LiveRegistry(), new Journal());
        var errors = new List<string>();
        var summary = new List<string>();
        foreach (var s in WinSettingsCatalog.Shared.Settings.Where(s => !s.IsSpecial))
        {
            try { summary.Add($"{s.Title}: {liveEngine.ReadRegistry(s).Summary}"); }
            catch (Exception ex) { errors.Add($"{s.Id}: {ex.Message}"); }
        }
        Check(errors.Count == 0, $"все {summary.Count} настроек этого ПК читаются", string.Join("; ", errors));
        foreach (var s in summary.Take(60)) Console.WriteLine("     " + s);
    }

    static bool LanguageScriptOk()
    {
        var s = WindowsSettings.LanguageScript("ru-RU|0419:00000419;en-US|0409:00000409");
        return s.Contains("New-WinUserLanguageList 'ru-RU'") && s.Contains("$l.Add('en-US')") && s.Contains("0409:00000409") && s.EndsWith("Set-WinUserLanguageList $l -Force");
    }

    // ───────────────────────────── Сборы и распаковка целиком ─────────────────────────────

    async Task EndToEndAsync()
    {
        Title("СБОРЫ → РАСПАКОВКА (песочница)");
        var root = Path.Combine(Path.GetTempPath(), "updater-selfcheck-" + Guid.NewGuid().ToString("N")[..8]);
        var oldPc = PathResolver.Sandbox(Path.Combine(root, "old"));
        var newPc = PathResolver.Sandbox(Path.Combine(root, "new"));
        var pkg = Path.Combine(root, "usb", Package.FolderName);
        var reg = new SandboxRegistry(@"Software\Updater.SelfCheck.E2E");
        reg.Wipe();
        FileStream? locked = null;
        string? junctionPath = null;
        try
        {
            var rnd = new Random(42);
            byte[] Bytes(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
            string P(PathResolver r, string tokenPath) => r.Resolve(tokenPath);
            void Put(string path, byte[] data, DateTime? mtime = null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
                if (mtime is not null) File.SetLastWriteTimeUtc(path, mtime.Value);
            }
            var stamp = new DateTime(2025, 3, 14, 15, 9, 26, DateTimeKind.Utc);

            // Старая винда.
            Put(P(oldPc, @"{Desktop}\заметки.txt"), Encoding.UTF8.GetBytes("старая заметка"), stamp);
            Put(P(oldPc, @"{Desktop}\фото\отпуск.jpg"), Bytes(200_000), stamp);
            var longRel = string.Join("\\", Enumerable.Repeat("очень-длинная-папка-с-именем", 10)) + "\\файл.txt";
            Put(P(oldPc, @"{Desktop}\" + longRel), Encoding.UTF8.GetBytes("глубоко"), stamp);
            Put(P(oldPc, @"{Desktop}\~$черновик.docx"), Bytes(10));
            Put(P(oldPc, @"{Desktop}\Thumbs.db"), Bytes(10));
            Put(P(oldPc, @"{Desktop}\занят.txt"), Encoding.UTF8.GetBytes("занят"));
            Put(P(oldPc, @"{Documents}\big.bin"), Bytes(10_000_000), stamp);
            Put(P(oldPc, @"{Videos}\clip.mp4"), Bytes(5_000_000), stamp);
            Put(P(oldPc, @"{AppData}\Telegram Desktop\tdata\key_datas"), Bytes(1_000), stamp);
            Put(P(oldPc, @"{AppData}\Telegram Desktop\tdata\D877F783D5D3EF8C\maps"), Bytes(2_000), stamp);
            Put(P(oldPc, @"{AppData}\Telegram Desktop\tdata\user_data\cache\0\blob"), Bytes(50_000));
            Put(P(oldPc, @"{LocalAppData}\Google\Chrome\User Data\Local State"), Encoding.UTF8.GetBytes("{}"), stamp);
            Put(P(oldPc, @"{LocalAppData}\Google\Chrome\User Data\Default\Bookmarks"), Encoding.UTF8.GetBytes("{\"roots\":{}}"), stamp);
            Put(P(oldPc, @"{LocalAppData}\Google\Chrome\User Data\Default\Cache\Cache_Data\f_000001"), Bytes(30_000));
            Put(P(oldPc, @"{LocalAppData}\Google\Chrome\User Data\Default\Service Worker\CacheStorage\x"), Bytes(30_000));
            Put(P(oldPc, @"{LocalLow}\Redbeet Interactive\Raft\User\save1.rgd"), Bytes(4_000), stamp);
            Put(P(oldPc, @"{LocalLow}\Microsoft\CryptnetUrlCache\x"), Bytes(100));
            Put(P(oldPc, @"{LocalAppData}\FactoryGame\Saved\SaveGames\slot.sav"), Bytes(3_000), stamp);
            Put(P(oldPc, @"{LocalAppData}\FactoryGame\Saved\Logs\FactoryGame.log"), Bytes(3_000));
            Put(P(oldPc, @"{SavedGames}\CD Projekt Red\Cyberpunk 2077\save.dat"), Bytes(2_000), stamp);
            Put(P(oldPc, @"{AppData}\Microsoft\Templates\Normal.dotm"), Bytes(3_000), stamp);
            Put(P(oldPc, @"{AppData}\Microsoft\Templates\LiveContent\16\cache.bin"), Bytes(3_000));
            Put(P(oldPc, @"{AppData}\Everything\Everything.ini"), Encoding.UTF8.GetBytes("[Everything]"), stamp);
            Put(P(oldPc, @"{Profile}\.gitconfig"), Encoding.UTF8.GetBytes("[user]\n name = test"), stamp);
            Put(P(oldPc, @"{Profile}\.ssh\id_ed25519"), Encoding.UTF8.GetBytes("ключ"), stamp);
            Put(P(oldPc, @"{Profile}\notes.md"), Encoding.UTF8.GetBytes("# заметки"), stamp);
            Put(P(oldPc, @"{Profile}\NTUSER.DAT"), Bytes(100));
            Put(P(oldPc, @"{Profile}\dev\project\main.cs"), Encoding.UTF8.GetBytes("class A {}"), stamp);
            Put(P(oldPc, @"{SystemDrive}\AyuGram\AyuGram.exe"), Bytes(5_000), stamp);
            Put(P(oldPc, @"{SystemDrive}\AyuGram\tdata\key_datas"), Bytes(500), stamp);
            Put(P(oldPc, @"{SystemDrive}\AyuGram\tdata\user_data\big"), Bytes(100_000));
            Put(P(oldPc, @"{SystemDrive}\AyuGram\AyuGram.exe.WebView2\EBWebView\x"), Bytes(10_000));
            Put(P(oldPc, @"{SystemDrive}\Games\game.bin"), Bytes(20_000), stamp);
            Put(P(oldPc, @"{SystemDrive}\Windows\system.dll"), Bytes(100));
            Put(P(oldPc, @"{SystemDrive}\Program Files\app.exe"), Bytes(100));
            // Точка соединения внутри «Документов» — обход не должен по ней пойти.
            var junction = junctionPath = P(oldPc, @"{Documents}\Моя музыка ссылка");
            await Proc.RunAsync("cmd.exe", $"/c mklink /J \"{junction}\" \"{P(oldPc, "{Music}")}\"");
            Directory.CreateDirectory(P(oldPc, "{Music}"));
            Put(P(oldPc, @"{Music}\song.mp3"), Bytes(1_000), stamp);
            Check(Directory.Exists(junction), "точка соединения создана");

            // Сканирование.
            var log = new Journal();
            var scanner = new Scanner(oldPc, log);
            var found = new List<PlanItem>();
            await scanner.RunAsync(found.Add);
            string Ids() => string.Join(", ", found.Select(i => i.Id));
            bool Has(string id) => found.Any(i => i.Id == id);
            Check(Has("files.desktop") && Has("files.documents") && Has("files.videos"), "найдены папки пользователя");
            Check(Has("files.c.ayugram") && Has("files.c.games") && !Has("files.c.windows") && !Has("files.c.program files"), "папки на C: (без системных)");
            Check(Has("files.profile.dev") && Has("files.profile.root"), "папка «dev» и файлы профиля");
            Check(Has("app.telegram") && Has("app.chrome") && Has("app.git") && Has("app.ssh"), "настройки программ из каталога");
            var office = found.FirstOrDefault(i => i.Id == "app.office");
            Check(office is { Selected: true, Optional: false, GroupTitle: "Главное", Files: 1 }, "Office: шаблон Normal найден и отмечен, без кэша LiveContent",
                office is null ? "нет" : $"{office.Files} файлов, {office.GroupTitle}");
            var everything = found.FirstOrDefault(i => i.Id == "app.everything");
            Check(everything is { Selected: false, Optional: true, GroupTitle: "По желанию" }, "Everything найден, но не отмечен — «по желанию»");
            Check(Has("game.unity.redbeet interactive.raft"), "Raft (Unity, LocalLow)", Ids());
            Check(found.FirstOrDefault(i => i.Id == "game.unreal.factorygame")?.Title == "Satisfactory", "Satisfactory (Unreal, по имени)");
            Check(Has("game.savedgames.cd projekt red"), "«Сохранённые игры»");
            Check(!found.Any(i => i.Id.Contains("microsoft")), "служебные папки LocalLow пропущены");
            var ayu = found.First(i => i.Id == "files.c.ayugram");
            Check(ayu.Roots[0].Exclude.Contains("tdata/user_data") && ayu.Note is not null, "AyuGram на C: — без кэша медиа");
            var desktop = found.First(i => i.Id == "files.desktop");
            Check(desktop.Measured && desktop.Files == 4, "размер рабочего стола без мусора", $"{desktop.Files} файлов");
            var telegram = found.First(i => i.Id == "app.telegram");
            Check(telegram.Files == 2, "Telegram без кэша", $"{telegram.Files} файлов");
            var chrome = found.First(i => i.Id == "app.chrome");
            Check(chrome.Files == 2, "Chrome без кэша", $"{chrome.Files} файлов");
            var docs = found.First(i => i.Id == "files.documents");
            Check(docs.Files == 1, "обход не пошёл по точке соединения", $"{docs.Files} файлов");

            // Сборы: FAT32 имитируем нарезкой по 3 МБ, exe — подставной.
            var fakeExe = Path.Combine(root, "fake-app.exe");
            Put(fakeExe, Bytes(1234));
            locked = new FileStream(P(oldPc, @"{Desktop}\занят.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var state = new JobState();
            var collector = new Collector(oldPc, log, state);
            var opt = new CollectOptions { PackageRoot = pkg, SplitSize = 3_000_000, SelfExe = fakeExe, Registry = reg };
            var sw = Stopwatch.StartNew();
            var manifest = await collector.RunAsync(found, scanner.Result, opt, CancellationToken.None);
            log.Detach();
            Check(manifest.IsComplete, $"сборы закончены ({sw.ElapsedMilliseconds} мс, {Fmt.Size(state.Done)})");
            Check(File.Exists(Path.Combine(pkg, Package.ExeName)) && new FileInfo(Path.Combine(pkg, Package.ExeName)).Length == 1234, "программа скопировала себя");
            Check(File.Exists(Path.Combine(pkg, Package.ReadmeName)) && File.Exists(Path.Combine(pkg, Package.ReportName)) && File.Exists(Path.Combine(pkg, Package.JournalName)), "инструкция, отчёт и журнал");
            Check(File.Exists(Path.Combine(pkg, @"Файлы\Рабочий стол\заметки.txt")), "файлы лежат как есть");
            Check(File.Exists(Path.Combine(pkg, @"Файлы\Рабочий стол\" + longRel)), "длинный путь (> 260 символов)");
            Check(!File.Exists(Path.Combine(pkg, @"Файлы\Рабочий стол\Thumbs.db")) && !File.Exists(Path.Combine(pkg, @"Файлы\Рабочий стол\~$черновик.docx")), "мусор не взят");
            var parts = Directory.GetFiles(Path.Combine(pkg, @"Файлы\Документы"), "big.bin.__part*");
            Check(parts.Length == 4 && parts.Sum(p => new FileInfo(p).Length) == 10_000_000, "большой файл разрезан на 4 части", $"{parts.Length} частей");
            var desk = manifest.Items.First(i => i.Id == "files.desktop");
            Check(desk.FailedCount == 1 && desk.Failed[0].Contains("занят"), "занятый файл записан в проблемы", string.Join("; ", desk.Failed));
            var tgZip = Path.Combine(pkg, manifest.Items.First(i => i.Id == "app.telegram").Stored);
            using (var z = ZipFile.OpenRead(tgZip))
                Check(z.Entries.Any(e => e.FullName == "0/key_datas") && !z.Entries.Any(e => e.FullName.Contains("user_data")), "zip Telegram: вход есть, кэша нет");
            Check(manifest.Windows is not null && manifest.Windows.Settings.Count > 10, "настройки Windows запомнены", manifest.Windows?.Settings.Count.ToString());
            Check(Path.GetFileName(pkg) == "Updater" && File.Exists(Path.Combine(pkg, "updater.json")) && File.Exists(Path.Combine(pkg, "Updater.exe")),
                "сборка называется Updater: папка, опись и exe");
            var legacyRoot = Path.Combine(root, "legacy-usb");
            var legacyPkg = Path.Combine(legacyRoot, "Переезд");
            Directory.CreateDirectory(legacyPkg);
            File.Copy(Path.Combine(pkg, PackageManifest.FileName), Path.Combine(legacyPkg, PackageManifest.LegacyFileName));
            Check(Package.Find(legacyRoot) == legacyPkg && Package.TryLoad(legacyPkg) is not null, "старая сборка («Переезд», pereezd.json) открывается");

            // Докачка: ничего не перезаписывается.
            var probe = Path.Combine(pkg, @"Файлы\Видео\clip.mp4");
            var before = File.GetLastWriteTimeUtc(probe);
            var probeCreated = File.GetCreationTimeUtc(probe);
            var log2 = new Journal();
            var state2 = new JobState();
            var manifest2 = await new Collector(oldPc, log2, state2).RunAsync(found, scanner.Result, opt, CancellationToken.None);
            log2.Detach();
            Check(manifest2.Items.Count == manifest.Items.Count, "повторные сборы — та же опись");
            Check(File.GetCreationTimeUtc(probe) == probeCreated && File.GetLastWriteTimeUtc(probe) == before, "повторные сборы не копируют заново");
            locked.Dispose();
            locked = null;

            // Распаковка на «новой винде» с другим профилем.
            Put(P(newPc, @"{Desktop}\заметки.txt"), Encoding.UTF8.GetBytes("новая, свежее"), DateTime.UtcNow);
            var loaded = Package.TryLoad(pkg)!;
            loaded.Programs.Add(new ProgramRecord { Name = "Игра из папки на C:", InstallLocation = P(oldPc, @"{SystemDrive}\Games") + "\\" });
            loaded.Programs.Add(new ProgramRecord { Name = "Программа без winget", InstallLocation = @"C:\Program Files\Nope" });
            var plan = RestorePlan.Build(loaded, pkg, newPc);
            Check(plan.Any(i => i.Id == RestorePlan.ProgramsId || i.Id == RestorePlan.RuntimesId), "в плане есть программы и библиотеки");
            var progs = plan.First(i => i.Id == RestorePlan.ProgramsId).Children;
            Check(progs.Any(c => c.Title == "Игра из папки на C:" && c.Chip == "вернётся с папкой" && c.Link is null)
                  && progs.Any(c => c.Title == "Программа без winget" && c.Link is not null),
                "портативное из папки на C: вернётся с папкой, остальное — ссылкой");

            // Установщик на флешке: программа становится «с флешки» и ставится без winget.
            Directory.CreateDirectory(Package.Abs(pkg, Package.InstallersDir));
            File.WriteAllText(Package.Abs(pkg, Package.InstallersDir + "/Тест.exe"), "fake");
            loaded.Programs.Add(new ProgramRecord { Name = "Прога с флешки", WingetId = "Nope.OfflineApp", LocalInstaller = Package.InstallersDir + "/Тест.exe", SilentArgs = "/S" });
            var plan2 = RestorePlan.Build(loaded, pkg, newPc);
            var flashChild = plan2.First(i => i.Id == RestorePlan.ProgramsId).Children.FirstOrDefault(c => c.Title == "Прога с флешки");
            Check(flashChild is { Chip: "с флешки", Selectable: true, Selected: true }, "установщик с флешки: программа ставится без winget",
                flashChild is null ? "нет в плане" : $"{flashChild.Chip}, selectable={flashChild.Selectable}");
            var restorer = new Restorer(newPc, new Journal());
            var ropt = new RestoreOptions { PackageRoot = pkg, Registry = reg, WorkDir = Path.Combine(root, "work") };
            sw.Restart();
            await restorer.RunAsync(plan, [], ropt, CancellationToken.None);
            Check(restorer.Summary.FilesFailed == 0, $"распаковка без ошибок ({sw.ElapsedMilliseconds} мс, {restorer.Summary.FilesRestored} файлов)", string.Join("; ", restorer.Summary.Problems));

            string Text(PathResolver r, string tp) => File.ReadAllText(r.Resolve(tp));
            string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            Check(Text(newPc, @"{Desktop}\заметки.txt") == "новая, свежее", "более новый файл на новой винде не затёрт");
            Check(Hash(P(newPc, @"{Desktop}\фото\отпуск.jpg")) == Hash(P(oldPc, @"{Desktop}\фото\отпуск.jpg")), "фото совпадает побайтно");
            Check(Text(newPc, @"{Desktop}\" + longRel) == "глубоко", "длинный путь восстановлен");
            Check(Hash(P(newPc, @"{Documents}\big.bin")) == Hash(P(oldPc, @"{Documents}\big.bin")), "части склеены в исходный файл");
            Check(Math.Abs((File.GetLastWriteTimeUtc(P(newPc, @"{Documents}\big.bin")) - stamp).TotalSeconds) <= 2, "дата изменения сохранена");
            Check(File.Exists(P(newPc, @"{AppData}\Telegram Desktop\tdata\key_datas")) && !Directory.Exists(P(newPc, @"{AppData}\Telegram Desktop\tdata\user_data")), "Telegram: вход на месте, кэша нет");
            Check(File.Exists(P(newPc, @"{LocalAppData}\Google\Chrome\User Data\Default\Bookmarks")) && !Directory.Exists(P(newPc, @"{LocalAppData}\Google\Chrome\User Data\Default\Cache")), "Chrome: закладки на месте");
            Check(File.Exists(P(newPc, @"{LocalLow}\Redbeet Interactive\Raft\User\save1.rgd")), "сохранение Raft на месте");
            Check(File.Exists(P(newPc, @"{LocalAppData}\FactoryGame\Saved\SaveGames\slot.sav")) && !Directory.Exists(P(newPc, @"{LocalAppData}\FactoryGame\Saved\Logs")), "Satisfactory: сейвы без логов");
            Check(File.Exists(P(newPc, @"{SavedGames}\CD Projekt Red\Cyberpunk 2077\save.dat")), "«Сохранённые игры» на месте");
            Check(Text(newPc, @"{Profile}\.gitconfig").Contains("name = test") && File.Exists(P(newPc, @"{Profile}\.ssh\id_ed25519")), "Git и SSH");
            Check(File.Exists(P(newPc, @"{Profile}\notes.md")) && !File.Exists(P(newPc, @"{Profile}\NTUSER.DAT")), "файлы профиля без NTUSER.DAT");
            Check(File.Exists(P(newPc, @"{Profile}\dev\project\main.cs")), "папка «dev»");
            Check(File.Exists(P(newPc, @"{SystemDrive}\AyuGram\tdata\key_datas")) && !Directory.Exists(P(newPc, @"{SystemDrive}\AyuGram\AyuGram.exe.WebView2")), "AyuGram вернулся на C: без кэша");
            Check(!File.Exists(P(newPc, @"{SystemDrive}\Windows\system.dll")), "системные папки не тронуты");

            // Подложенный путь в архиве не выходит за свою папку.
            var evilZip = Path.Combine(root, "evil.zip");
            using (var z = ZipFile.Open(evilZip, ZipArchiveMode.Create))
            {
                using var s = z.CreateEntry("0/../../evil.txt").Open();
                s.Write("x"u8);
            }
            var target = Path.Combine(root, "zipslip", "inside");
            var (res, _) = await ZipPacker.UnpackAsync(evilZip, [(new StoredRoot { Entry = "0" }, target)], Path.Combine(root, "work"), new JobState(), CancellationToken.None);
            Check(!File.Exists(Path.Combine(root, "zipslip", "evil.txt")) && !File.Exists(Path.Combine(root, "evil.txt")) && res.Failed.Count == 1, "защита от «../» в архиве");

            // Отчёт.
            var html = Report.Html(loaded, pkg);
            Check(html.Contains("<html") && html.Contains("Рабочий стол") && html.Contains("Что делать на новой винде"), "отчёт собирается");
        }
        catch (Exception ex)
        {
            Check(false, "сквозная проверка упала", ex.ToString());
        }
        string? cleanup = null;
        try
        {
            locked?.Dispose();
            reg.Wipe();
            // Точку соединения убираем отдельно: рекурсивное удаление в некоторых средах на ней спотыкается.
            if (junctionPath is not null && Directory.Exists(junctionPath)) Directory.Delete(junctionPath);
            Directory.Delete(root, true);
        }
        catch (Exception ex)
        {
            cleanup = ex.GetType().Name + ": " + ex.Message;
        }
        Check(!Directory.Exists(root), "временные файлы удалены", cleanup);
    }

    // ───────────────────────────── Живой скан (только чтение) ─────────────────────────────

    async Task LiveScanAsync()
    {
        Title("ЖИВОЙ СКАН ЭТОГО ПК (только чтение)");
        var sw = Stopwatch.StartNew();
        var scanner = new Scanner(PathResolver.Live(), new Journal());
        var items = new List<PlanItem>();
        string? lastStage = null;
        await scanner.RunAsync(items.Add, s => { if (s.Length > 0 && s != lastStage) Console.WriteLine($"     … {s} ({sw.Elapsed.TotalSeconds:0.0} с)"); lastStage = s; });
        Check(items.Count > 0, $"скан за {sw.Elapsed.TotalSeconds:0.0} с: {items.Count} пунктов");
        foreach (var g in items.GroupBy(i => i.Section))
        {
            Console.WriteLine($"   [{Report.SectionTitle(g.Key)}]");
            foreach (var i in g.Where(i => !i.Hidden))
                Console.WriteLine($"     {(i.Selectable ? (i.Selected ? "[x]" : "[ ]") : "   ")} {i.Title,-44} {i.SizeText,10}  {(i.Warning is null ? "" : "‹" + i.Warning + "› ")}{i.Detail}");
            var hidden = g.Where(i => i.Hidden).Select(i => i.Title).ToList();
            if (hidden.Count > 0) Console.WriteLine("     скрыты пустые: " + string.Join(", ", hidden));
        }
        var sel = items.Where(i => i.Selected && i.IsData).Sum(i => i.Bytes);
        Console.WriteLine($"   Отмечено по умолчанию: {Fmt.Size(sel)}; облачных файлов пропущено: {scanner.Result.Stats.CloudFiles}");
        var drivers = scanner.Result.Drivers;
        Console.WriteLine($"   Драйверы ({drivers.Count}): " + string.Join("; ", drivers.Select(d => $"{(Drivers.DefaultSelected(d) ? "[x]" : "[ ]")} {d.Device} [{d.Class}, {d.Package}, {Fmt.Size(d.Bytes)}]")));
        Check(drivers.All(d => d.Package.Length > 0), "у каждого драйвера найден пакет");
    }
}
