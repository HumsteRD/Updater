using System.Diagnostics;

namespace Updater.Core;

/// <summary>Строки плана на новой винде — из описи сборки.</summary>
public static class RestorePlan
{
    public const string ProgramsId = "restore.programs";
    public const string RuntimesId = "restore.runtimes";
    public const string PopularId = "restore.popular";
    public const string StoreId = "restore.store";
    public const string SettingsId = "restore.settings";
    public const string DriversId = "restore.drivers";
    public const string WifiId = "restore.wifi";
    public const string FontsId = "restore.fonts";

    public static List<PlanItem> Build(PackageManifest m, string root, PathResolver paths)
    {
        var list = new List<PlanItem>();

        var drivers = m.Drivers.Where(d => d.Stored).ToList();
        if (drivers.Count > 0)
        {
            var item = new PlanItem
            {
                Id = DriversId, Section = Section.Windows, Kind = ItemKind.Drivers, Title = "Драйверы", Glyph = "", Selected = true,
                Note = "Ставятся первыми — чтобы сразу заработали сеть, звук и периферия. Windows возьмёт драйвер, только если он подходит к устройству и новее своего.",
            };
            foreach (var d in drivers)
                item.Children.Add(new SubItem
                {
                    Id = d.Inf, Title = string.IsNullOrEmpty(d.Device) ? d.Package : d.Device,
                    Detail = $"{Drivers.ClassName(d.Class)} · {d.Provider} · {d.Version}", Bytes = d.Bytes, Selected = true, Tag = d,
                });
            item.Bytes = drivers.Sum(d => d.Bytes);
            item.Measured = true;
            list.Add(item);
        }

        if (m.WifiProfiles.Count > 0)
        {
            var wifi = new PlanItem
            {
                Id = WifiId, Section = Section.Windows, Kind = ItemKind.Wifi, Title = "Сети Wi-Fi", Glyph = "", Selected = true,
                Note = "Компьютер сам подключится к знакомым сетям.",
            };
            wifi.SizeOverride = Fmt.Count(m.WifiProfiles.Count, "сеть", "сети", "сетей");
            foreach (var w in m.WifiProfiles)
            {
                var info = Wifi.Inspect(Package.Abs(root, Package.WifiDir + "/" + w));
                wifi.Children.Add(new SubItem
                {
                    Id = w, Title = info?.Name ?? WifiName(w), Selectable = false,
                    Chip = info?.PasswordLost == true ? "без пароля" : null,
                    Detail = info?.PasswordLost == true ? "Пароль сохранён зашифрованным под старый ПК — введёшь при подключении" : null,
                });
            }
            list.Add(wifi);
        }

        foreach (var si in m.Items)
        {
            var item = new PlanItem
            {
                Id = si.Id, Section = si.Section, Kind = si.Kind, Title = si.Title, Glyph = si.Glyph, Note = si.Note,
                Processes = si.Processes, Sensitive = si.Sensitive, WingetId = si.WingetId, After = si.After, Stored = si,
                Selected = true,
            };
            item.Bytes = si.Bytes;
            item.Files = si.Files;
            item.Measured = true;
            var first = si.Roots.FirstOrDefault();
            if (first is not null && paths.TryResolve(first.Target, out var target))
            {
                item.Detail = target;
                if (target.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) && si.Section == Section.Files)
                    item.Warning = "В OneDrive";
            }
            if (!si.Complete) item.Warning = "Собрано не полностью";
            if (!File.Exists(Package.Abs(root, si.Stored)) && !Directory.Exists(Package.Abs(root, si.Stored)))
            {
                item.Selected = false;
                item.Selectable = false;
                item.Warning = "Нет в сборке";
            }
            list.Add(item);
        }

        if (m.Fonts.Count > 0)
        {
            var fonts = new PlanItem
            {
                Id = FontsId, Section = Section.Windows, Kind = ItemKind.Fonts, Title = "Шрифты", Glyph = "", Selected = true,
                Note = "Шрифты, которые ты ставил на старой винде.",
            };
            fonts.SizeOverride = Fmt.Count(m.Fonts.Count, "шрифт", "шрифта", "шрифтов");
            foreach (var f in m.Fonts) fonts.Children.Add(new SubItem { Id = f.File, Title = f.Name, Selectable = false, Tag = f });
            list.Add(fonts);
        }

