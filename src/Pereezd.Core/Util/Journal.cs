using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace Pereezd.Core;

public enum LogLevel { Info, Warn, Error, Ok }

public sealed record LogLine(DateTime Time, LogLevel Level, string Text)
{
    public override string ToString() =>
        $"{Time:HH:mm:ss} {(Level switch { LogLevel.Warn => "!", LogLevel.Error => "×", LogLevel.Ok => "✓", _ => "·" })} {Text}";
}

/// <summary>Журнал работы: в файл рядом со сборкой и в очередь для окна программы.</summary>
public sealed class Journal
{
    private readonly object _gate = new();
    private StreamWriter? _file;
    public ConcurrentQueue<LogLine> Pending { get; } = new();
    public List<LogLine> All { get; } = [];

    public void AttachFile(string path)
    {
        lock (_gate)
        {
            _file?.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true))
            {
                AutoFlush = true
            };
            foreach (var l in All) _file.WriteLine(l.ToString());
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    public void Write(LogLevel level, string text)
    {
        var line = new LogLine(DateTime.Now, level, text);
        lock (_gate)
        {
            All.Add(line);
            try { _file?.WriteLine(line.ToString()); } catch { }
        }
        Pending.Enqueue(line);
    }

    public void Info(string text) => Write(LogLevel.Info, text);
    public void Warn(string text) => Write(LogLevel.Warn, text);
    public void Error(string text) => Write(LogLevel.Error, text);
    public void Ok(string text) => Write(LogLevel.Ok, text);
}

/// <summary>База для объектов, к которым привязан интерфейс.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
