using System.Collections.Concurrent;

namespace Pereezd.Core;

/// <summary>Что нашлось на старом ПК, кроме строк плана.</summary>
public sealed class ScanResult
{
    public List<PlanItem> Items { get; } = [];
    public List<ProgramRecord> Programs { get; set; } = [];
    public List<StoreAppRecord> StoreApps { get; set; } = [];
    public List<SteamGameRecord> SteamGames { get; set; } = [];
    public List<DriverRecord> Drivers { get; set; } = [];
    public List<Fonts.FoundFont> Fonts { get; set; } = [];
    public List<string> WifiProfiles { get; set; } = [];
    public List<BitLockerVolume> BitLocker { get; set; } = [];
    public List<string> Reminders { get; } = [];
    public string? OemKey { get; set; }
    public WalkStats Stats { get; } = new();
}

/// <summary>
/// Обход старой винды: сначала быстро находит, что вообще есть (строки появляются сразу),
/// потом в несколько потоков считает размеры.
/// </summary>
public sealed class Scanner(PathResolver paths, Journal log)
{
    public const long BigFolder = 15L * 1024 * 1024 * 1024;

    /// <summary>Папки в корне системного диска, которые принадлежат Windows или пересоздаются сами.</summary>
    private static readonly HashSet<string> SystemRootDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users", "PerfLogs", "Recovery",
        "System Volume Information", "$Recycle.Bin", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "$SysReset",
        "$GetCurrent", "$AV_ASW", "Config.Msi", "Documents and Settings", "MSOCache", "OneDriveTemp", "Windows.old",
        "ESD", "Intel", "AMD", "NVIDIA", "inetpub", "PerfLogs", "Boot", "EFI", "$Extend", "found.000", "$SXS", "SWSetup",
    };

    /// <summary>Папки в профиле, которые принадлежат Windows (их содержимое берётся отдельно или не нужно).</summary>
    private static readonly HashSet<string> ProfileSystemDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "Saved Games", "Contacts",
        "Favorites", "Links", "Searches", "3D Objects", "MicrosoftEdgeBackups", "IntelGraphicsProfiles",
        "Application Data", "Cookies", "Local Settings", "NetHood", "PrintHood", "Recent", "SendTo", "Start Menu",
        "Templates", "My Documents", "главное меню", "Мои документы", "Шаблоны", "Рабочий стол", "Документы",
        "Загрузки", "Изображения", "Видео", "Музыка", "Сохранённые игры", "Ссылки", "Избранное", "Контакты", "Поиски",
    };

    /// <summary>Скрытые папки профиля — кэши инструментов, которые скачиваются заново.</summary>
    private static readonly HashSet<string> ProfileCaches = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cache", ".nuget", ".dotnet", ".templateengine", ".npm", ".gradle", ".m2", ".cargo", ".rustup",
        ".vscode-shared", ".android", ".pyenv", ".node-gyp", ".yarn", ".pnpm-store", ".ms-ad", ".azure", ".dbus-keyrings",
        ".lmstudio", ".conda", ".ipython", ".matplotlib", ".keras", ".gologin", ".insomniac",
    };

    private static readonly string[] TelegramPortableExcludes =
        ["tdata/user_data", "tdata/user_data#*", "tdata/emoji", "tdata/dumps", "tdata/temp", "*.WebView2", "log.txt"];

    private static readonly string[] UnrealExcludes =
        ["Logs", "Crashes", "webcache*", "PersistentDownloadDir", "*.upipelinecache", "*.dmp", "D3DCache", "ShaderCache*", "CrashReportClient"];

    public ScanResult Result { get; } = new();

    public async Task RunAsync(Action<PlanItem> onItem, Action<string>? onStage = null, CancellationToken ct = default)
    {
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(PlanItem item)
        {
            // Один и тот же путь двумя строками (например, игра и в каталоге, и найденная эвристикой) не нужен.
            var key = item.Roots.Count > 0 ? string.Join("|", item.Roots.Select(r => SafeResolve(r.Path))) : item.Id;
            if (item.IsData && !added.Add(key)) return;
            Result.Items.Add(item);
            onItem(item);
        }

        onStage?.Invoke("Папки пользователя");
        foreach (var i in UserFolders()) Add(i);
        foreach (var i in ProfileExtras()) Add(i);
        foreach (var i in SystemDriveFolders()) Add(i);

        onStage?.Invoke("Настройки программ");
        foreach (var i in CatalogApps(AppCatalog.Shared.Apps, AppCatalog.Shared)) Add(i);

        onStage?.Invoke("Сохранения игр");
        foreach (var i in CatalogApps(GameCatalog.Shared.Games.Select(g => { g.Section ??= "games"; return g; }), AppCatalog.Shared)) Add(i);
        foreach (var i in GameSaves()) Add(i);

        onStage?.Invoke("Программы");
        await ScanProgramsAsync(Add, ct).ConfigureAwait(false);

        onStage?.Invoke("Настройки Windows");
        await ScanWindowsAsync(Add, ct).ConfigureAwait(false);

        onStage?.Invoke("Перед переустановкой");
        await ScanImportantAsync(Add, ct).ConfigureAwait(false);

        onStage?.Invoke("Считаю размеры");
        await MeasureAsync(Result.Items.Where(i => i.IsData && !i.Measured).ToList(), ct).ConfigureAwait(false);
        onStage?.Invoke("");
    }

    private string SafeResolve(string tokenPath) => paths.TryResolve(tokenPath, out var p) ? p : tokenPath;

    // ───────────────────────────── Файлы ─────────────────────────────

    private IEnumerable<PlanItem> UserFolders()
    {
        (string Token, string Title, string Glyph)[] known =
        [
            ("Desktop", "Рабочий стол", ""),
            ("Documents", "Документы", ""),
            ("Downloads", "Загрузки", ""),
            ("Pictures", "Изображения", ""),
            ("Videos", "Видео", ""),
            ("Music", "Музыка", ""),
        ];
        foreach (var (token, title, glyph) in known)
        {
            var path = paths.Get(token);
            if (path is null || !Directory.Exists(path)) continue;
            var item = new PlanItem
            {
                Id = "files." + token.ToLowerInvariant(),
                Section = Section.Files,
                Kind = ItemKind.Folder,
                Title = title,
                Glyph = glyph,
                Roots = [new SourceRoot("{" + token + "}", ["My Music", "My Pictures", "My Videos", "Моя музыка", "Мои рисунки", "Мои видеозаписи"])],
                Selected = true,
            };
            item.Detail = path;
            if (!paths.IsSandbox && !paths.OnSystemDrive(path))
            {
                item.Selected = false;
                item.Note = $"Лежит на диске {Path.GetPathRoot(path)?.TrimEnd('\\')} — переустановка его не тронет. Бери, только если этот диск тоже будешь форматировать.";
            }
            else if (path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            {
                item.Note = "Папка в OneDrive: файлы, которые лежат только в облаке, не копирую — они и так сохранены.";
            }
            yield return item;
        }
    }

    private IEnumerable<PlanItem> ProfileExtras()
    {
        var profile = paths.Get("Profile")!;
        if (!Directory.Exists(profile)) yield break;

        var knownFolders = PathResolver.Tokens.Select(t => paths.Get(t)).Where(p => p is not null)
            .Select(p => p!.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalogRoots = AppCatalog.Shared.Apps.SelectMany(a => a.Roots).Select(r => SafeResolve(r.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        DirectoryInfo[] dirs;
        try { dirs = new DirectoryInfo(profile).GetDirectories(); }
        catch { yield break; }

        foreach (var d in dirs.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (ProfileSystemDirs.Contains(d.Name) || knownFolders.Contains(d.FullName)) continue;
            if (d.Name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) continue;
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (d.Attributes.HasFlag(FileAttributes.Hidden) && d.Attributes.HasFlag(FileAttributes.System)) continue;
            if (ProfileCaches.Contains(d.Name)) continue;
            // Папки, которые целиком забирает каталог программ (.ssh, .claude…), второй раз не показываем.
            if (catalogRoots.Any(r => r.StartsWith(d.FullName, StringComparison.OrdinalIgnoreCase))) continue;

            bool dot = d.Name.StartsWith('.');
            var item = new PlanItem
            {
                Id = "files.profile." + d.Name.ToLowerInvariant(),
                Section = Section.Files,
                Kind = ItemKind.Folder,
                Title = dot ? $"Скрытая папка {d.Name}" : $"Папка «{d.Name}»",
                Glyph = "",
                Roots = [new SourceRoot("{Profile}\\" + d.Name)],
                Selected = !dot,
            };
            item.Detail = d.FullName;
            if (dot) item.Note = "Чьи-то настройки в папке пользователя. Бери, если знаешь, что это.";
            if (d.Name.Equals("go", StringComparison.OrdinalIgnoreCase) && IsOnlyGoCache(d))
            {
                item.Selected = false;
                item.Note = "Кэш модулей Go — он скачается заново.";
            }
            yield return item;
        }

        // Отдельные файлы прямо в папке пользователя (.gitconfig, свои скрипты). Файлы реестра профиля не трогаем.
        var loose = new PlanItem
        {
            Id = "files.profile.root",
            Section = Section.Files,
            Kind = ItemKind.Folder,
            Title = "Файлы в папке пользователя",
            Glyph = "",
            Roots = [new SourceRoot("{Profile}", ["NTUSER*", "ntuser*", "*.blf", "*.regtrans-ms", "*.TM", "desktop.ini", ".claude.json*", ".gitconfig", ".git-credentials", "Sti_Trace.log"], topOnly: true)],
            Selected = true,
        };
        loose.Detail = profile;
        var (b, n) = FileWalker.Measure(profile, new GlobSet(loose.Roots[0].Exclude), topOnly: true);
        if (n > 0)
        {
            loose.Bytes = b;
            loose.Files = n;
            loose.Measured = true;
            yield return loose;
        }
    }

    private static bool IsOnlyGoCache(DirectoryInfo d)
    {
        try { return d.GetDirectories().All(x => x.Name is "pkg" or "bin") && d.GetFiles().Length == 0; }
        catch { return false; }
    }

    private IEnumerable<PlanItem> SystemDriveFolders()
    {
        var root = paths.SystemDrive;
        if (!Directory.Exists(root)) yield break;
        DirectoryInfo[] dirs;
        try { dirs = new DirectoryInfo(root).GetDirectories(); }
        catch { yield break; }

        foreach (var d in dirs.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (SystemRootDirs.Contains(d.Name)) continue;
            if (d.Name.StartsWith('$')) continue;
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (d.Attributes.HasFlag(FileAttributes.Hidden) && d.Attributes.HasFlag(FileAttributes.System)) continue;

            var excludes = new List<string>();
            string? note = null;
            if (Directory.Exists(Path.Combine(d.FullName, "tdata")))
            {
                excludes.AddRange(TelegramPortableExcludes);
                note = "Похоже на Telegram или AyuGram: вход в аккаунт переедет, кэш картинок и видео не беру.";
            }
            var item = new PlanItem
            {
                Id = "files.c." + d.Name.ToLowerInvariant(),
                Section = Section.Files,
                Kind = ItemKind.Folder,
                Title = d.Name,
                Glyph = "",
                Roots = [new SourceRoot("{SystemDrive}\\" + d.Name, excludes)],
                Selected = true,
                Note = note,
            };
            item.Detail = d.FullName;
            item.Warning = "Удалится при переустановке";
            yield return item;
        }
    }

    // ───────────────────────────── Программы и игры из каталога ─────────────────────────────

    private IEnumerable<PlanItem> CatalogApps(IEnumerable<AppDef> defs, AppCatalog catalog)
    {
        var reg = new LiveRegistry();
        foreach (var a in defs)
        {
            var roots = new List<SourceRoot>();
            foreach (var r in a.Roots)
            {
                if (!paths.TryResolve(r.Path, out var full)) continue;
                if (File.Exists(full) || Directory.Exists(full)) roots.Add(new SourceRoot(r.Path, catalog.ExcludesFor(r)));
            }
            var regKeys = paths.IsSandbox ? [] : a.Registry.Where(k => SafeKeyExists(reg, k)).ToArray();
            if (roots.Count == 0 && regKeys.Length == 0) continue;

            var item = new PlanItem
            {
                Id = (a.SectionValue == Section.Games && !a.Id.StartsWith("game.") ? "game." : a.SectionValue == Section.Windows ? "win." : "app.") + a.Id,
                Section = a.SectionValue,
                Kind = ItemKind.Archive,
                Title = a.Title,
                Glyph = a.SectionValue == Section.Games ? "" : a.Glyph,
                Note = a.Note,
                Roots = roots,
                Registry = regKeys,
                Processes = [.. a.Processes],
                Sensitive = a.Sensitive,
                WingetId = a.WingetId,
                After = a.After,
                PasswordsUrl = a.PasswordsUrl,
                BrowserExe = a.Exe,
                Selected = !a.DefaultOff,
                Optional = a.DefaultOff,
                Category = a.Group,
            };
            item.Detail = roots.Count > 0 ? SafeResolve(roots[0].Path) : string.Join(", ", regKeys);
            if (a.Sensitive) item.Warning = "Секреты";
            if (a.Reminder is not null) Result.Reminders.Add(a.Reminder);
            if (roots.Count == 0)
            {
                item.Measured = true;
                item.SizeOverride = "реестр";
            }
            yield return item;
        }
    }

    private static bool SafeKeyExists(IRegistryAccess reg, string key)
    {
        try { return reg.KeyExists(key); }
        catch { return false; }
    }

    /// <summary>Сохранения, которые находятся без каталога: по устройству популярных движков.</summary>
    private IEnumerable<PlanItem> GameSaves()
    {
        var games = GameCatalog.Shared;

        // «Сохранённые игры» и «Документы\My Games»: каждая папка — игра.
        foreach (var (token, sub, where) in new[] { ("SavedGames", "", "Сохранённые игры"), ("Documents", "My Games", "Документы\\My Games") })
        {
            var basePath = paths.Get(token);
            if (basePath is null) continue;
            var dir = sub.Length > 0 ? Path.Combine(basePath, sub) : basePath;
            foreach (var d in SafeDirs(dir))
                yield return GameItem($"game.{token.ToLowerInvariant()}.{d.Name}", d.Name, $"{{{token}}}\\{(sub.Length > 0 ? sub + "\\" : "")}{d.Name}", where, []);
        }

        // Unity: AppData\LocalLow\<студия>\<игра>.
        var localLow = paths.Get("LocalLow");
        if (localLow is not null)
            foreach (var company in SafeDirs(localLow))
            {
                if (games.LocalLowSkip.Contains(company.Name, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var game in SafeDirs(company.FullName))
                {
                    if (game.Name.Equals("Unity", StringComparison.OrdinalIgnoreCase)) continue;
                    yield return GameItem($"game.unity.{company.Name}.{game.Name}", game.Name,
                        $"{{LocalLow}}\\{company.Name}\\{game.Name}", company.Name, ["Unity", "*.log", "Player*.log", "Crashes", "ShaderCache", "Analytics"]);
                }
            }

        // Unreal Engine: AppData\Local\<игра>\Saved с SaveGames или настройками.
        var local = paths.Get("LocalAppData");
        if (local is not null)
            foreach (var d in SafeDirs(local))
            {
                if (games.UnrealSkip.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) continue;
                var saved = Path.Combine(d.FullName, "Saved");
                if (!Directory.Exists(saved)) continue;
                bool hasSaves = Directory.Exists(Path.Combine(saved, "SaveGames"));
                bool hasConfig = Directory.Exists(Path.Combine(saved, "Config"));
                if (!hasSaves && !hasConfig) continue;
                var title = games.UnrealNames.TryGetValue(d.Name, out var nice) ? nice : d.Name;
                var item = GameItem($"game.unreal.{d.Name}", title, $"{{LocalAppData}}\\{d.Name}\\Saved",
                    hasSaves ? "Unreal Engine" : "Unreal Engine, только настройки", UnrealExcludes);
                if (!hasSaves) item.Note = "Только настройки графики и управления — прогресс хранится на серверах игры.";
                yield return item;
            }
    }

    private PlanItem GameItem(string id, string title, string tokenPath, string where, string[] exclude)
    {
        var item = new PlanItem
        {
            Id = id.ToLowerInvariant(),
            Section = Section.Games,
            Kind = ItemKind.Archive,
            Title = title,
            Glyph = "",
            Roots = [new SourceRoot(tokenPath, exclude)],
            Selected = true,
        };
        item.Detail = where;
        return item;
    }

    private static IEnumerable<DirectoryInfo> SafeDirs(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        try
        {
            return new DirectoryInfo(dir).GetDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }

    // ───────────────────────────── Программы ─────────────────────────────

    private async Task ScanProgramsAsync(Action<PlanItem> add, CancellationToken ct)
    {
        if (paths.IsSandbox) return;
        try
        {
            var arp = ProgramInventory.ReadArp();
            Result.Programs = ProgramInventory.Build(arp, WingetIndex.Shared, paths, ProgramCatalog.Shared);
        }
        catch (Exception ex)
        {
            log.Warn("Список программ не прочитался: " + ex.Message);
        }

        var auto = Result.Programs.Count(p => p.WingetId is not null || p.Download is not null);
        var item = new PlanItem
        {
            Id = "programs.list",
            Section = Section.Programs,
            Kind = ItemKind.Programs,
            Title = "Установленные программы",
            Glyph = "",
            Selected = true,
            Selectable = false,
            Note = $"Список запишется в сборку. На новой винде {auto} из {Result.Programs.Count} поставятся сами через winget — одной кнопкой, " +
                   "для остальных будут ссылки.",
        };
        item.SizeOverride = Fmt.Count(Result.Programs.Count, "программа", "программы", "программ");
        foreach (var p in Result.Programs)
        {
            string chip = p.WingetId is not null || p.Download is not null ? "сама" : p.OffSystemDrive ? "на другом диске" : "вручную";
            item.Children.Add(new SubItem
            {
                Id = p.WingetId ?? p.Name,
                Title = p.Name,
                Detail = p.IsRuntime ? "библиотека" : p.Publisher,
                Chip = chip,
                ChipIsGood = chip == "сама",
                Selected = true,
                Selectable = false,
                Tag = p,
            });
        }
        add(item);

        AddInstallers(add);

        try { Result.StoreApps = await ProgramInventory.ReadStoreAppsAsync(ct).ConfigureAwait(false); }
        catch { }
        if (Result.StoreApps.Count > 0)
        {
            var store = new PlanItem
            {
                Id = "programs.store",
                Section = Section.Programs,
                Kind = ItemKind.Note,
                Title = "Приложения из Microsoft Store",
                Glyph = "",
                Selectable = false,
                Note = "На новой винде открою их страницы в Store — останется нажать «Получить».",
            };
            store.SizeOverride = Fmt.Count(Result.StoreApps.Count, "приложение", "приложения", "приложений");
            foreach (var a in Result.StoreApps)
                store.Children.Add(new SubItem { Id = a.FamilyName, Title = a.Name, Detail = a.FamilyName, Selectable = false });
            add(store);
        }

        Result.SteamGames = ProgramInventory.ReadSteamGames(paths);
        var onSystem = Result.SteamGames.Where(g => paths.OnSystemDrive(g.Library)).ToList();
        if (onSystem.Count > 0)
        {
            long total = onSystem.Sum(g => g.Bytes);
            var steam = new PlanItem
            {
                Id = "programs.steamOnC",
                Section = Section.Programs,
                Kind = ItemKind.Note,
                Title = "Игры Steam на диске C:",
                Glyph = "",
                Selectable = false,
                Note = $"После переустановки Steam скачает их заново — {Fmt.Size(total)}. Чтобы не качать, до переустановки " +
                       "перенеси их на другой диск: Steam → Библиотека → игра → Свойства → Установленные файлы → Переместить.",
            };
            steam.Warning = "Скачаются заново";
            steam.Measured = true;
            steam.Bytes = total;
            foreach (var g in onSystem)
                steam.Children.Add(new SubItem { Id = g.AppId.ToString(), Title = g.Name, Bytes = g.Bytes, Selectable = false });
            add(steam);
        }
    }

    /// <summary>
    /// Отдельный пункт: скачать официальные установщики на флешку. Полезно, когда на новой винде ещё нет
    /// интернета (не встали сетевые драйверы) или нет winget. По умолчанию выключено — это большие загрузки.
    /// </summary>
    private void AddInstallers(Action<PlanItem> add)
    {
        var installed = new HashSet<string>(
            Result.Programs.Where(p => p.WingetId is not null).Select(p => p.WingetId!), StringComparer.OrdinalIgnoreCase);

        var item = new PlanItem
        {
            Id = "programs.installers",
            Section = Section.Programs,
            Kind = ItemKind.Installers,
            Title = "Установщики на флешку",
            Glyph = "",
            Selected = false,
            Note = "Скачаю официальные установщики на флешку, чтобы поставить программы на новой винде даже без " +
                   "интернета (пока не встали сетевые драйверы) или без winget. Нужен интернет сейчас; загрузки большие.",
        };

        foreach (var def in InstallerCatalog.Shared.Installers)
        {
            bool has = installed.Contains(def.Id);
            // Установленные программы и «первым делом» (браузер, Steam, библиотеки) — остальное незачем.
            if (!has && !def.Bootstrap) continue;
            item.Children.Add(new SubItem
            {
                Id = def.Id,
                Title = def.Name,
                Detail = has ? "установлена" : "на всякий случай",
                Chip = def.Type == "msi" ? "MSI" : null,
                Selected = has,
                Tag = def,
            });
        }
        if (item.Children.Count == 0) return;
        item.SizeOverride = Fmt.Count(item.Children.Count(c => c.Selected), "установщик", "установщика", "установщиков");
        add(item);
    }

    // ───────────────────────────── Windows ─────────────────────────────

    private async Task ScanWindowsAsync(Action<PlanItem> add, CancellationToken ct)
    {
        var settingsCount = WinSettingsCatalog.Shared.Settings.Count(s => s.AppliesTo(Native.WindowsBuild));
        var ws = new PlanItem
        {
            Id = "windows.settings",
            Section = Section.Windows,
            Kind = ItemKind.WindowsSettings,
            Title = "Настройки Windows",
            Glyph = "",
            Selected = true,
            Note = "Тема и цвет, обои, панель задач, Проводник, мышь, раскладки, часовой пояс, электропитание. " +
                   "На новой винде покажу каждую настройку и спрошу, что вернуть.",
        };
        ws.SizeOverride = Fmt.Count(settingsCount, "настройка", "настройки", "настроек");
        add(ws);

        if (paths.IsSandbox) return;

        try { Result.WifiProfiles = await Wifi.ProfilesAsync(ct).ConfigureAwait(false); }
        catch { }
        if (Result.WifiProfiles.Count > 0)
        {
            var wifi = new PlanItem
            {
                Id = "windows.wifi",
                Section = Section.Windows,
                Kind = ItemKind.Wifi,
                Title = "Сети Wi-Fi с паролями",
                Glyph = "",
                Selected = true,
                Sensitive = true,
                Note = "Компьютер сам подключится к знакомым сетям. Пароли лягут в сборку открытым текстом — флешку никому не давай.",
            };
            wifi.SizeOverride = Fmt.Count(Result.WifiProfiles.Count, "сеть", "сети", "сетей");
            foreach (var p in Result.WifiProfiles) wifi.Children.Add(new SubItem { Id = p, Title = p, Selectable = false });
            add(wifi);
        }

        try { Result.Drivers = await Task.Run(Drivers.List, ct).ConfigureAwait(false); }
        catch { }
        if (Result.Drivers.Count > 0)
        {
            var drv = new PlanItem
            {
                Id = "windows.drivers",
                Section = Section.Windows,
                Kind = ItemKind.Drivers,
                Title = "Драйверы",
                Glyph = "",
                Selected = true,
                Note = "Чтобы на новой винде сразу работали сеть, звук и периферия — даже без интернета. " +
                       "Драйвер видеокарты лучше скачать свежий, поэтому он не отмечен.",
            };
            foreach (var d in Result.Drivers)
            {
                var sub = new SubItem
                {
                    Id = d.Inf,
                    Title = string.IsNullOrEmpty(d.Device) ? d.Inf : d.Device,
                    Detail = $"{Drivers.ClassName(d.Class)} · {d.Provider} · {d.Version}",
                    Bytes = d.Bytes,
                    Selected = Drivers.DefaultSelected(d),
                    Tag = d,
                };
                sub.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SubItem.Selected)) UpdateDriverSize(drv);
                };
                drv.Children.Add(sub);
            }
            UpdateDriverSize(drv);
            add(drv);
        }

        try { Result.Fonts = Fonts.FindUserInstalled(); }
        catch { }
        if (Result.Fonts.Count > 0)
        {
            var fonts = new PlanItem
            {
                Id = "windows.fonts",
                Section = Section.Windows,
                Kind = ItemKind.Fonts,
                Title = "Установленные шрифты",
                Glyph = "",
                Selected = true,
                Note = "Шрифты, которые ты ставил сам: для дизайна, документов, игр.",
            };
            fonts.Bytes = Result.Fonts.Sum(f => f.Bytes);
            fonts.Files = Result.Fonts.Count;
            fonts.Measured = true;
            foreach (var f in Result.Fonts)
                fonts.Children.Add(new SubItem { Id = f.Path, Title = f.Name, Bytes = f.Bytes, Selectable = false });
            add(fonts);
        }

        var hosts = Path.Combine(paths.Get("Windows")!, "System32", "drivers", "etc", "hosts");
        try
        {
            if (File.Exists(hosts))
            {
                int custom = File.ReadAllLines(hosts).Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
                if (custom > 0)
                    add(new PlanItem
                    {
                        Id = "win.hosts",
                        Section = Section.Windows,
                        Kind = ItemKind.Archive,
                        Title = "Файл hosts",
                        Glyph = "",
                        Roots = [new SourceRoot(@"{Windows}\System32\drivers\etc\hosts")],
                        Selected = true,
                        Note = $"{Fmt.Count(custom, "своя строка", "своих строки", "своих строк")}: блокировки и адреса сайтов.",
                    });
            }
        }
        catch { }
    }

    private static void UpdateDriverSize(PlanItem drv)
    {
        drv.Bytes = drv.Children.Where(c => c.Selected).Sum(c => c.Bytes);
        drv.Files = drv.Children.Count(c => c.Selected);
        drv.Measured = true;
    }

    // ───────────────────────────── Важно ─────────────────────────────

    private async Task ScanImportantAsync(Action<PlanItem> add, CancellationToken ct)
    {
        foreach (var b in Result.Items.Where(i => i.PasswordsUrl is not null).ToList())
            add(new PlanItem
            {
                Id = "important.passwords." + b.Id,
                Section = Section.Important,
                Kind = ItemKind.Note,
                Title = $"Сохрани пароли из {b.Title}",
                Glyph = "",
                Selectable = false,
                PasswordsUrl = b.PasswordsUrl,
                BrowserExe = b.BrowserExe,
                Note = "Закладки и расширения переедут, а пароли и входы на сайты (куки) — нет: браузер шифрует их под конкретный " +
                       "компьютер, и на новой системе они не откроются. Включи синхронизацию в браузере или выгрузи пароли в файл " +
                       "и положи его в «Документы». (У Firefox всё переносится само.)",
            });

        if (paths.IsSandbox) return;

        try { Result.BitLocker = await BitLocker.ScanAsync(ct).ConfigureAwait(false); }
        catch { }
        if (Result.BitLocker.Count > 0)
        {
            var drives = string.Join(", ", Result.BitLocker.Select(v => v.Drive));
            add(new PlanItem
            {
                Id = "important.bitlocker",
                Section = Section.Important,
                Kind = ItemKind.BitLocker,
                Title = $"Ключи BitLocker для {drives}",
                Glyph = "",
                Selected = true,
                Sensitive = true,
                Note = "Эти диски зашифрованы. Новая винда без ключа восстановления их не откроет — сохраню ключи в сборку. " +
                       "Перепиши их ещё и на бумагу или в аккаунт Microsoft.",
            });
        }

        try { Result.OemKey = Activation.OemKey(); }
        catch { }
        add(new PlanItem
        {
            Id = "important.activation",
            Section = Section.Important,
            Kind = ItemKind.Note,
            Title = "Активация Windows",
            Glyph = "",
            Selectable = false,
            Note = Result.OemKey is not null
                ? $"Ключ из BIOS: {Result.OemKey}. На этом железе Windows активируется сама — при установке можно нажать «У меня нет ключа»."
                : "Цифровая лицензия привязана к железу: на этом компьютере Windows активируется сама. При установке жми «У меня нет ключа» " +
                  "и выбирай ту же редакцию, что сейчас — " + Native.WindowsName() + ".",
        });
    }

    // ───────────────────────────── Размеры ─────────────────────────────

    private async Task MeasureAsync(List<PlanItem> items, CancellationToken ct)
    {
        // Большие папки считаются дольше — запускаем их первыми, чтобы общее время было меньше.
        var order = items.OrderBy(i => i.Section == Section.Files ? 0 : 1).ToList();
        await Parallel.ForEachAsync(order, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, (item, token) =>
        {
            long bytes = 0;
            int files = 0;
            foreach (var r in item.Roots)
            {
                if (!paths.TryResolve(r.Path, out var full)) continue;
                var (b, n) = FileWalker.Measure(full, new GlobSet(r.Exclude), Result.Stats, token, r.TopOnly);
                bytes += b;
                files += n;
            }
            item.Bytes = bytes;
            item.Files = files;
            item.Measured = true;
            if (item.Warning == "Удалится при переустановке" && bytes > BigFolder)
            {
                item.Selected = false;
                item.Note = (item.Note is null ? "" : item.Note + " ") + "Большая папка — отметь, если она нужна.";
            }
            // Пустые папки игр и программ только засоряют список.
            if (files == 0 && item.Registry.Length == 0)
            {
                item.Selected = false;
                item.Hidden = true;
            }
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    }
}
