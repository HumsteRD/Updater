using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Pereezd.Core;

namespace Pereezd.App;

/// <summary>Общая часть экранов с прогрессом: таймер, скорость, журнал.</summary>
public abstract class JobVMBase : Observable
{
    protected readonly Shell Shell;
    protected readonly DispatcherTimer Timer;
    protected readonly CancellationTokenSource Cts = new();
    public ObservableCollection<string> LogLines { get; } = [];

    protected JobVMBase(Shell shell)
    {
        Shell = shell;
        Timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Application.Current.Dispatcher);
    }

    protected abstract void Tick();

    protected void DrainLog()
    {
        while (Shell.Log.Pending.TryDequeue(out var line))
        {
            LogLines.Add(line.ToString());
            if (LogLines.Count > 300) LogLines.RemoveAt(0);
        }
    }

    private bool _paused;
    public bool Paused { get => _paused; protected set { if (Set(ref _paused, value)) Raise(nameof(PauseText)); } }
    public string PauseText => Paused ? "Продолжить" : "Пауза";

    private bool _showLog;
    public bool ShowLog { get => _showLog; set => Set(ref _showLog, value); }
}

public sealed class CollectJobVM : JobVMBase
{
    private readonly IReadOnlyList<PlanItem> _all;
    private readonly ScanResult _scan;
    private readonly DriveOption _dest;
    private readonly SpeedMeter _speed = new();
    public JobState State { get; } = new();
    public ObservableCollection<PlanItem> Items { get; }
    public string Destination { get; }

    public CollectJobVM(Shell shell, IReadOnlyList<PlanItem> all, ScanResult scan, DriveOption dest) : base(shell)
    {
        _all = all;
        _scan = scan;
        _dest = dest;
        Destination = dest.Destination;
        Items = new(all.Where(i => i.Selected && i.Kind != ItemKind.Note && i.Kind != ItemKind.Programs)
            .OrderBy(i => i.Kind == ItemKind.Folder ? 1 : 0));
        foreach (var i in Items)
        {
            i.Status = ItemStatus.Pending;
            i.StatusText = i.SizeText;
        }
    }

    private string _percent = "0%";
    public string Percent { get => _percent; private set => Set(ref _percent, value); }

    private double _fraction;
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }

    private string _doneText = "";
    public string DoneText { get => _doneText; private set => Set(ref _doneText, value); }

    private string _speedText = "";
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }

    private string _current = "Готовлюсь…";
    public string Current { get => _current; private set => Set(ref _current, value); }

    public void Run()
    {
        Timer.Start();
        var collector = new Collector(Shell.Paths, Shell.Log, State);
        var opt = new CollectOptions
        {
            PackageRoot = _dest.Destination,
            SplitSize = _dest.IsFat32 ? FileCopier.Fat32Split : long.MaxValue,
        };
        _ = Task.Run(async () =>
        {
            PackageManifest? manifest = null;
            bool cancelled = false;
            Exception? error = null;
            try
            {
                manifest = await collector.RunAsync(_all, _scan, opt, Cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                error = ex;
                Shell.Log.Error("Сборы прерваны: " + ex.Message);
            }
            finally
            {
                Shell.Log.Detach();
            }
            manifest ??= Package.TryLoad(_dest.Destination);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Timer.Stop();
                Shell.Go(new CollectDoneVM(Shell, _dest.Destination, manifest, cancelled, collector.DiskFull, error?.Message, _all, _scan),
                    "Сборы · готово");
            });
        });
    }

    protected override void Tick()
    {
        _speed.Sample(State.Done);
        Fraction = State.Fraction;
        Percent = $"{Math.Floor(State.Fraction * 100)}%";
        DoneText = $"{Fmt.Size(State.Done)} из {Fmt.Size(State.Total)}";
        var eta = _speed.Eta(State.Total - State.Done);
        SpeedText = State.Paused ? "на паузе"
            : _speed.BytesPerSecond > 0 ? $"{Fmt.Speed(_speed.BytesPerSecond)}{(eta is { } e ? " · осталось " + Fmt.Duration(e) : "")}" : "";
        var file = State.CurrentFile;
        Current = State.CurrentItem is { } item ? (string.IsNullOrEmpty(file) ? item : $"{item} — {file}") : State.Stage ?? "Готовлюсь…";
        DrainLog();
    }

    public ICommand PauseCommand => new RelayCommand(() =>
    {
        if (State.Paused) State.Resume();
        else State.Pause();
        Paused = State.Paused;
    });

    public ICommand CancelCommand => new RelayCommand(async () =>
    {
        if (!await Shell.AskAsync("Остановить сборы?",
                "Уже скопированное останется в сборке. Если запустить сборы ещё раз в ту же папку, программа докопирует остальное, не начиная заново.",
                "Остановить", "Продолжить сборы", danger: true))
            return;
        State.Resume();
        Cts.Cancel();
    });

    public ICommand ToggleLog => new RelayCommand(() => ShowLog = !ShowLog);

    /// <summary>Картинка «идут сборы» для снимка экрана — без настоящего копирования.</summary>
    internal void Demo()
    {
        Timer.Stop();
        State.Total = Items.Sum(i => i.Bytes);
        long target = (long)(State.Total * 0.42);
        State.Add(target);
        long acc = 0;
        foreach (var i in Items)
        {
            if (acc + i.Bytes <= target)
            {
                i.Status = ItemStatus.Done;
                i.StatusText = $"{Fmt.Files(i.Files)}, {Fmt.Size(i.Bytes)}";
            }
            else if (acc < target)
            {
                i.Status = ItemStatus.Running;
                i.StatusText = "Собираю…";
                State.CurrentItem = i.Title;
                State.CurrentFile = "2026-10-31 19-42-07.mkv";
            }
            acc += i.Bytes;
        }
        Tick();
        SpeedText = "86,4 МБ/с · осталось 7 мин";
    }
}