        // Программы старого ПК.
        var programs = new PlanItem
        {
            Id = ProgramsId, Section = Section.Programs, Kind = ItemKind.Programs, Title = "Программы со старого ПК", Glyph = "",
            Selected = true,
            Note = "Ставятся через winget тихо, без «Далее-Далее». Для остальных — ссылка, откуда скачать.",
        };
        var installedIds = InstalledIds(paths);
        // Портативные программы и игры из папок в корне C:, которые едут в сборке, вернутся вместе с папкой.
        var copiedFolders = m.Items.Where(i => i.Id.StartsWith("files.c.", StringComparison.OrdinalIgnoreCase))
            .SelectMany(i => i.Roots).Select(r => OldPath(m, r.Target)).Where(p => p is not null).Select(p => p!.TrimEnd('\\'))
            .ToList();
        foreach (var p in m.Programs.Where(p => !p.IsRuntime))
        {
            bool onFlash = p.LocalInstaller is not null && File.Exists(Package.Abs(root, p.LocalInstaller));
            bool auto = p.WingetId is not null || p.Download is not null || onFlash;
            bool withFolder = !auto && !string.IsNullOrEmpty(p.InstallLocation) && copiedFolders.Any(f =>
                p.InstallLocation.TrimEnd('\\').Equals(f, StringComparison.OrdinalIgnoreCase) ||
                p.InstallLocation.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));
            bool needsHands = !auto && !p.OffSystemDrive && !withFolder;
            // Установщик на флешке ставит без интернета — показываем это первым.
            var chip = onFlash ? "с флешки" : p.WingetId is not null ? "winget" : p.Download is not null ? "скачать"
                : withFolder ? "вернётся с папкой" : p.OffSystemDrive ? "на диске " + Drive(p.InstallLocation) : "вручную";
            programs.Children.Add(new SubItem
            {
                Id = p.WingetId ?? p.Download ?? p.Name,
                Title = p.Name,
                Detail = p.Note ?? p.Publisher,
                Chip = chip,
                ChipIsGood = auto || withFolder,
                Selected = auto && !p.OffSystemDrive && !(p.WingetId is not null && !onFlash && installedIds.Contains(p.WingetId)),
                Selectable = auto,
                Tag = p,
                Link = needsHands ? p.Url ?? $"https://www.google.com/search?q={Uri.EscapeDataString(p.Name + " скачать")}" : null,
                LinkText = needsHands ? p.Url is null ? "Найти" : "Сайт" : null,
            });
        }
        programs.SizeOverride = Fmt.Count(programs.Children.Count(c => c.Selectable), "встанет сама", "встанут сами", "встанут сами");
        if (programs.Children.Count > 0) list.Add(programs);

        // Библиотеки: и те, что были, и полный набор для игр.
        var pack = ProgramCatalog.Shared.Runtimes;
        var runtimes = new PlanItem
        {
            Id = RuntimesId, Section = Section.Programs, Kind = ItemKind.Programs, Title = pack.Title, Glyph = "", Selected = true,
            Note = pack.Note,
        };
        // Отмечены только библиотеки, которые стояли на старом ПК: ставим человеку то, что ему было нужно,
        // а не весь набор подряд. Остальное из набора — по желанию.
        var oldRuntimeIds = m.Programs.Where(p => p.IsRuntime && p.WingetId is not null).Select(p => p.WingetId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in oldRuntimeIds.Concat(pack.Items).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = WingetIndex.Shared.Find(id)?.Name ?? id;
            bool installed = installedIds.Contains(id);
            bool had = oldRuntimeIds.Contains(id);
            // Если библиотеку скачали на флешку — ставим с неё (работает без интернета).
            var onFlash = m.Programs.FirstOrDefault(p => string.Equals(p.WingetId, id, StringComparison.OrdinalIgnoreCase)
                && p.LocalInstaller is not null && File.Exists(Package.Abs(root, p.LocalInstaller)));
            bool wanted = !installed && (had || onFlash is not null);
            runtimes.Children.Add(new SubItem
            {
                Id = id, Title = name,
                Chip = installed ? "уже стоит" : onFlash is not null ? "с флешки" : had ? "было на старом ПК" : "по желанию",
                ChipIsGood = wanted, Selected = wanted,
                Tag = (object?)onFlash ?? id,
            });
        }
        int runtimesWanted = runtimes.Children.Count(c => c.Selected);
        runtimes.SizeOverride = runtimesWanted > 0 ? Fmt.Count(runtimesWanted, "пакет", "пакета", "пакетов") : "по желанию";
        list.Add(runtimes);

        // Популярное — отмечается вручную.
        var popular = new PlanItem
        {
            Id = PopularId, Section = Section.Programs, Kind = ItemKind.Programs, Title = "Добавить популярные программы", Glyph = "",
            Selectable = false,
            Note = "Если хочется поставить что-то ещё — отметь в списке, и оно встанет вместе с остальными.",
        };
        var already = new HashSet<string>(programs.Children.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var g in ProgramCatalog.Shared.Popular)
            foreach (var a in g.Items.Where(a => !already.Contains(a.Id)))
                popular.Children.Add(new SubItem { Id = a.Id, Title = a.Name, Detail = g.Group, Chip = g.Group, Selected = false, Tag = a.Id });
        popular.SizeOverride = "по желанию";
        list.Add(popular);

        if (m.StoreApps.Count > 0)
        {
            var store = new PlanItem
            {
                Id = StoreId, Section = Section.Programs, Kind = ItemKind.Note, Title = "Приложения из Microsoft Store", Glyph = "",
                Selectable = false,
                Note = "Store ставит их сам после входа в аккаунт. Открой страницу и нажми «Получить».",
            };
            store.SizeOverride = Fmt.Count(m.StoreApps.Count, "приложение", "приложения", "приложений");
            foreach (var a in m.StoreApps)
                store.Children.Add(new SubItem
                {
                    Id = a.FamilyName, Title = a.Name, Selectable = false, Tag = a,
                    Link = $"ms-windows-store://pdp/?PFN={a.FamilyName}", LinkText = "Открыть в Store",
                });
            list.Add(store);
        }

        if (m.Windows is not null)
        {
            var ws = new PlanItem
            {
                Id = SettingsId, Section = Section.Windows, Kind = ItemKind.WindowsSettings, Title = "Настройки Windows", Glyph = "",
                Selected = true, Payload = m.Windows,
                Note = "Каждую настройку можно вернуть как было, выбрать по-другому или не трогать.",
            };
            ws.SizeOverride = Fmt.Count(m.Windows.Settings.Count, "настройка", "настройки", "настроек");
            list.Add(ws);
        }
        return list;
    }

    /// <summary>
    /// Что ставить: отмеченные программы из отмеченных групп. Группа без флажка («популярные») — просто
    /// набор: из неё берутся отмеченные строки.
    /// </summary>
    public static List<SubItem> SelectedPrograms(IEnumerable<PlanItem> plan) =>
        plan.Where(i => i.Kind == ItemKind.Programs && (i.Selected || !i.Selectable))
            .SelectMany(i => i.Children.Where(c => c.Selected && c.Selectable))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    private static string WifiName(string file)
    {
        // netsh называет файл «<интерфейс>-<сеть>.xml», а интерфейс сам бывает с дефисом: «Wi-Fi», «Wi-Fi 2».
        var name = Path.GetFileNameWithoutExtension(file);
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^Wi-Fi(?: \d+)?-(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        int dash = name.IndexOf('-');
        return dash > 0 ? name[(dash + 1)..] : name;
    }

    private static string Drive(string? path) => string.IsNullOrEmpty(path) ? "?" : Path.GetPathRoot(path)?.TrimEnd('\\') ?? "?";

    /// <summary>Путь с меткой → как он выглядел на старом ПК (по таблице папок из описи).</summary>
    private static string? OldPath(PackageManifest m, string tokenPath)
    {
        if (tokenPath.Length == 0 || tokenPath[0] != '{') return tokenPath;
        int end = tokenPath.IndexOf('}');
        if (end < 0 || !m.Source.Folders.TryGetValue(tokenPath[1..end], out var basePath)) return null;
        var rest = tokenPath[(end + 1)..].TrimStart('\\');
        return rest.Length == 0 ? basePath : Path.Combine(basePath, rest);
    }

    /// <summary>Что уже стоит на новой винде (по встроенному индексу) — такое заново не ставим.</summary>
    private static HashSet<string> InstalledIds(PathResolver paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths.IsSandbox) return set;
        try
        {
            foreach (var e in ProgramInventory.ReadArp())
                if (WingetIndex.Shared.Match(e.Key, e.Name, e.Publisher, e.Version) is { } p) set.Add(p.Id);
        }
        catch { }
        return set;
    }
}

