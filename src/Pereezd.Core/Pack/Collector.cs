using System.Text;

namespace Pereezd.Core;

public sealed class CollectOptions
{
    public required string PackageRoot { get; init; }
    /// <summary>Резать файлы больше этого размера (FAT32).</summary>
    public long SplitSize { get; init; } = long.MaxValue;
    /// <summary>Путь к своему exe для самокопирования; null — определить самому.</summary>
    public string? SelfExe { get; init; }
    public IRegistryAccess? Registry { get; init; }
}

/// <summary>Сборы на старой винде: всё отмеченное — в одну папку, вместе с самой программой.</summary>
public sealed class Collector(PathResolver paths, Journal log, JobState state)
{
    /// <summary>Пункты больше этого пишутся папкой, а не архивом: так их можно докачать по файлам.</summary>
    public const long FolderModeThreshold = 2L * 1024 * 1024 * 1024;

    public bool DiskFull { get; private set; }

    public async Task<PackageManifest> RunAsync(IReadOnlyList<PlanItem> items, ScanResult scan, CollectOptions opt, CancellationToken ct)
    {
        var root = opt.PackageRoot;
        Directory.CreateDirectory(root);
        log.AttachFile(Path.Combine(root, Package.JournalName));
        log.Info($"Сборы в {root}");

        var manifest = LoadOrCreate(root);
        var previous = manifest.Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var usedStored = new HashSet<string>(manifest.Items.Select(i => i.Stored), StringComparer.OrdinalIgnoreCase);

        state.Stage = "Копирую программу";
        CopySelf(root, opt.SelfExe);
        WriteReadme(root);
        Save(root, manifest);

        var selected = items.Where(i => i.Selected && i.Kind != ItemKind.Note && i.Kind != ItemKind.Programs)
            .Where(i => i.Kind != ItemKind.Installers || i.Children.Any(c => c.Selected)).ToList();
        var ordered = selected.OrderBy(Order).ToList();
        state.Total = ordered.Sum(WeightOf);
        var reg = opt.Registry ?? new LiveRegistry();
        var walk = new WalkStats();

        foreach (var item in ordered)
        {
            ct.ThrowIfCancellationRequested();
            state.CurrentItem = item.Title;
            state.Stage = item.Title;
            item.Status = ItemStatus.Running;
            item.StatusText = "Собираю…";
            long before = state.Done;
            try
            {
                switch (item.Kind)
                {
                    case ItemKind.Folder:
                    case ItemKind.Archive:
                        await CollectDataAsync(item, root, opt, manifest, previous, usedStored, walk, ct).ConfigureAwait(false);
                        break;
                    case ItemKind.WindowsSettings:
                        manifest.Windows = await new WindowsSettings(reg, log, paths.IsSandbox)
                            .CaptureAsync(Package.Abs(root, Package.WindowsDir), ct).ConfigureAwait(false);
                        Done(item, $"Запомнил {Fmt.Count(manifest.Windows.Settings.Count, "настройку", "настройки", "настроек")}");
                        break;
                    case ItemKind.Wifi:
                        await CollectWifiAsync(item, root, manifest, ct).ConfigureAwait(false);
                        break;
                    case ItemKind.Drivers:
                        await CollectDriversAsync(item, root, opt, manifest, scan, walk, ct).ConfigureAwait(false);
                        break;
                    case ItemKind.Fonts:
                        CollectFonts(item, root, manifest, scan);
                        break;
                    case ItemKind.BitLocker:
                        await CollectBitLockerAsync(item, root, scan, manifest, ct).ConfigureAwait(false);
                        break;
                    case ItemKind.Installers:
                        await CollectInstallersAsync(item, root, scan, ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = ItemStatus.Pending;
                item.StatusText = "Остановлено";
                Save(root, manifest);
                throw;
            }
            catch (Exception ex)
            {
                item.Status = ItemStatus.Failed;
                item.StatusText = "Ошибка: " + ex.Message;
                log.Error($"{item.Title}: {ex.Message}");
            }
            // Прогресс по пункту доводим до его веса, даже если часть файлов пропущена.
            long expected = WeightOf(item);
            long got = state.Done - before;
            if (got < expected) state.Add(expected - got);
            Save(root, manifest);

            if (DiskFull)
            {
                foreach (var rest in ordered.SkipWhile(x => x != item).Skip(1))
                {
                    rest.Status = ItemStatus.Skipped;
                    rest.StatusText = "Не хватило места";
                }
                log.Error("Место на диске закончилось — сборы остановлены. Освободи место или выбери другой диск и запусти сборы ещё раз: уже скопированное повторно не копируется.");
                break;
            }
        }

        manifest.Programs = scan.Programs;
        manifest.StoreApps = scan.StoreApps;
        manifest.SteamGames = scan.SteamGames;
        manifest.Reminders = BuildReminders(items, scan);
        if (!DiskFull)
        {
            manifest.Status = "complete";
            manifest.Finished = DateTime.Now;
        }
        if (walk.CloudFiles > 0)
            log.Info($"Файлы только в облаке OneDrive не копировались: {Fmt.Files(walk.CloudFiles)} ({Fmt.Size(walk.CloudBytes)}) — они и так в облаке.");
        Save(root, manifest);
        try { File.WriteAllText(Path.Combine(root, Package.ReportName), Report.Html(manifest, root), new UTF8Encoding(true)); }
        catch (Exception ex) { log.Warn("Отчёт не записался: " + ex.Message); }
        log.Ok(DiskFull ? "Сборы прерваны: нет места." : $"Сборы закончены: {Fmt.Size(manifest.TotalBytes)} в {root}");
        return manifest;
    }

    private static int Order(PlanItem i) => i.Kind switch
    {
        ItemKind.WindowsSettings => 0,
        ItemKind.BitLocker => 1,
        ItemKind.Wifi => 2,
        ItemKind.Fonts => 3,
        ItemKind.Archive when i.Section == Section.Windows => 4,
        ItemKind.Archive when i.Section == Section.Apps => 5,
        ItemKind.Archive when i.Section == Section.Games => 6,
        ItemKind.Drivers => 7,
        ItemKind.Archive => 8,
        _ => 9,
    };

    private static long WeightOf(PlanItem i) => i.Kind switch
    {
        ItemKind.Folder or ItemKind.Archive or ItemKind.Drivers or ItemKind.Fonts => i.Bytes,
        _ => 0,
    };

    private PackageManifest LoadOrCreate(string root)
    {
        var existing = Package.TryLoad(root);
        if (existing is not null && existing.Source.Computer == Environment.MachineName)
        {
            log.Info($"В папке уже есть сборка от {Fmt.Date(existing.Created)} — дополняю её, скопированное повторно не копирую.");
            existing.Status = "collecting";
            existing.App = AppVersion();
            return existing;
        }
        var m = new PackageManifest
        {
            App = AppVersion(),
            Created = DateTime.Now,
            Source = new SourcePc
            {
                Computer = Environment.MachineName,
                User = Environment.UserName,
                Windows = Native.WindowsName(),
                Build = Native.WindowsBuild,
                Profile = paths.Get("Profile") ?? "",
                Folders = paths.Map.ToDictionary(k => k.Key, k => k.Value),
            },
        };
        return m;
    }

    public static string AppVersion() =>
        Package.AppName + " " + (typeof(Collector).Assembly.GetName().Version?.ToString(3) ?? "1.0");

    private static void Save(string root, PackageManifest m)
    {
        Json.Write(Path.Combine(root, PackageManifest.FileName), m);
        // Докачка в старую сборку: прежняя опись больше не нужна, иначе их будет две.
        FileCopier.TryDelete(Path.Combine(root, PackageManifest.LegacyFileName));
    }

    // ───────────────────────────── Данные ─────────────────────────────

    private async Task CollectDataAsync(PlanItem item, string root, CollectOptions opt, PackageManifest manifest,
        Dictionary<string, StoredItem> previous, HashSet<string> usedStored, WalkStats walk, CancellationToken ct)
    {
        // Большие пункты и всё крупное на FAT32 — папкой: так их можно докачать и не упереться в 4 ГБ на файл.
        var kind = item.Kind;
        if (kind == ItemKind.Archive && (item.Bytes > FolderModeThreshold || (opt.SplitSize < long.MaxValue && item.Bytes > opt.SplitSize * 9 / 10)))
            kind = ItemKind.Folder;

        var stored = previous.TryGetValue(item.Id, out var prev) && prev.Kind == kind
            ? prev.Stored
            : UniqueStored(StoredPathFor(item, kind), usedStored);
        usedStored.Add(stored);

        var si = new StoredItem
        {
            Id = item.Id,
            Section = item.Section,
            Kind = kind,
            Title = item.Title,
            Note = item.Note,
            Glyph = item.Glyph,
            Stored = stored,
            Processes = item.Processes,
            Sensitive = item.Sensitive,
            WingetId = item.WingetId,
            After = item.After,
        };
        manifest.Items.RemoveAll(i => i.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
        manifest.Items.Add(si);

        var resolved = new List<(SourceRoot Root, string Full)>();
        foreach (var r in item.Roots)
            if (paths.TryResolve(r.Path, out var full) && (Directory.Exists(full) || File.Exists(full)))
                resolved.Add((r, full));

        for (int i = 0; i < resolved.Count; i++)
        {
            var (r, full) = resolved[i];
            si.Roots.Add(new StoredRoot
            {
                Target = r.Path,
                Entry = kind == ItemKind.Archive || resolved.Count > 1 ? i.ToString() : "",
                IsFile = File.Exists(full),
            });
        }

        if (kind == ItemKind.Folder)
        {
            for (int i = 0; i < resolved.Count; i++)
            {
                var (r, full) = resolved[i];
                var dest = Package.Abs(root, stored);
                if (si.Roots[i].Entry.Length > 0) dest = Path.Combine(dest, si.Roots[i].Entry);
                FileCopier.Outcome o;
                if (File.Exists(full))
                {
                    // Одиночный файл в пункте-папке: кладём рядом под своим именем.
                    o = new FileCopier.Outcome();
                    var fi = new FileInfo(full);
                    var target = Path.Combine(dest, fi.Name);
                    if (!FileCopier.SameFile(target, fi.Length, fi.LastWriteTimeUtc))
                        await FileCopier.CopyOneAsync(full, target, fi.LastWriteTimeUtc, state, ct).ConfigureAwait(false);
                    else state.Add(fi.Length);
                    o.Files = 1;
                    o.Bytes = fi.Length;
                }
                else
                {
                    o = await FileCopier.MirrorAsync(full, dest, new GlobSet(r.Exclude), r.TopOnly, opt.SplitSize, state, walk, ct).ConfigureAwait(false);
                }
                si.Bytes += o.Bytes + o.SkippedBytes;
                si.Files += o.Files + o.Skipped;
                Record(si, o.Failed);
                if (o.DiskFull) DiskFull = true;
            }
            // Ветки реестра для пункта-папки — отдельными .reg рядом.
            if (item.Registry.Length > 0 && !paths.IsSandbox)
                for (int k = 0; k < item.Registry.Length; k++)
                {
                    var regRel = $"_реестр/{k}.reg";
                    var regFile = Package.Abs(root, stored + "/" + regRel);
                    Directory.CreateDirectory(Path.GetDirectoryName(regFile)!);
                    var res = await Proc.RunAsync(Proc.System32("reg.exe"), $"export \"{item.Registry[k]}\" \"{regFile}\" /y", ct).ConfigureAwait(false);
                    if (res.Ok) si.RegFiles.Add(regRel);
                }
        }
        else
        {
            var zipPath = Package.Abs(root, stored);
            var roots = resolved.Select(x => (x.Full, new GlobSet(x.Root.Exclude), x.Root.TopOnly)).ToList();
            var o = await ZipPacker.PackAsync(zipPath, roots, item.Registry, state, walk, paths.IsSandbox, ct).ConfigureAwait(false);
            si.Bytes = o.Bytes;
            si.Files = o.Files;
            si.RegFiles = o.RegFiles;
            Record(si, o.Failed);
            if (o.DiskFull) DiskFull = true;
        }

        si.Complete = !DiskFull;
        if (DiskFull)
        {
            item.Status = ItemStatus.Failed;
            item.StatusText = "Не хватило места";
        }
        else if (si.FailedCount > 0)
        {
            item.Status = ItemStatus.Warning;
            item.StatusText = $"Готово, но {Fmt.Count(si.FailedCount, "файл", "файла", "файлов")} не удалось взять";
            log.Warn($"{item.Title}: не удалось взять {si.FailedCount} — {string.Join("; ", si.Failed.Take(3))}");
        }
        else if (si.Files == 0 && si.RegFiles.Count > 0)
        {
            Done(item, Fmt.Count(si.RegFiles.Count, "ветка реестра", "ветки реестра", "веток реестра"));
        }
        else
        {
            Done(item, $"{Fmt.Files(si.Files)}, {Fmt.Size(si.Bytes)}");
        }
    }

    private static void Record(StoredItem si, List<string> failed)
    {
        si.FailedCount += failed.Count;
        foreach (var f in failed)
            if (si.Failed.Count < 50) si.Failed.Add(f);
    }

    private void Done(PlanItem item, string text)
    {
        item.Status = ItemStatus.Done;
        item.StatusText = text;
        log.Ok($"{item.Title}: {text}");
    }

    private static string StoredPathFor(PlanItem item, ItemKind kind)
    {
        var name = Fmt.SafeName(item.Title);
        if (item.Section == Section.Files)
        {
            if (item.Id.StartsWith("files.c.")) return $"{Package.FilesDir}/Диск C/{name}";
            if (item.Id == "files.profile.root") return $"{Package.FilesDir}/Папка пользователя/(файлы)";
            if (item.Id.StartsWith("files.profile.")) return $"{Package.FilesDir}/Папка пользователя/{Fmt.SafeName(item.Id["files.profile.".Length..])}";
            if (item.Id.StartsWith("files.custom.")) return $"{Package.FilesDir}/Другие папки/{name}";
            return $"{Package.FilesDir}/{name}";
        }
        var dir = Package.SectionDir(item.Section);
        return kind == ItemKind.Archive ? $"{dir}/{name}.zip" : $"{dir}/{name}";
    }

    private static string UniqueStored(string wanted, HashSet<string> used)
    {
        if (!used.Contains(wanted)) return wanted;
        var ext = wanted.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : "";
        var stem = ext.Length > 0 ? wanted[..^4] : wanted;
        for (int n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    // ───────────────────────────── Особые пункты ─────────────────────────────

    private async Task CollectWifiAsync(PlanItem item, string root, PackageManifest manifest, CancellationToken ct)
    {
        var dir = Package.Abs(root, Package.WifiDir);
        var r = await Wifi.ExportAsync(dir, ct).ConfigureAwait(false);
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.xml") : [];
        manifest.WifiProfiles = files.Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList();
        if (files.Length == 0)
        {
            item.Status = ItemStatus.Warning;
            item.StatusText = "Сети не сохранились: " + r.All.Trim();
            return;
        }
        bool keys = files.Any(f => File.ReadAllText(f).Contains("<keyMaterial>", StringComparison.OrdinalIgnoreCase));
        if (!keys && !Native.IsAdmin())
        {
            item.Status = ItemStatus.Warning;
            item.StatusText = "Сети сохранены без паролей — для паролей нужны права администратора";
            return;
        }
        Done(item, Fmt.Count(files.Length, "сеть", "сети", "сетей"));
    }

    private async Task CollectDriversAsync(PlanItem item, string root, CollectOptions opt, PackageManifest manifest, ScanResult scan,
        WalkStats walk, CancellationToken ct)
    {
        int copied = 0;
        var failed = new List<string>();
        manifest.Drivers = scan.Drivers.Select(d => new DriverRecord
        {
            Inf = d.Inf, Package = d.Package, Device = d.Device, Class = d.Class, Provider = d.Provider, Version = d.Version, Bytes = d.Bytes,
        }).ToList();
        foreach (var child in item.Children.Where(c => c.Selected))
        {
            ct.ThrowIfCancellationRequested();
            if (child.Tag is not DriverRecord d) continue;
            var src = Path.Combine(Drivers.FileRepository, d.Package);
            if (!Directory.Exists(src)) continue;
            state.CurrentFile = d.Device;
            var o = await FileCopier.MirrorAsync(src, Path.Combine(Package.Abs(root, Package.DriversDir), d.Package), GlobSet.Empty, false,
                opt.SplitSize, state, walk, ct).ConfigureAwait(false);
            if (o.DiskFull)
            {
                DiskFull = true;
                break;
            }
            if (o.Failed.Count > 0) failed.Add(d.Device);
            else
            {
                copied++;
                var rec = manifest.Drivers.FirstOrDefault(x => x.Inf == d.Inf);
                if (rec is not null) rec.Stored = true;
            }
        }
        if (failed.Count > 0)
        {
            item.Status = ItemStatus.Warning;
            item.StatusText = $"Сохранено {copied}, не удалось: {string.Join(", ", failed.Take(3))}";
        }
        else Done(item, Fmt.Count(copied, "драйвер", "драйвера", "драйверов"));
    }

    private void CollectFonts(PlanItem item, string root, PackageManifest manifest, ScanResult scan)
    {
        var dir = Package.Abs(root, Package.FontsDir);
        Directory.CreateDirectory(dir);
        manifest.Fonts.Clear();
        foreach (var f in scan.Fonts)
        {
            try
            {
                var name = Path.GetFileName(f.Path);
                var dst = Path.Combine(dir, name);
                if (!FileCopier.SameFile(dst, f.Bytes, File.GetLastWriteTimeUtc(f.Path))) File.Copy(f.Path, dst, overwrite: true);
                state.Add(f.Bytes);
                manifest.Fonts.Add(new FontRecord { Name = f.Name, File = name, PerUser = f.PerUser });
            }
            catch (Exception ex)
            {
                log.Warn($"Шрифт {f.Name}: {ex.Message}");
            }
        }
        Done(item, Fmt.Count(manifest.Fonts.Count, "шрифт", "шрифта", "шрифтов"));
    }

    private async Task CollectInstallersAsync(PlanItem item, string root, ScanResult scan, CancellationToken ct)
    {
        var dir = Package.Abs(root, Package.InstallersDir);
        int ok = 0;
        var failed = new List<string>();
        foreach (var child in item.Children.Where(c => c.Selected))
        {
            ct.ThrowIfCancellationRequested();
            if (child.Tag is not InstallerDef def) continue;
            state.CurrentFile = def.Name;
            item.StatusText = "Скачиваю " + def.Name + "…";
            var fileName = def.FileName();
            var r = await Downloader.GetAsync(def.Url, dir, fileName, b => state.Add(b), ct).ConfigureAwait(false);
            if (!r.Ok)
            {
                failed.Add($"{def.Name} — {r.Error}");
                log.Warn($"Установщик {def.Name}: не скачался — {r.Error}");
                continue;
            }
            ok++;
            var rel = Package.InstallersDir + "/" + fileName;
            // Прописываем установщик нужной программе; если её не было (набор «первым делом») — добавляем запись.
            var rec = scan.Programs.FirstOrDefault(p => string.Equals(p.WingetId, def.Id, StringComparison.OrdinalIgnoreCase));
            if (rec is null)
            {
                rec = new ProgramRecord { Name = def.Name, WingetId = def.Id, Source = "winget" };
                scan.Programs.Add(rec);
            }
            rec.LocalInstaller = rel;
            rec.SilentArgs = def.Silent;
            rec.LocalIsMsi = def.Type == "msi";
            log.Ok($"Установщик {def.Name}: {Fmt.Size(r.Bytes)}");
        }
        if (failed.Count > 0)
        {
            item.Status = ItemStatus.Warning;
            item.StatusText = $"Скачано {ok}, не вышло: {string.Join(", ", failed.Take(3))}";
        }
        else Done(item, Fmt.Count(ok, "установщик", "установщика", "установщиков"));
    }

    private async Task CollectBitLockerAsync(PlanItem item, string root, ScanResult scan, PackageManifest manifest, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ключи восстановления BitLocker. Без них новая Windows не откроет эти диски.");
        sb.AppendLine("Это секрет: никому не показывай, перепиши на бумагу или сохрани в аккаунте Microsoft.");
        sb.AppendLine();
        int ok = 0;
        foreach (var v in scan.BitLocker)
        {
            var key = await BitLocker.RecoveryKeyAsync(v.Drive, ct).ConfigureAwait(false);
            sb.AppendLine($"{v.Drive}  {key ?? "не удалось получить — нужны права администратора"}");
            if (key is not null) ok++;
        }
        File.WriteAllText(Path.Combine(root, Package.BitLockerName), sb.ToString(), new UTF8Encoding(true));
        if (ok == scan.BitLocker.Count) Done(item, $"Ключи сохранены в «{Package.BitLockerName}»");
        else
        {
            item.Status = ItemStatus.Warning;
            item.StatusText = "Не все ключи получены — запусти программу от администратора";
        }
    }

    private static List<string> BuildReminders(IReadOnlyList<PlanItem> items, ScanResult scan)
    {
        var list = new List<string>(scan.Reminders);
        foreach (var i in items.Where(i => i.Id.StartsWith("important.passwords.")))
            list.Add($"{i.Title["Сохрани пароли из ".Length..]}: войди в аккаунт для синхронизации или импортируй пароли из файла, который выгрузил на старой винде.");
        if (scan.OemKey is not null) list.Add($"Ключ Windows из BIOS: {scan.OemKey} (обычно он подставляется сам).");
        var steamOnC = scan.SteamGames.Where(g => g.Library.StartsWith(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\", StringComparison.OrdinalIgnoreCase)).ToList();
        if (steamOnC.Count > 0)
            list.Add($"Steam скачает заново игры с диска C — {string.Join(", ", steamOnC.Select(g => g.Name))}.");
        if (scan.BitLocker.Count > 0)
            list.Add($"Диски {string.Join(", ", scan.BitLocker.Select(b => b.Drive))} зашифрованы BitLocker: при первом открытии введи ключ из файла «{Package.BitLockerName}».");
        return list.Distinct().ToList();
    }

    // ───────────────────────────── Сама программа ─────────────────────────────

    /// <summary>Программа кладёт себя в сборку: на новой винде её не нужно искать и скачивать.</summary>
    private void CopySelf(string root, string? selfExe)
    {
        try
        {
            var src = selfExe ?? SingleFileExe();
            if (src is null)
            {
                log.Info("Программа запущена не одним файлом (сборка для разработки) — себя в сборку не копирую.");
                return;
            }
            var dst = Path.Combine(root, Package.ExeName);
            if (Path.GetFullPath(src).Equals(Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return;
            var fi = new FileInfo(src);
            if (FileCopier.SameFile(dst, fi.Length, fi.LastWriteTimeUtc)) return;
            File.Copy(src, dst, overwrite: true);
            log.Ok($"Программа скопирована: {dst}");
        }
        catch (Exception ex)
        {
            log.Warn("Не удалось скопировать программу в сборку: " + ex.Message);
        }
    }

    public static string? SingleFileExe()
    {
        // У приложения «одним файлом» у сборок нет пути на диске.
        bool singleFile = string.IsNullOrEmpty(typeof(Collector).Assembly.Location);
        return singleFile ? Environment.ProcessPath : null;
    }

    private static void WriteReadme(string root)
    {
        var text = $"""
            UPDATER — вещи со старой Windows
            ================================

            Как вернуть всё на новой винде
            1. Установи Windows и войди в систему.
            2. Вставь эту флешку (или открой этот диск) и запусти «{Package.ExeName}».
            3. Программа сама найдёт сборку и покажет, что вернуть: драйверы, программы, файлы,
               настройки программ, сохранения игр и настройки Windows. Нажми «Начать переезд».
               Если интернета ещё нет — программы из «{Package.InstallersDir}» встанут прямо с флешки.

            Если программа не запускается — всё можно забрать руками:
            • {Package.FilesDir}      — твои папки как есть (Рабочий стол, Документы, Видео…).
            • {Package.AppsDir}  — настройки программ, по zip-архиву на программу; установщики — в подпапке.
            • {Package.GamesDir}        — сохранения игр, тоже zip-архивы.
            • {Package.WindowsDir}     — обои, драйверы, сети Wi-Fi, шрифты.
            • {Package.ReportName}  — что лежит в сборке и что сделать после переустановки.

            Большие файлы на флешке с FAT32 разрезаны на части «имя.__part001», «имя.__part002»…
            Программа склеит их сама. Руками — командой: copy /b "имя.__part001"+"имя.__part002" "имя"

            ВАЖНО: здесь могут лежать входы в Telegram, ключи SSH, пароли Wi-Fi и ключи BitLocker.
            Не давай эту флешку чужим и удали сборку, когда переезд закончится.
            """;
        File.WriteAllText(Path.Combine(root, Package.ReadmeName), text, new UTF8Encoding(true));
    }
}
