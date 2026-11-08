using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Updater.Core;

namespace Updater.App;

/// <summary>Раздел списка слева: «Файлы», «Программы»…</summary>
public sealed class SectionVM(Section key, string title, string glyph) : Observable
{
    public Section Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string? Hint { get; init; }
    /// <summary>Раздел с настройками Windows (своя разметка вместо списка строк).</summary>
    public bool IsSettings { get; init; }
    /// <summary>Раздел предупреждений перед стартом.</summary>
    public bool IsWarnings { get; init; }
    public bool IsItems => !IsSettings && !IsWarnings;
    public bool IsFiles => Key == Section.Files && IsItems;
    public ObservableCollection<PlanItem> Items { get; } = [];

    /// <summary>Делить список на «Главное» и «По желанию» (настройки программ).</summary>
    public bool Grouped { get; init; }

    private ListCollectionView? _view;
    /// <summary>Что показывает список: те же строки, но с группами, если раздел их делит.</summary>
    public ICollectionView View => _view ??= CreateView();

    private ListCollectionView CreateView()
    {
        var v = new ListCollectionView(Items);
        if (Grouped)
        {
            v.SortDescriptions.Add(new SortDescription(nameof(PlanItem.GroupOrder), ListSortDirection.Ascending));
            v.SortDescriptions.Add(new SortDescription(nameof(PlanItem.Order), ListSortDirection.Ascending));
            v.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PlanItem.GroupTitle)));
        }
        return v;
    }

    /// <summary>Отметить или снять всю группу разом.</summary>
    public ICommand SelectGroup => new RelayCommand(p => SetGroup(p as string, true));
    public ICommand ClearGroup => new RelayCommand(p => SetGroup(p as string, false));

    private void SetGroup(string? group, bool value)
    {
        foreach (var i in Items.Where(i => i.Selectable && i.GroupTitle == group)) i.Selected = value;
    }

    private string _summary = "";
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    private bool _attention;
    /// <summary>Есть то, на что стоит посмотреть (раздел «Важно»).</summary>
    public bool Attention { get => _attention; set => Set(ref _attention, value); }
}

/// <summary>Экран сборов: что нашлось на старой винде и куда это сложить.</summary>
public sealed class CollectVM : Observable
{
    private readonly Shell _shell;
    private readonly Scanner _scanner;
    private readonly DispatcherTimer _timer;
    private readonly List<PlanItem> _all = [];
    private bool _dirty;

    public ObservableCollection<SectionVM> Sections { get; } = [];
    public ObservableCollection<DriveOption> Drives { get; } = [];
    public Task ScanTask { get; }