public sealed class RestoreOptions
{
    public required string PackageRoot { get; init; }
    public FileCopier.Conflict Conflict { get; init; } = FileCopier.Conflict.NewerWins;
    public IRegistryAccess? Registry { get; init; }
    /// <summary>Пользователь согласился закрыть запущенные программы, чьи настройки возвращаем.</summary>
    public bool CloseRunningApps { get; init; } = true;
    public string WorkDir { get; init; } = Package.TempDir;
    /// <summary>Не ставить winget (для проверки установки с флешки без интернета).</summary>
    public bool SkipWinget { get; init; }
}

public sealed class RestoreSummary
{
    public int FilesRestored;
    public long BytesRestored;
    public int FilesKept;
    public int FilesFailed;
    public int ProgramsInstalled;
    public int ProgramsAlready;
    public List<string> ProgramsFailed { get; } = [];
    public int DriversInstalled;
    public ApplyReport? Settings;
    public bool NeedsReboot;
    public List<string> Problems { get; } = [];
}

/// <summary>
/// Распаковка на новой винде. Две дорожки идут одновременно: программы качаются из интернета,
/// а файлы в это время копируются с флешки — так переезд занимает время самой долгой из них, а не сумму.
/// </summary>
public sealed class Restorer(PathResolver paths, Journal log)
{
    public JobState Data { get; } = new();
    public JobState Apps { get; } = new();
    public RestoreSummary Summary { get; } = new();
    private readonly HashSet<string> _after = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public async Task RunAsync(IReadOnlyList<PlanItem> plan, IReadOnlyList<SettingChoice> settings, RestoreOptions opt, CancellationToken ct)
    {
        var root = opt.PackageRoot;
        log.Info($"Распаковка из сборки {root}");

        var data = plan.Where(i => i.Selected && i.Stored is not null && i.Selectable).ToList();
        var programs = RestorePlan.SelectedPrograms(plan);
        var drivers = plan.FirstOrDefault(i => i.Kind == ItemKind.Drivers && i.Selected);
        var wifi = plan.FirstOrDefault(i => i.Kind == ItemKind.Wifi && i.Selected);
        var fonts = plan.FirstOrDefault(i => i.Kind == ItemKind.Fonts && i.Selected);

        Data.Total = data.Sum(i => i.Bytes) + (fonts is not null ? 1 : 0);
        Apps.Total = programs.Count + (drivers is not null ? 1 : 0) + (wifi is not null ? 1 : 0);

        var systemLane = Task.Run(() => SystemLaneAsync(drivers, wifi, programs, opt, ct), ct);
        var dataLane = Task.Run(() => DataLaneAsync(data, fonts, opt, ct), ct);
        try
        {
            await Task.WhenAll(systemLane, dataLane).ConfigureAwait(false);
        }
        finally
        {
            // Даже при отмене: то, что уже легло на место, пусть заработает.
            if (settings.Count > 0 && !ct.IsCancellationRequested)
            {
                Data.Stage = "Настройки Windows";
                var engine = new WindowsSettings(opt.Registry ?? new LiveRegistry(), log, paths.IsSandbox);
                var old = plan.FirstOrDefault(i => i.Kind == ItemKind.WindowsSettings)?.Payload as WindowsSnapshot;
                var rollbackDir = paths.IsSandbox && paths.SandboxRoot is not null
                    ? Path.Combine(paths.SandboxRoot, "rollback")
                    : Package.DataDir;
                var rollback = Path.Combine(rollbackDir, $"откат-настроек-{DateTime.Now:yyyy-MM-dd-HHmmss}.json");
                Summary.Settings = await engine.ApplyAsync(settings, old, root, rollback, ct).ConfigureAwait(false);
                if (Summary.Settings.NeedsReboot) Summary.NeedsReboot = true;
            }
            if (!paths.IsSandbox) RunAfterActions(settingsRestartedExplorer: settings.Any(s => s.Def.Notify == "explorer" && s.OptionId != SettingChoice.Keep));
        }
        log.Ok("Переезд закончен.");
    }

