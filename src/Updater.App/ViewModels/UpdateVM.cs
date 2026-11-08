using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Updater.Core;

namespace Updater.App;

/// <summary>Экран обновления: скачиваю новую версию, проверяю и перезапускаюсь. Можно пропустить.</summary>
public sealed class UpdateVM : Observable
{
    private readonly Shell _shell;
    private readonly SelfUpdater.Release _release;
    private readonly string? _exe;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource _cts = new();
    private long _done;

    /// <param name="exe">Путь к своему exe; null — только показать экран (снимки вёрстки).</param>
    public UpdateVM(Shell shell, SelfUpdater.Release release, string? exe)
    {
        _shell = shell;
        _release = release;
        _exe = exe;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Tick(), Application.Current.Dispatcher);
        if (exe is not null) _ = RunAsync(exe);
    }

    public string FromVersion => SelfUpdater.Current.ToString(3);
    public string ToVersion => _release.Version.ToString(3);
    public string SizeText => Fmt.Size(_release.ExeSize);
    public string WhatsNew { get; private init; } = "";
    public bool HasWhatsNew => WhatsNew.Length > 0;

    private string _status = "Готовлюсь…";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private double _fraction;
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }

    private string _doneText = "";
    public string DoneText { get => _doneText; private set => Set(ref _doneText, value); }

    private string? _error;
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError), nameof(SkipText)); } }
    public bool HasError => Error is not null;

    private bool _working;
    public bool Working { get => _working; private set { if (Set(ref _working, value)) Raise(nameof(SkipText)); } }

    public string SkipText => HasError ? "Продолжить без обновления" : "Пропустить";

    public ICommand Skip => new RelayCommand(() =>
    {
        _cts.Cancel();
        _timer.Stop();
        // Новая версия никуда не денется: на главном экране останется ссылка «обновить».
        _shell.GoHome();
    });

    public ICommand Retry => new RelayCommand(() =>
    {
        if (_exe is null || Working) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_exe);
    });

    public ICommand OpenPage => new RelayCommand(() => Proc.OpenAsUser(_release.PageUrl));

    private async Task RunAsync(string exe)
    {
        Error = null;
        Working = true;
        Interlocked.Exchange(ref _done, 0);
        Status = "Скачиваю новую версию";
        _timer.Start();
        try
        {
            var path = await SelfUpdater.DownloadAsync(_release, exe, n => Interlocked.Add(ref _done, n), _cts.Token);
            _timer.Stop();
            Tick();
            Status = "Проверил файл. Перезапускаюсь…";
            _shell.Log.Ok($"Обновление {FromVersion} → {ToVersion}: скачано и проверено");
            SelfUpdater.Note($"{FromVersion} → {ToVersion}: скачано и проверено ({exe})");
            await Task.Delay(400);
            if (_cts.IsCancellationRequested) return;
            // Замена может подождать, пока антивирус отпустит новый файл, — не держим окно.
            await Task.Run(() => SelfUpdater.Apply(exe, path));
            SelfUpdater.Note($"{FromVersion} → {ToVersion}: файл заменён, перезапуск");
            if (!App.RestartAfterUpdate(exe))
            {
                Error = $"Новая версия {ToVersion} уже на месте, но перезапуститься не вышло. Закрой программу и открой заново.";
                SelfUpdater.Note($"{FromVersion} → {ToVersion}: не удалось перезапуститься");
            }
        }
        catch (OperationCanceledException)
        {
            FileCopier.TryDelete(exe + SelfUpdater.DownloadSuffix + FileCopier.TempSuffix);
        }
        catch (Exception ex)
        {
            Error = ex is UpdateException ? ex.Message : "Не получилось обновиться: " + ex.Message;
            _shell.Log.Warn("Обновление: " + Error);
            SelfUpdater.Note($"{FromVersion} → {ToVersion}: ошибка — {Error}");
        }
        finally
        {
            _timer.Stop();
            Working = false;
        }
    }

    private void Tick()
    {
        long done = Math.Min(Interlocked.Read(ref _done), _release.ExeSize);
        Fraction = _release.ExeSize > 0 ? (double)done / _release.ExeSize : 0;
        DoneText = $"{Fmt.Size(done)} из {Fmt.Size(_release.ExeSize)}";
    }

    /// <summary>Для снимков вёрстки: середина загрузки или ошибка.</summary>
    public void Demo(bool error)
    {
        Interlocked.Exchange(ref _done, (long)(_release.ExeSize * 0.42));
        Tick();
        Status = "Скачиваю новую версию";
        if (error) Error = "Контрольная сумма не совпала — файл повреждён при загрузке";
    }

    public static UpdateVM Create(Shell shell, SelfUpdater.Release release, string? exe) =>
        new(shell, release, exe) { WhatsNew = SelfUpdater.SummarizeNotes(release.Notes) };
}
