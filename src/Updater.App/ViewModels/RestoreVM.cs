using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Updater.Core;

namespace Updater.App;

public sealed class SettingOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public override string ToString() => Label;
}

/// <summary>Одна настройка Windows на новой винде: было → будет.</summary>
public sealed class SettingRowVM : Observable
{
    public WinSettingDef Def { get; }
    public CapturedSetting? Captured { get; }
    public List<SettingOption> Options { get; } = [];
    public string Title => Def.Title;
    public string? Hint => Def.Hint;
    public bool NeedsAdmin { get; }
    public string? CapturedOption { get; }

    public SettingRowVM(WinSettingDef def, CapturedSetting? captured, bool isAdmin)
    {
        Def = def;
        Captured = captured;
        NeedsAdmin = def.Admin && !isAdmin;
        Options.Add(new SettingOption { Id = SettingChoice.Keep, Label = "Не менять" });

        if (!def.IsRaw && !def.IsSpecial)
        {
            CapturedOption = captured?.Option;
            foreach (var o in def.Options)
                Options.Add(new SettingOption { Id = o.Id, Label = o.Id == CapturedOption ? o.Label + "  · как было" : o.Label });
        }
        else
        {
            var special = WindowsSettings.SpecialOptions(def);
            if (def.Handler == "classicmenu")
            {
                CapturedOption = captured?.Option;
                foreach (var (id, label) in special)
                    Options.Add(new SettingOption { Id = id, Label = id == CapturedOption ? label + "  · как было" : label });
            }
            else
            {
                if (captured?.Option is not null)
                {
                    CapturedOption = SettingChoice.Old;
                    Options.Add(new SettingOption { Id = SettingChoice.Old, Label = "Как было: " + Short(captured.Summary) });
                }
                foreach (var (id, label) in special) Options.Add(new SettingOption { Id = id, Label = label });
            }
        }
        WasText = captured?.Summary is { Length: > 0 } s ? "было: " + Short(s, 60) : "на старом ПК не прочиталось";
        _selected = Options[0];
    }

    private static string Short(string? s, int max = 42) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..(max - 1)] + "…";

    private SettingOption _selected;
    public SettingOption Selected
    {
        get => _selected;
        set
        {
            if (value is not null && Set(ref _selected, value)) Raise(nameof(Changes));
        }
    }

    public bool Changes => Selected.Id != SettingChoice.Keep;
    public string WasText { get; }

    private string? _nowText;
    public string? NowText { get => _nowText; set => Set(ref _nowText, value); }

    /// <summary>Сейчас на этой винде уже так же, как было, — менять нечего.</summary>
    public bool AlreadyAsWas { get; set; }

    public void SelectWas()
    {
        if (CapturedOption is null || AlreadyAsWas) Selected = Options[0];
        else Selected = Options.FirstOrDefault(o => o.Id == CapturedOption) ?? Options[0];
    }

    public void SelectRecommended()
    {
        if (Def.Recommended is { } rec && Options.FirstOrDefault(o => o.Id == rec) is { } r) Selected = r;
        else SelectWas();
    }
}

public sealed class SettingGroupVM
{
    public required string Title { get; init; }
    public List<SettingRowVM> Rows { get; } = [];
}

/// <summary>Экран распаковки на новой винде: что вернуть и как настроить систему.</summary>
public sealed class RestoreVM : Observable
{
    private readonly Shell _shell;
    private readonly DispatcherTimer _timer;
    private bool _dirty = true;

    public string Root { get; }
    public PackageManifest Manifest { get; }
    public List<PlanItem> Plan { get; }
    public ObservableCollection<SectionVM> Sections { get; } = [];
    public ObservableCollection<SettingGroupVM> SettingGroups { get; } = [];
    public ObservableCollection<CheckLine> Warnings { get; } = [];
    public List<SettingRowVM> SettingRows { get; } = [];

    public string Header { get; }
    public string SubHeader { get; }