    // ───────────────────────────── Система и программы ─────────────────────────────

    private async Task SystemLaneAsync(PlanItem? drivers, PlanItem? wifi, List<SubItem> programs, RestoreOptions opt, CancellationToken ct)
    {
        try
        {
            await SystemLaneCoreAsync(drivers, wifi, programs, opt, ct).ConfigureAwait(false);
        }
        finally
        {
            Apps.CurrentItem = null;
            Apps.CurrentFile = null;
        }
    }

    private async Task SystemLaneCoreAsync(PlanItem? drivers, PlanItem? wifi, List<SubItem> programs, RestoreOptions opt, CancellationToken ct)
    {
        if (drivers is not null)
        {
            Apps.CurrentItem = "Драйверы";
            await RestoreDriversAsync(drivers, opt.PackageRoot, ct).ConfigureAwait(false);
            Apps.Add(1);
        }
        if (wifi is not null)
        {
            Apps.CurrentItem = "Сети Wi-Fi";
            bool wirelessDriver = drivers?.Children.Any(c => c.Selected && c.Tag is DriverRecord d && Wifi.LooksWireless(d.Device)) == true;
            await RestoreWifiAsync(wifi, opt.PackageRoot, ct, wirelessDriver).ConfigureAwait(false);
            Apps.Add(1);
        }
        if (programs.Count == 0) return;

        if (paths.IsSandbox)
        {
            foreach (var p in programs)
            {
                p.Status = ItemStatus.Skipped;
                p.StatusText = "песочница";
                Apps.Add(1);
            }
            return;
        }

        bool hasWinget;
        string? wingetReason;
        if (opt.SkipWinget)
        {
            hasWinget = false;
            wingetReason = "winget отключён для проверки установки с флешки";
            log.Info(wingetReason);
        }
        else
        {
            Apps.CurrentItem = "winget";
            (hasWinget, wingetReason) = await Winget.EnsureAsync(s => Apps.CurrentFile = s, l => log.Info(l), ct).ConfigureAwait(false);
            if (!hasWinget) log.Warn("winget не поставился: " + wingetReason);
        }
        foreach (var p in programs)
        {
            ct.ThrowIfCancellationRequested();
            Apps.WaitIfPaused(ct);
            Apps.CurrentItem = p.Title;
            Apps.CurrentFile = "";
            p.Status = ItemStatus.Running;
            p.StatusText = "Ставлю…";
            InstallResult r;
            try
            {
                var rec = p.Tag as ProgramRecord;
                var id = rec?.WingetId ?? (p.Tag as string) ?? (rec is null ? p.Id : null);
                var localInstaller = rec?.LocalInstaller is { } li ? Package.Abs(opt.PackageRoot, li) : null;
                bool haveLocal = localInstaller is not null && File.Exists(localInstaller);

                if (id is not null && hasWinget)
                {
                    r = await Winget.InstallAsync(id, rec?.Source, line => Apps.CurrentFile = line, ct).ConfigureAwait(false);
                    if (r.Outcome == InstallOutcome.NeedsUserContext)
                    {
                        p.StatusText = "Ставлю без прав администратора…";
                        r = await Winget.InstallAsUserAsync(id, rec?.Source, Path.Combine(opt.WorkDir, "user-installs"), ct).ConfigureAwait(false);
                    }
                    // winget не сработал, но установщик лежит на флешке — ставим с неё.
                    if (!r.Ok && haveLocal)
                    {
                        p.StatusText = "winget не смог — ставлю с флешки…";
                        r = await RunLocalInstallerAsync(rec!, localInstaller!, ct).ConfigureAwait(false);
                    }
                }
                else if (haveLocal)
                {
                    p.StatusText = "Ставлю с флешки…";
                    r = await RunLocalInstallerAsync(rec!, localInstaller!, ct).ConfigureAwait(false);
                }
                else if (id is not null && !hasWinget)
                    r = new InstallResult(InstallOutcome.Failed, -1, "winget не поставился: " + wingetReason);
                else if (rec?.Download is not null)
                    r = await Winget.DownloadAndRunAsync(rec.Download, Path.Combine(opt.WorkDir, "downloads"), line => Apps.CurrentFile = line, ct).ConfigureAwait(false);
                else
                    r = new InstallResult(InstallOutcome.Failed, -1, "Нет способа поставить автоматически");
            }
            catch (OperationCanceledException)
            {
                p.Status = ItemStatus.Pending;
                p.StatusText = "Остановлено";
                throw;
            }
            catch (Exception ex)
            {
                r = new InstallResult(InstallOutcome.Failed, -1, ex.Message);
            }

            p.Status = r.Outcome switch
            {
                InstallOutcome.Installed => ItemStatus.Done,
                InstallOutcome.AlreadyInstalled => ItemStatus.Done,
                InstallOutcome.NeedsReboot => ItemStatus.Warning,
                _ => ItemStatus.Failed,
            };
            p.StatusText = r.Message;
            lock (_gate)
            {
                if (r.Outcome == InstallOutcome.Installed) Summary.ProgramsInstalled++;
                else if (r.Outcome == InstallOutcome.AlreadyInstalled) Summary.ProgramsAlready++;
                else if (r.Outcome == InstallOutcome.NeedsReboot)
                {
                    Summary.ProgramsInstalled++;
                    Summary.NeedsReboot = true;
                }
                else Summary.ProgramsFailed.Add($"{p.Title}: {r.Message}");
            }
            if (r.Ok) log.Ok($"{p.Title}: {r.Message}");
            else log.Warn($"{p.Title}: {r.Message}");
            Apps.Add(1);
        }
        Apps.CurrentItem = null;
        Apps.CurrentFile = null;
    }

