using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Pereezd.Core;

namespace Pereezd.App;

/// <summary>Переезд идёт: программы и файлы двумя дорожками одновременно.</summary>
public sealed class RestoreJobVM : JobVMBase
{
    private readonly RestoreVM _plan;
    private readonly IReadOnlyList<SettingChoice> _settings;
    private readonly bool _closeApps;
    private readonly Restorer _restorer;
    private readonly SpeedMeter _speed = new();

    public ObservableCollection<SubItem> Programs { get; }
    public ObservableCollection<PlanItem> DataItems { get; }
    public IReadOnlyList<SettingChoice> Choices => _settings;
    public Restorer Restorer => _restorer;

    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Переезд закончился, и окно перешло к итогу.</summary>
    public Task Completion => _done.Task;
    public bool HasPrograms => Programs.Count > 0 || _plan.Plan.Any(i => i.Kind is ItemKind.Drivers or ItemKind.Wifi && i.Selected);

    public RestoreJobVM(Shell shell, RestoreVM plan, IReadOnlyList<SettingChoice> settings, bool closeApps) : base(shell)
    {
        _plan = plan;
        _settings = settings;
        _closeApps = closeApps;
        _restorer = new Restorer(shell.Paths, shell.Log);
        Programs = new(RestorePlan.SelectedPrograms(plan.Plan));
        foreach (var p in Programs)
        {
            p.Status = ItemStatus.Pending;
            p.StatusText = "";
        }
        DataItems = new(plan.Plan.Where(i => i.Selected && i.Selectable && (i.Stored is not null || i.Kind is ItemKind.Drivers or ItemKind.Wifi or ItemKind.Fonts)));
        foreach (var d in DataItems)
        {
            d.Status = ItemStatus.Pending;
            d.StatusText = d.SizeText;
        }
    }

    // Дорожка программ
    private double _appsFraction;
    public double AppsFraction { get => _appsFraction; private set => Set(ref _appsFraction, value); }
    private string _appsText = "";
    public string AppsText { get => _appsText; private set => Set(ref _appsText, value); }
    private string _appsCurrent = "";
    public string AppsCurrent { get => _appsCurrent; private set => Set(ref _appsCurrent, value); }

