using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Updater.Core;

namespace Updater.App;

public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can is null ? null : _ => can()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => run(parameter);
}

/// <summary>true → Visible. Параметр «!» переворачивает.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool v = value is bool b && b;
        if (p as string == "!") v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Не null и не пустая строка → Visible. Параметр «!» переворачивает.</summary>
public sealed class NotEmptyToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool has = value switch
        {
            null => false,
            string s => s.Length > 0,
            int i => i > 0,
            long l => l > 0,
            System.Collections.ICollection col => col.Count > 0,
            _ => true,
        };
        if (p as string == "!") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class StatusToGlyph : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        ItemStatus.Running => "",
        ItemStatus.Done => "",
        ItemStatus.Warning => "",
        ItemStatus.Failed => "",
        ItemStatus.Skipped => "",
        _ => "",
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class StatusToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value switch
        {
            ItemStatus.Running => "AccentInk",
            ItemStatus.Done => "Success",
            ItemStatus.Warning => "Warning",
            ItemStatus.Failed => "Critical",
            _ => "TextTertiary",
        };
        return Application.Current.Resources[key] as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Доля 0..1 → ширина полосы (для самодельных индикаторов заполнения).</summary>
public sealed class FractionToWidth : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object? p, CultureInfo c)
    {
        if (values.Length < 2 || values[0] is not double f || values[1] is not double w) return 0.0;
        return Math.Max(0, Math.Min(1, f)) * w;
    }

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Всплывающий вопрос поверх окна в стиле программы (вместо системного MessageBox).</summary>
public sealed class DialogVM : Observable
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public required string Title { get; init; }
    public required string Text { get; init; }
    public string OkText { get; init; } = "Продолжить";
    public string? CancelText { get; init; } = "Отмена";
    public bool Danger { get; init; }

    public ICommand Ok => new RelayCommand(() => _tcs.TrySetResult(true));
    public ICommand Cancel => new RelayCommand(() => _tcs.TrySetResult(false));
    public Task<bool> Result => _tcs.Task;
}