    /// <summary>Поставить программу из установщика, скачанного на флешку (работает и без интернета).</summary>
    private async Task<InstallResult> RunLocalInstallerAsync(ProgramRecord rec, string file, CancellationToken ct)
    {
        string exe, args;
        if (rec.LocalIsMsi)
        {
            exe = Proc.System32("msiexec.exe");
            args = $"/i \"{file}\" {rec.SilentArgs ?? "/qn /norestart"}";
        }
        else
        {
            exe = file;
            args = rec.SilentArgs ?? "";
        }
        var r = await Proc.RunAsync(exe, args, ct, timeout: TimeSpan.FromMinutes(40)).ConfigureAwait(false);
        // 0 — готово, 3010/1641 — нужна перезагрузка, 1638 — такая версия уже стоит.
        return unchecked((uint)r.ExitCode) switch
        {
            0 => new InstallResult(InstallOutcome.Installed, 0, "Установлено с флешки"),
            1638 => new InstallResult(InstallOutcome.AlreadyInstalled, r.ExitCode, "Уже установлено"),
            3010 or 1641 => new InstallResult(InstallOutcome.NeedsReboot, r.ExitCode, "Установлено с флешки, нужна перезагрузка"),
            _ => new InstallResult(InstallOutcome.Failed, r.ExitCode, $"Установщик с флешки завершился с кодом {r.ExitCode}"),
        };
    }