    public CollectVM(Shell shell)
    {
        _shell = shell;
        _scanner = new Scanner(shell.Paths, shell.Log);
        Sections.Add(new SectionVM(Section.Files, "Файлы", "") { Hint = "Твои папки. Всё, что лежит на диске C:, переустановка сотрёт." });
        Sections.Add(new SectionVM(Section.Programs, "Программы", "") { Hint = "Список программ запишется в сборку — на новой винде они встанут сами." });
        Sections.Add(new SectionVM(Section.Apps, "Настройки программ", "") { Hint = "Чтобы программы открылись такими, какими ты их оставил: вкладки, входы, профили, сцены. Главное уже отмечено, а «по желанию» — мелочи, которые легко настроить заново.", Grouped = true });
        Sections.Add(new SectionVM(Section.Games, "Сохранения игр", "") { Hint = "Сохранения и настройки, которые игры держат на диске C:." });
        Sections.Add(new SectionVM(Section.Windows, "Windows", "") { Hint = "Настройки, драйверы, сети Wi-Fi и мелочи, которые долго настраивать заново." });
        Sections.Add(new SectionVM(Section.Important, "Перед переустановкой", "") { Hint = "Что сделать руками, пока старая винда ещё жива." });
        _selected = Sections[0];

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            if (_dirty) Recalc();
        }, Application.Current.Dispatcher);
        _timer.Start();

        RefreshDrives();
        ScanTask = ScanAsync();
    }

    private SectionVM? _selected;
    public SectionVM? SelectedSection { get => _selected; set => Set(ref _selected, value); }

    private bool _scanning = true;
    public bool Scanning
    {
        get => _scanning;
        private set
        {
            if (Set(ref _scanning, value)) Raise(nameof(CanStart));
        }
    }

    public bool CanStart => !Scanning;

    private string _stage = "Осматриваюсь…";
    public string ScanStage { get => _stage; private set => Set(ref _stage, value); }

    private DriveOption? _destination;
    public DriveOption? Destination
    {
        get => _destination;
        set
        {
            if (Set(ref _destination, value)) Recalc();
        }
    }

    private long _selectedBytes;
    public long SelectedBytes { get => _selectedBytes; private set => Set(ref _selectedBytes, value); }

    private string _capacityText = "";
    public string CapacityText { get => _capacityText; private set => Set(ref _capacityText, value); }

    private double _capacity;
    public double CapacityFraction { get => _capacity; private set => Set(ref _capacity, value); }

    private bool _over;
    public bool OverCapacity { get => _over; private set => Set(ref _over, value); }

    private string? _destNote;
    public string? DestinationNote { get => _destNote; private set => Set(ref _destNote, value); }

    public string StartText => Scanning ? "Подожди, считаю…" : $"Собрать {Fmt.Size(SelectedBytes)}";

    public bool ShowElevate => !_shell.IsAdmin && _shell.CanElevate;
    public bool ShowStandardUser => _shell.IsStandardUser;

    // ───────────────────────────── Скан ─────────────────────────────

    private async Task ScanAsync()
    {
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            await Task.Run(() => _scanner.RunAsync(
                item => dispatcher.BeginInvoke(() => Add(item)),
                stage => dispatcher.BeginInvoke(() => { if (stage.Length > 0) ScanStage = stage + "…"; })));
        }
        catch (Exception ex)
        {
            _shell.Log.Error("Осмотр не удался: " + ex.Message);
        }
        await dispatcher.InvokeAsync(() =>
        {
            Scanning = false;
            ScanStage = "";
            Recalc();
            Raise(nameof(StartText));
        });
    }

    private void Add(PlanItem item)
    {
        item.Order = _all.Count;
        _all.Add(item);
        item.PropertyChanged += OnItemChanged;
        if (!item.Hidden)
        {
            // Настройки Windows — главное в своём разделе, они идут первыми.
            if (item.Kind == ItemKind.WindowsSettings) SectionOf(item).Items.Insert(0, item);
            else SectionOf(item).Items.Add(item);
        }
        _dirty = true;
    }

    private SectionVM SectionOf(PlanItem i) => Sections.First(s => s.Key == i.Section);

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PlanItem item) return;
        if (e.PropertyName == nameof(PlanItem.Hidden))
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var list = SectionOf(item).Items;
                if (item.Hidden) list.Remove(item);
                else if (!list.Contains(item)) list.Add(item);
            });
        if (e.PropertyName is nameof(PlanItem.Selected) or nameof(PlanItem.Bytes) or nameof(PlanItem.Measured) or nameof(PlanItem.Hidden))
            _dirty = true;
    }

    private static bool Counts(PlanItem i) => i.Selected && i.Kind is ItemKind.Folder or ItemKind.Archive or ItemKind.Drivers or ItemKind.Fonts;

    private void Recalc()
    {
        _dirty = false;
        SelectedBytes = _all.Where(Counts).Sum(i => i.Bytes);
        foreach (var s in Sections)
        {
            var visible = s.Items.ToList();
            switch (s.Key)
            {
                case Section.Programs:
                    s.Summary = visible.FirstOrDefault(i => i.Kind == ItemKind.Programs)?.SizeOverride ?? "";
                    break;
                case Section.Important:
                    s.Summary = visible.Count > 0 ? visible.Count.ToString() : "";
                    s.Attention = visible.Count > 0;
                    break;
                case Section.Windows:
                    s.Summary = visible.Count(i => i.Selected).ToString();
                    break;
                default:
                    var sel = visible.Where(i => i.Selected).ToList();
                    s.Summary = sel.Count == 0 ? "—" : Fmt.Size(sel.Sum(i => i.Bytes));
                    break;
            }
        }

        if (Destination is { } d)
        {
            CapacityFraction = d.Free <= 0 ? 1 : Math.Min(1, (double)SelectedBytes / d.Free);
            OverCapacity = SelectedBytes > d.Free;
            CapacityText = $"Выбрано {Fmt.Size(SelectedBytes)} · свободно {Fmt.Size(d.Free)}";
            DestinationNote = d.IsSystem
                ? "Это системный диск — переустановка его сотрёт. Выбери флешку или другой диск."
                : d.IsFat32 ? "FAT32: файлы больше 4 ГБ разрежу на части, на новой винде склею."
                : OverCapacity ? "Не поместится — сними отметки с больших пунктов или выбери диск побольше." : null;
        }
        else
        {
            CapacityFraction = 0;
            OverCapacity = false;
            CapacityText = "Выбрано " + Fmt.Size(SelectedBytes);
            DestinationNote = "Вставь флешку или выбери папку на другом диске.";
        }
        Raise(nameof(StartText));
    }

    // ───────────────────────────── Куда ─────────────────────────────

    public ICommand RefreshDrivesCommand => new RelayCommand(RefreshDrives);

    private void RefreshDrives()
    {
        var current = Destination?.Root;
        Drives.Clear();
        foreach (var d in DriveOption.List(_shell.Paths.SystemDrive)) Drives.Add(d);
        Destination = Drives.FirstOrDefault(d => d.Root == current)
                      ?? Drives.Where(d => !d.IsSystem).OrderByDescending(d => d.Removable).ThenByDescending(d => d.Free).FirstOrDefault()
                      ?? Drives.FirstOrDefault();
    }

    public ICommand BrowseDestination => new RelayCommand(() =>
    {
        var dlg = new OpenFolderDialog { Title = "Куда сложить вещи" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var folder = dlg.FolderName;
            // Выбрали корень диска — кладём в папку «Updater», иначе прямо в выбранную.
            var opt = DriveOption.ForFolder(Path.GetPathRoot(folder) == folder ? Path.Combine(folder, Package.FolderName) : folder);
            var withSystem = new DriveOption
            {
                Root = opt.Root, Label = opt.Label, Free = opt.Free, Size = opt.Size, Format = opt.Format, Removable = opt.Removable,
                CustomFolder = opt.CustomFolder,
                IsSystem = opt.Root.Equals(_shell.Paths.SystemDrive, StringComparison.OrdinalIgnoreCase),
            };
            Drives.Add(withSystem);
            Destination = withSystem;
        }
        catch (Exception ex)
        {
            _ = _shell.InfoAsync("Не подходит", ex.Message);
        }
    });

    public ICommand AddFolder => new RelayCommand(() =>
    {
        var dlg = new OpenFolderDialog { Title = "Какую папку взять с собой", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var folder in dlg.FolderNames)
        {
            var token = _shell.Paths.Tokenize(folder);
            var name = Path.GetFileName(folder.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = folder;
            var item = new PlanItem
            {
                Id = "files.custom." + Fmt.SafeName(token).ToLowerInvariant(),
                Section = Section.Files,
                Kind = ItemKind.Folder,
                Title = name,
                Glyph = "",
                Roots = [new SourceRoot(token)],
                Selected = true,
            };
            item.Detail = folder;
            if (!_shell.Paths.OnSystemDrive(folder)) item.Note = "Лежит не на диске C: — переустановка её не тронет, но если нужно — возьму.";
            Add(item);
            SelectedSection = Sections[0];
            Task.Run(() =>
            {
                var (b, n) = FileWalker.Measure(folder);
                item.Bytes = b;
                item.Files = n;
                item.Measured = true;
            });
        }
    });

    public ICommand OpenPasswords => new RelayCommand(p =>
    {
        if (p is not PlanItem item || item.PasswordsUrl is null) return;
        // chrome://… открывается только самим браузером: запускаем его exe с адресом страницы паролей.
        if (item.BrowserExe is not null && _shell.Paths.TryResolve(item.BrowserExe, out var exe) && File.Exists(exe))
            Proc.RunAsUser(exe, "\"" + item.PasswordsUrl + "\"");
        else
            _ = _shell.InfoAsync("Открой страницу паролей", $"Вставь в адресную строку браузера: {item.PasswordsUrl}");
    });

    public ICommand Elevate => new RelayCommand(Shell.RestartElevated);
    public ICommand Back => new RelayCommand(() => { _timer.Stop(); _shell.GoHome(); });

    // ───────────────────────────── Старт ─────────────────────────────

    public ICommand Start => new RelayCommand(async () =>
    {
        if (Scanning) return;
        if (Destination is not { } dest)
        {
            await _shell.InfoAsync("Куда собирать?", "Вставь флешку или выбери папку на другом диске (кнопка «Обзор»).");
            return;
        }
        if (dest.IsSystem)
        {
            await _shell.InfoAsync("Не на диск C:", "Системный диск сотрётся при переустановке вместе со сборкой. Выбери флешку или другой диск.");
            return;
        }
        if (OverCapacity && !await _shell.AskAsync("Может не поместиться",
                $"Выбрано {Fmt.Size(SelectedBytes)}, а свободно {Fmt.Size(dest.Free)}. Сборы остановятся, когда место кончится, — " +
                "скопированное не пропадёт. Начать всё равно?", "Начать", "Вернуться"))
            return;

        var selected = _all.Where(i => i.Selected && !i.Hidden).ToList();
        var running = Restorer.RunningOf(selected.Where(i => i.Kind == ItemKind.Archive));
        if (running.Count > 0 && await _shell.AskAsync("Закрыть программы?",
                $"Сейчас запущены: {string.Join(", ", running)}. Пока программа открыта, часть её файлов занята, и их не получится взять. " +
                "Закрыть эти программы?", "Закрыть и начать", "Не закрывать"))
            await Task.Run(() => Restorer.CloseProcesses(selected.Where(i => i.Kind == ItemKind.Archive).SelectMany(i => i.Processes)));

        _timer.Stop();
        var job = new CollectJobVM(_shell, _all.Where(i => !i.Hidden).ToList(), _scanner.Result, dest);
        _shell.Go(job, "Сборы · копирую");
        job.Run();
    });

    /// <summary>Для снимков экрана: все строки, включая скрытые.</summary>
    public IReadOnlyList<PlanItem> AllItems => _all;
    public ScanResult ScanResult => _scanner.Result;
}