    public RestoreVM(Shell shell, string root, PackageManifest manifest)
    {
        _shell = shell;
        Root = root;
        Manifest = manifest;
        Plan = RestorePlan.Build(manifest, root, shell.Paths);

        Header = "Возвращаем всё на место";
        SubHeader = $"Сборка с {manifest.Source.Computer} от {Fmt.Date(manifest.Finished ?? manifest.Created)} · {manifest.Source.Windows}";

        var programs = new SectionVM(Section.Programs, "Программы", "") { Hint = "Поставятся сами, тихо и по очереди, пока копируются файлы." };
        var files = new SectionVM(Section.Files, "Файлы", "") { Hint = "Папки вернутся на свои места. Если файл на новой винде новее — он останется." };
        var apps = new SectionVM(Section.Apps, "Настройки программ", "") { Hint = "Ложатся до первого запуска программ: откроются такими, какими были." };
        var games = new SectionVM(Section.Games, "Сохранения игр", "") { Hint = "Сейвы и настройки игр." };
        var windows = new SectionVM(Section.Windows, "Драйверы и Windows", "") { Hint = "Драйверы ставятся первыми, чтобы сразу заработали сеть и звук." };
        var settings = new SectionVM(Section.Windows, "Настройки Windows", "") { Hint = "Выбери, что вернуть как было, а что настроить по-новому.", IsSettings = true };
        var check = new SectionVM(Section.Important, "Перед стартом", "") { Hint = "Что стоит знать до начала.", IsWarnings = true };
        foreach (var s in new[] { programs, files, apps, games, windows, settings, check }) Sections.Add(s);

        foreach (var item in Plan)
        {
            var target = item.Kind switch
            {
                ItemKind.Programs or ItemKind.Note when item.Section == Section.Programs => programs,
                ItemKind.WindowsSettings => null,
                _ => item.Section switch
                {
                    Section.Files => files,
                    Section.Apps => apps,
                    Section.Games => games,
                    _ => windows,
                },
            };
            target?.Items.Add(item);
            item.PropertyChanged += (_, _) => _dirty = true;
            foreach (var c in item.Children) c.PropertyChanged += (_, _) => _dirty = true;
        }

        BuildSettings();
        BuildWarnings();
        _selected = Sections.First(s => s.Items.Count > 0 || s.IsSettings);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            if (_dirty) Recalc();
        }, Application.Current.Dispatcher);
        _timer.Start();
        Recalc();
        Ready = Task.WhenAll(LoadCurrentSettingsAsync(), CheckWingetAsync());
    }

    /// <summary>Текущие значения настроек прочитаны, наличие winget проверено.</summary>
    public Task Ready { get; }

    private SectionVM? _selected;
    public SectionVM? SelectedSection { get => _selected; set => Set(ref _selected, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    // ───────────────────────────── Настройки Windows ─────────────────────────────

    private void BuildSettings()
    {
        var snap = Manifest.Windows;
        int build = Native.WindowsBuild;
        foreach (var group in WinSettingsCatalog.Shared.Settings.Where(d => d.AppliesTo(build)).GroupBy(d => d.Group))
        {
            var g = new SettingGroupVM { Title = group.Key };
            foreach (var d in group)
            {
                var captured = snap?.Settings.GetValueOrDefault(d.Id);
                var row = new SettingRowVM(d, captured, _shell.IsAdmin);
                row.PropertyChanged += (_, _) => _dirty = true;
                g.Rows.Add(row);
                SettingRows.Add(row);
            }
            SettingGroups.Add(g);
        }
    }

    private async Task LoadCurrentSettingsAsync()
    {
        var engine = new WindowsSettings(_shell.Registry, _shell.Log, _shell.Paths.IsSandbox);
        foreach (var row in SettingRows)
        {
            var now = await engine.ReadCurrentAsync(row.Def);
            row.NowText = now?.Summary is { Length: > 0 } s ? "сейчас: " + s : null;
            if (row.Def.Handler == "env" || row.Def.Handler == "languages") row.NowText = null;
            // «Как было» имеет смысл, только если сейчас по-другому.
            if (now is not null && row.Captured is not null)
            {
                if (!row.Def.IsRaw && !row.Def.IsSpecial) row.AlreadyAsWas = now.Option == row.Captured.Option;
                else if (row.Def.IsRaw) row.AlreadyAsWas = now.Values.Count == row.Captured.Values.Count &&
                                                           now.Values.Zip(row.Captured.Values).All(p => p.First.SameAs(p.Second));
                else if (row.Def.Handler is "timezone") row.AlreadyAsWas = now.Extra.GetValueOrDefault("id") == row.Captured.Extra.GetValueOrDefault("id");
                else if (row.Def.Handler is "computername") row.AlreadyAsWas = string.Equals(now.Extra.GetValueOrDefault("name"), row.Captured.Extra.GetValueOrDefault("name"), StringComparison.OrdinalIgnoreCase);
                else if (row.Def.Handler is "classicmenu") row.AlreadyAsWas = now.Option == row.Captured.Option;
                else if (row.Def.Handler is "power") row.AlreadyAsWas = now.Extra.GetValueOrDefault("guid") == row.Captured.Extra.GetValueOrDefault("guid");
                else if (row.Def.Handler is "wallpaper") row.AlreadyAsWas = SameWallpaper(row.Captured);
            }
            if (!row.NeedsAdmin && row.Def.Handler != "computername") row.SelectWas();
        }
        _dirty = true;
    }

    /// <summary>Те же обои, что в сборке, уже стоят (сравнение по содержимому: имя у Windows часто служебное).</summary>
    private bool SameWallpaper(CapturedSetting captured)
    {
        try
        {
            if (!captured.Extra.TryGetValue("file", out var f)) return false;
            var packaged = Path.Combine(Root, Package.WindowsDir, f);
            if (!File.Exists(packaged)) return false;
            var current = _shell.Registry.Read(@"HKCU\Control Panel\Desktop", "WallPaper").Value;
            var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
            var want = new FileInfo(packaged);
            foreach (var candidate in new[] { current, transcoded })
            {
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate)) continue;
                if (new FileInfo(candidate).Length != want.Length) continue;
                if (File.ReadAllBytes(candidate).AsSpan().SequenceEqual(File.ReadAllBytes(packaged))) return true;
            }
        }
        catch { }
        return false;
    }

    // Имя компьютера — только ручным выбором: переименование требует перезагрузки и может столкнуться
    // со старым ПК в той же сети, если тот ещё жив.
    private IEnumerable<SettingRowVM> BulkRows => SettingRows.Where(r => !r.NeedsAdmin && r.Def.Handler != "computername");
    public ICommand AllAsWas => new RelayCommand(() => { foreach (var r in BulkRows) r.SelectWas(); });
    public ICommand AllRecommended => new RelayCommand(() => { foreach (var r in BulkRows) r.SelectRecommended(); });
    public ICommand NoneSettings => new RelayCommand(() => { foreach (var r in SettingRows) r.Selected = r.Options[0]; });

    // ───────────────────────────── Предупреждения ─────────────────────────────

    private void BuildWarnings()
    {
        if (!_shell.IsAdmin)
            Warnings.Add(new CheckLine
            {
                Text = _shell.IsStandardUser ? "Обычная учётная запись, без прав администратора" : "Нет прав администратора",
                Detail = _shell.IsStandardUser
                    ? "Файлы и настройки программ вернутся тебе. Драйверы, сети Wi-Fi и часть настроек Windows без прав не встанут, а программы попросят пароль администратора."
                    : "Без них не встанут драйверы, сети Wi-Fi и часть настроек, а каждая программа спросит разрешение отдельно.",
                ActionText = _shell.CanElevate ? "Перезапустить с правами" : null,
                Action = _shell.CanElevate ? new RelayCommand(Shell.RestartElevated) : null,
                Glyph = "",
                Warn = true,
            });
        if (!Manifest.IsComplete)
            Warnings.Add(new CheckLine
            {
                Text = "Сборы не были закончены",
                Detail = "Верну то, что успело собраться. Пункты, собранные не полностью, помечены.",
                Glyph = "",
                Warn = true,
            });
        var oneDrive = Plan.Where(i => i.Warning == "В OneDrive").Select(i => i.Title).ToList();
        if (oneDrive.Count > 0)
            Warnings.Add(new CheckLine
            {
                Text = $"{string.Join(", ", oneDrive)} — в OneDrive",
                Detail = "На этой винде эти папки синхронизируются с облаком: файлы начнут загружаться в OneDrive (бесплатно — 5 ГБ). " +
                         "Если не нужно — отключи резервное копирование папок в настройках OneDrive до начала.",
                Glyph = "",
                Warn = true,
            });
        if (Manifest.Source.Computer == _shell.ComputerName && Manifest.Source.Profile == _shell.Paths.Get("Profile"))
            Warnings.Add(new CheckLine
            {
                Text = "Сборка с этого же компьютера и профиля",
                Detail = "Если Windows ещё не переустановлена, распаковывать не нужно — всё и так на месте.",
                Glyph = "",
            });
        Warnings.Add(new CheckLine
        {
            Text = "Не запускай программы, пока идёт переезд",
            Detail = "Их настройки ложатся прямо сейчас. Если программа уже открыта — закрою её перед тем, как вернуть настройки.",
            Glyph = "",
        });
    }

    private bool? _hasWinget;
    private async Task CheckWingetAsync()
    {
        var v = await Winget.VersionAsync();
        _hasWinget = v is not null;
        if (v is null)
            await Application.Current.Dispatcher.InvokeAsync(() => Warnings.Insert(0, new CheckLine
            {
                Text = "winget не найден",
                Detail = "Без него программы не встанут сами. Перед установкой программ попробую включить его, а если не выйдет — скачаю с GitHub Microsoft (нужен интернет).",
                Glyph = "",
                Warn = true,
            }));
        _dirty = true;
    }

    // ───────────────────────────── Итог и старт ─────────────────────────────

    private void Recalc()
    {
        _dirty = false;
        int programs = RestorePlan.SelectedPrograms(Plan).Count;
        long bytes = Plan.Where(i => i.Selected && i.Stored is not null && i.Selectable).Sum(i => i.Bytes);
        int settings = SettingRows.Count(r => r.Changes);
        Summary = $"Поставлю {Fmt.Count(programs, "программу", "программы", "программ")}, верну {Fmt.Size(bytes)} файлов и настроек, " +
                  $"поменяю {Fmt.Count(settings, "настройку", "настройки", "настроек")} Windows";

        foreach (var s in Sections)
        {
            if (s.IsSettings) s.Summary = settings.ToString();
            else if (s.IsWarnings)
            {
                s.Summary = Warnings.Count(w => w.Warn) is var n && n > 0 ? n.ToString() : "";
                s.Attention = Warnings.Any(w => w.Warn);
            }
            else if (s.Key == Section.Programs) s.Summary = programs.ToString();
            else
            {
                var sel = s.Items.Where(i => i.Selected && i.Selectable).ToList();
                s.Summary = sel.Count == 0 ? "—" : sel.Any(i => i.Stored is not null) ? Fmt.Size(sel.Sum(i => i.Bytes)) : sel.Count.ToString();
            }
        }
    }

    public ICommand OpenReport => new RelayCommand(() =>
    {
        var report = Path.Combine(Root, Package.ReportName);
        if (File.Exists(report)) Proc.OpenAsUser(report);
    });

    public ICommand OpenUrl => new RelayCommand(p =>
    {
        switch (p)
        {
            case SubItem { Tag: ProgramRecord rec }:
                Proc.OpenAsUser(rec.Url ?? $"https://www.google.com/search?q={Uri.EscapeDataString(rec.Name + " скачать")}");
                break;
            case SubItem { Tag: StoreAppRecord app }:
                Proc.OpenAsUser($"ms-windows-store://pdp/?PFN={app.FamilyName}");
                break;
        }
    });

    public ICommand Back => new RelayCommand(() => { _timer.Stop(); _shell.GoHome(); });

    public ICommand Start => new RelayCommand(async () =>
    {
        if (!_shell.IsAdmin && _shell.CanElevate && !_shell.Auto)
        {
            if (await _shell.AskAsync("Нужны права администратора",
                    "Без них не встанут драйверы и сети Wi-Fi, часть настроек не поменяется, а каждая программа спросит разрешение отдельно. Перезапустить с правами?",
                    "Перезапустить", "Продолжить без них"))
            {
                Shell.RestartElevated();
                return;
            }
        }
        var data = Plan.Where(i => i.Selected && i.Stored is not null && i.Kind == ItemKind.Archive).ToList();
        var running = Restorer.RunningOf(data);
        bool close = true;
        if (running.Count > 0)
            close = await _shell.AskAsync("Закрыть программы?",
                $"Сейчас запущены: {string.Join(", ", running)}. Чтобы вернуть их настройки, их нужно закрыть. Закрыть?",
                "Закрыть", "Не трогать их настройки");
        if (!close)
            foreach (var i in data.Where(i => Restorer.RunningOf([i]).Count > 0)) i.Selected = false;

        _timer.Stop();
        var choices = SettingRows.Where(r => r.Changes && !r.NeedsAdmin).Select(r => new SettingChoice(r.Def, r.Selected.Id)).ToList();
        var job = new RestoreJobVM(_shell, this, choices, close);
        _shell.Go(job, "Распаковка · переезжаем");
        job.Run();
    });
}