    public async Task RestoreDriversAsync(PlanItem drivers, string root, CancellationToken ct)
    {
        drivers.Status = ItemStatus.Running;
        if (paths.IsSandbox)
        {
            drivers.Status = ItemStatus.Skipped;
            drivers.StatusText = "песочница";
            return;
        }
        if (!Native.IsAdmin())
        {
            drivers.Status = ItemStatus.Failed;
            drivers.StatusText = "Нужны права администратора";
            return;
        }
        // Ставим только отмеченные: копируем их пакеты во временную папку и отдаём pnputil одним разом.
        var src = Package.Abs(root, Package.DriversDir);
        var selected = drivers.Children.Where(c => c.Selected && c.Tag is DriverRecord).Select(c => (DriverRecord)c.Tag!).ToList();
        int ok = 0;
        foreach (var d in selected)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.Combine(src, d.Package);
            if (!Directory.Exists(dir)) continue;
            Apps.CurrentFile = d.Device;
            var r = await Drivers.InstallAsync(dir, ct).ConfigureAwait(false);
            // 0 — поставлен, 3010 — нужна перезагрузка, 259 — подходящего устройства нет (драйвер добавлен в хранилище).
            if (r.ExitCode is 0 or 3010 or 259) ok++;
            if (r.ExitCode == 3010) Summary.NeedsReboot = true;
            var child = drivers.Children.FirstOrDefault(c => ReferenceEquals(c.Tag, d));
            if (child is not null)
            {
                child.Status = r.ExitCode is 0 or 3010 or 259 ? ItemStatus.Done : ItemStatus.Failed;
                child.StatusText = r.ExitCode switch { 0 => "Установлен", 3010 => "После перезагрузки", 259 => "Добавлен про запас", _ => $"Код {r.ExitCode}" };
            }
        }
        Summary.DriversInstalled = ok;
        drivers.Status = ok == selected.Count ? ItemStatus.Done : ItemStatus.Warning;
        drivers.StatusText = $"Готово: {ok} из {selected.Count}";
        log.Ok($"Драйверы: {ok} из {selected.Count}");
    }

    /// <param name="wirelessDriverJustInstalled">Только что поставили драйвер Wi-Fi: адаптер появится через несколько
    /// секунд — ждём его дольше, а не сдаёмся сразу.</param>
    public async Task RestoreWifiAsync(PlanItem wifi, string root, CancellationToken ct, bool wirelessDriverJustInstalled = false)
    {
        if (paths.IsSandbox)
        {
            wifi.Status = ItemStatus.Skipped;
            return;
        }
        var dir = Package.Abs(root, Package.WifiDir);
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.xml") : [];
        if (files.Length == 0)
        {
            wifi.Status = ItemStatus.Warning;
            wifi.StatusText = "Нечего добавлять";
            return;
        }
        if (!await Wifi.WaitForInterfaceAsync(TimeSpan.FromSeconds(wirelessDriverJustInstalled ? 45 : 6), ct).ConfigureAwait(false))
        {
            wifi.Status = ItemStatus.Warning;
            wifi.StatusText = "На этом ПК пока нет Wi-Fi — сети не добавлены";
            log.Warn($"Сети Wi-Fi: адаптера нет. Профили лежат в сборке ({Package.WifiDir}) — их можно добавить позже, повторив распаковку только с пунктом «Сети Wi-Fi».");
            return;
        }
        int ok = 0, lost = 0;
        var failed = new List<string>();
        foreach (var xml in files)
        {
            var info = Wifi.Inspect(xml);
            var name = info?.Name ?? Path.GetFileNameWithoutExtension(xml);
            var r = await Wifi.ImportAsync(xml, ct).ConfigureAwait(false);
            // Адаптер мог моргнуть (служба WLAN перезапускается после драйвера) — одна повторная попытка.
            if (!r.Ok && Wifi.NoWifiHere(r.All))
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                r = await Wifi.ImportAsync(xml, ct).ConfigureAwait(false);
            }
            if (r.Ok)
            {
                ok++;
                if (info?.PasswordLost == true) lost++;
            }
            else
            {
                failed.Add(name);
                log.Warn($"Сеть Wi-Fi «{name}» не добавилась: {r.All.Trim()}");
            }
        }
        wifi.Status = failed.Count == 0 && lost == 0 ? ItemStatus.Done : ItemStatus.Warning;
        wifi.StatusText = ok == 0 ? "Не добавились — см. журнал"
            : lost > 0 ? $"Добавлено {ok} из {files.Length}; у {lost} пароль не сохранился — введи его при подключении"
            : $"Добавлено {ok} из {files.Length}";
        log.Ok($"Сети Wi-Fi: {ok} из {files.Length}");
    }

    // ───────────────────────────── Файлы и настройки ─────────────────────────────

    private async Task DataLaneAsync(List<PlanItem> data, PlanItem? fonts, RestoreOptions opt, CancellationToken ct)
    {
        // Настройки программ и сохранения — первыми: они маленькие и должны лечь до первого запуска программ.
        var ordered = data.OrderBy(i => i.Section switch { Section.Apps => 0, Section.Games => 1, Section.Windows => 2, _ => 3 })
            .ThenBy(i => i.Bytes).ToList();
        foreach (var item in ordered)
        {
            ct.ThrowIfCancellationRequested();
            long before = Data.Done;
            await RestoreItemAsync(item, opt, ct).ConfigureAwait(false);
            long got = Data.Done - before;
            if (got < item.Bytes) Data.Add(item.Bytes - got);
        }
        if (fonts is not null)
        {
            RestoreFonts(fonts, opt.PackageRoot);
            Data.Add(1);
        }
        Data.CurrentItem = null;
        Data.CurrentFile = null;
    }

    public async Task RestoreItemAsync(PlanItem item, RestoreOptions opt, CancellationToken ct)
    {
        var si = item.Stored!;
        Data.CurrentItem = item.Title;
        Data.Stage = item.Title;
        item.Status = ItemStatus.Running;
        item.StatusText = "Возвращаю…";
        try
        {
            if (opt.CloseRunningApps && !paths.IsSandbox) CloseProcesses(si.Processes);

            var targets = new List<(StoredRoot Root, string Target)>();
            foreach (var r in si.Roots)
            {
                if (!paths.TryResolve(r.Target, out var t))
                {
                    log.Warn($"{item.Title}: не знаю, куда вернуть {r.Target}");
                    continue;
                }
                targets.Add((r, t));
            }

            FileCopier.Outcome o;
            var regFiles = new List<string>();
            var storedPath = Package.Abs(opt.PackageRoot, si.Stored);
            if (si.Kind == ItemKind.Archive)
            {
                var (res, regs) = await ZipPacker.UnpackAsync(storedPath, targets, Path.Combine(opt.WorkDir, "reg"), Data, ct).ConfigureAwait(false);
                o = res;
                regFiles.AddRange(regs);
            }
            else
            {
                o = new FileCopier.Outcome();
                foreach (var (r, target) in targets)
                {
                    var dir = r.Entry.Length > 0 ? Path.Combine(storedPath, r.Entry) : storedPath;
                    if (r.IsFile)
                    {
                        // Одиночный файл лежит в своей подпапке под своим именем.
                        var file = Directory.Exists(dir) ? Directory.GetFiles(dir).FirstOrDefault() : null;
                        if (file is null) continue;
                        var fi = new FileInfo(file);
                        await FileCopier.CopyOneAsync(file, target, fi.LastWriteTimeUtc, Data, ct).ConfigureAwait(false);
                        o.Files++;
                        o.Bytes += fi.Length;
                        continue;
                    }
                    var part = await FileCopier.RestoreAsync(dir, target, opt.Conflict, Data, ct).ConfigureAwait(false);
                    o.Files += part.Files;
                    o.Bytes += part.Bytes;
                    o.Kept += part.Kept;
                    o.Skipped += part.Skipped;
                    o.Failed.AddRange(part.Failed);
                    if (part.DiskFull) o.DiskFull = true;
                }
                foreach (var reg in si.RegFiles) regFiles.Add(Package.Abs(opt.PackageRoot, si.Stored + "/" + reg));
            }

            int regOk = 0;
            foreach (var reg in regFiles)
            {
                if (paths.IsSandbox)
                {
                    regOk = regFiles.Count;
                    break;
                }
                var r = await Proc.RunAsync(Proc.System32("reg.exe"), $"import \"{reg}\"", ct).ConfigureAwait(false);
                if (r.Ok) regOk++;
                else o.Failed.Add("реестр — не импортировался");
            }

            lock (_gate)
            {
                Summary.FilesRestored += o.Files;
                Summary.BytesRestored += o.Bytes;
                Summary.FilesKept += o.Kept;
                Summary.FilesFailed += o.Failed.Count;
                if (o.Failed.Count > 0) Summary.Problems.Add($"{item.Title}: {string.Join("; ", o.Failed.Take(3))}");
                if (si.After is not null) _after.Add(si.After);
            }

            if (o.DiskFull)
            {
                item.Status = ItemStatus.Failed;
                item.StatusText = "Не хватило места на диске";
            }
            else if (o.Failed.Count > 0)
            {
                item.Status = ItemStatus.Warning;
                item.StatusText = $"Готово, но {Fmt.Count(o.Failed.Count, "файл", "файла", "файлов")} не встали на место";
            }
            else
            {
                item.Status = ItemStatus.Done;
                var kept = o.Kept > 0 ? $", {o.Kept} новее на этом ПК — оставил" : "";
                item.StatusText = o.Files + o.Skipped == 0 && regOk > 0
                    ? Fmt.Count(regOk, "ветка реестра", "ветки реестра", "веток реестра")
                    : $"{Fmt.Files(o.Files + o.Skipped)}{kept}";
            }
            log.Ok($"{item.Title}: {item.StatusText}");
        }
        catch (OperationCanceledException)
        {
            item.Status = ItemStatus.Pending;
            item.StatusText = "Остановлено";
            throw;
        }
        catch (Exception ex)
        {
            item.Status = ItemStatus.Failed;
            item.StatusText = "Ошибка: " + ex.Message;
            lock (_gate) Summary.Problems.Add($"{item.Title}: {ex.Message}");
            log.Error($"{item.Title}: {ex.Message}");
        }
    }

    private void RestoreFonts(PlanItem fonts, string root)
    {
        if (paths.IsSandbox)
        {
            fonts.Status = ItemStatus.Skipped;
            return;
        }
        var dir = Package.Abs(root, Package.FontsDir);
        bool admin = Native.IsAdmin();
        int ok = 0;
        foreach (var c in fonts.Children)
        {
            if (c.Tag is not FontRecord f) continue;
            try
            {
                Fonts.Install(Path.Combine(dir, f.File), f.Name, forAllUsers: admin && !f.PerUser);
                ok++;
            }
            catch (Exception ex)
            {
                log.Warn($"Шрифт {f.Name}: {ex.Message}");
            }
        }
        Native.BroadcastFontChange();
        fonts.Status = ok == fonts.Children.Count ? ItemStatus.Done : ItemStatus.Warning;
        fonts.StatusText = $"Поставлено {ok} из {fonts.Children.Count}";
    }

    /// <summary>Какие из программ, чьи настройки возвращаем, сейчас запущены.</summary>
    public static List<string> RunningOf(IEnumerable<PlanItem> items)
    {
        var running = new List<string>();
        foreach (var i in items)
            foreach (var p in i.Processes)
                try
                {
                    if (Process.GetProcessesByName(p).Length > 0 && !running.Contains(i.Title)) running.Add(i.Title);
                }
                catch { }
        return running;
    }

    public static void CloseProcesses(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var p in list)
            {
                try
                {
                    if (p.CloseMainWindow()) p.WaitForExit(4000);
                    if (!p.HasExited) p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
    }

    private void RunAfterActions(bool settingsRestartedExplorer)
    {
        if (_after.Contains("startmenu")) Native.RestartStartMenu();
        if (_after.Contains("explorer") && !settingsRestartedExplorer) Native.RestartExplorer();
    }
}
