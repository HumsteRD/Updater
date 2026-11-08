namespace Pereezd.Core;

/// <summary>
/// Состояние долгой работы для окна: счётчики меняются из рабочих потоков, окно раз в четверть секунды их читает.
/// Пауза — честная: копирование останавливается между кусками файла.
/// </summary>
public sealed class JobState
{
    private long _total, _done, _filesDone, _failed;
    private readonly ManualResetEventSlim _running = new(true);

    public long Total { get => Interlocked.Read(ref _total); set => Interlocked.Exchange(ref _total, value); }
    public long Done => Interlocked.Read(ref _done);
    public long FilesDone => Interlocked.Read(ref _filesDone);
    public long Failed => Interlocked.Read(ref _failed);

    public volatile string? Stage;
    public volatile string? CurrentItem;
    public volatile string? CurrentFile;

    public void Add(long bytes) => Interlocked.Add(ref _done, bytes);
    public void FileDone() => Interlocked.Increment(ref _filesDone);
    public void Fail() => Interlocked.Increment(ref _failed);

    public bool Paused => !_running.IsSet;
    public void Pause() => _running.Reset();
    public void Resume() => _running.Set();

    public void WaitIfPaused(CancellationToken ct)
    {
        if (_running.IsSet) return;
        _running.Wait(ct);
    }

    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>Скорость и оставшееся время по последним секундам, а не по всему времени (флешки «устают»).</summary>
public sealed class SpeedMeter
{
    private readonly Queue<(DateTime At, long Done)> _samples = new();
    private readonly TimeSpan _window;

    public SpeedMeter(TimeSpan? window = null) => _window = window ?? TimeSpan.FromSeconds(6);

    public double BytesPerSecond { get; private set; }

    public void Sample(long done)
    {
        var now = DateTime.UtcNow;
        _samples.Enqueue((now, done));
        while (_samples.Count > 2 && now - _samples.Peek().At > _window) _samples.Dequeue();
        var first = _samples.Peek();
        var secs = (now - first.At).TotalSeconds;
        BytesPerSecond = secs > 0.5 ? Math.Max(0, (done - first.Done) / secs) : BytesPerSecond;
    }

    public TimeSpan? Eta(long remaining) =>
        BytesPerSecond > 1024 && remaining > 0 ? TimeSpan.FromSeconds(remaining / BytesPerSecond) : null;

    public void Reset()
    {
        _samples.Clear();
        BytesPerSecond = 0;
    }
}