    // Дорожка файлов
    private double _dataFraction;
    public double DataFraction { get => _dataFraction; private set => Set(ref _dataFraction, value); }
    private string _dataText = "";
    public string DataText { get => _dataText; private set => Set(ref _dataText, value); }
    private string _dataCurrent = "";
    public string DataCurrent { get => _dataCurrent; private set => Set(ref _dataCurrent, value); }
    private string _speedText = "";
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }

    private string _stage = "Начинаю…";
    public string Stage { get => _stage; private set => Set(ref _stage, value); }

    public void Run()
    {
        var sandbox = Shell.Paths.IsSandbox ? Shell.Paths.SandboxRoot : null;
        var logDir = sandbox ?? Package.DataDir;
        try { Shell.Log.AttachFile(Path.Combine(logDir, $"Журнал распаковки {DateTime.Now:yyyy-MM-dd HH-mm}.txt")); } catch { }
        Timer.Start();
        var opt = new RestoreOptions
        {
            PackageRoot = _plan.Root,
            CloseRunningApps = _closeApps,
            Registry = Shell.Registry,
            WorkDir = sandbox is null ? Package.TempDir : Path.Combine(sandbox, "work"),
            SkipWinget = App.Has("--no-winget"),
        };
        _ = Task.Run(async () =>
        {
            bool cancelled = false;
            string? error = null;
            try
            {
                await _restorer.RunAsync(_plan.Plan, _settings, opt, Cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Shell.Log.Error("Переезд прерван: " + ex.Message);
            }
            finally
            {
                Shell.Log.Detach();
            }
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Timer.Stop();
                Shell.Go(new RestoreDoneVM(Shell, _plan, _restorer.Summary, cancelled, error), "Распаковка · готово");
                _done.TrySetResult();
            });
        });
    }

    protected override void Tick()
    {
        var apps = _restorer.Apps;
        var data = _restorer.Data;
        AppsFraction = apps.Fraction;
        AppsText = apps.Total == 0 ? "Нечего ставить" : $"{apps.Done} из {apps.Total}";
        AppsCurrent = apps.CurrentItem is { } ai
            ? (string.IsNullOrEmpty(apps.CurrentFile) ? ai : $"{ai} — {apps.CurrentFile}")
            : apps.Total > 0 && apps.Done >= apps.Total ? "Готово" : "";

        _speed.Sample(data.Done);
        DataFraction = data.Fraction;
        DataText = data.Total == 0 ? "Нечего копировать" : $"{Fmt.Size(data.Done)} из {Fmt.Size(data.Total)}";
        var eta = _speed.Eta(data.Total - data.Done);
        SpeedText = data.Paused ? "на паузе"
            : _speed.BytesPerSecond > 0 ? $"{Fmt.Speed(_speed.BytesPerSecond)}{(eta is { } e ? " · осталось " + Fmt.Duration(e) : "")}" : "";
        DataCurrent = data.CurrentItem is { } di
            ? (string.IsNullOrEmpty(data.CurrentFile) ? di : $"{di} — {data.CurrentFile}")
            : data.Total > 0 && data.Done >= data.Total ? "Готово" : "";
        Stage = data.Stage == "Настройки Windows" ? "Настраиваю Windows…" : "Переезжаем…";
        DrainLog();
    }

    public ICommand PauseCommand => new RelayCommand(() =>
    {
        if (_restorer.Data.Paused)
        {
            _restorer.Data.Resume();
            _restorer.Apps.Resume();
        }
        else
        {
            _restorer.Data.Pause();
            _restorer.Apps.Pause();
        }
        Paused = _restorer.Data.Paused;
    });

    public ICommand CancelCommand => new RelayCommand(async () =>
    {
        if (!await Shell.AskAsync("Остановить переезд?",
                "Уже вернувшееся останется на месте. Программа, которая ставится прямо сейчас, доустановится. Можно будет запустить распаковку ещё раз.",
                "Остановить", "Продолжить", danger: true))
            return;
        _restorer.Data.Resume();
        _restorer.Apps.Resume();
        Cts.Cancel();
    });

    public ICommand ToggleLog => new RelayCommand(() => ShowLog = !ShowLog);

    /// <summary>Картинка «идёт переезд» для снимка экрана — ничего не ставит и не копирует.</summary>
    internal void Demo()
    {
        Timer.Stop();
        int n = Programs.Count;
        int done = n * 2 / 3;
        for (int i = 0; i < n; i++)
        {
            Programs[i].Status = i < done ? ItemStatus.Done : i == done ? ItemStatus.Running : ItemStatus.Pending;
            Programs[i].StatusText = i < done ? (i % 5 == 3 ? "Уже установлено" : "Установлено") : i == done ? "Ставлю…" : "";
        }
        _restorer.Apps.Total = n;
        _restorer.Apps.Add(done);
        if (done < n)
        {
            _restorer.Apps.CurrentItem = Programs[done].Title;
            _restorer.Apps.CurrentFile = "Скачивание 42,1 МБ / 118 МБ";
        }
        _restorer.Data.Total = DataItems.Sum(i => i.Bytes);
        long target = (long)(_restorer.Data.Total * 0.55);
        _restorer.Data.Add(target);
        long acc = 0;
        foreach (var d in DataItems)
        {
            if (acc + d.Bytes <= target)
            {
                d.Status = ItemStatus.Done;
                d.StatusText = Fmt.Size(d.Bytes);
            }
            else if (acc < target)
            {
                d.Status = ItemStatus.Running;
                d.StatusText = "Возвращаю…";
                _restorer.Data.CurrentItem = d.Title;
                _restorer.Data.CurrentFile = "settings.json";
            }
            acc += d.Bytes;
        }
        Tick();
        SpeedText = "112 МБ/с · осталось 4 мин";
    }
}
