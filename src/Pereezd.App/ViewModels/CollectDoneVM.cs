using System.IO;
using System.Windows;
using System.Windows.Input;
using Pereezd.Core;

namespace Pereezd.App;

/// <summary>Строка чек-листа с необязательной кнопкой.</summary>
public sealed class CheckLine
{
    public required string Text { get; init; }
    public string? Detail { get; init; }
    public string? ActionText { get; init; }
    public ICommand? Action { get; init; }
    public string Glyph { get; init; } = "";
    public bool Warn { get; init; }
}

public sealed class Stat
{
    public required string Value { get; init; }
    public required string Label { get; init; }
}

public sealed class CollectDoneVM : Observable
{
    private readonly Shell _shell;
    private readonly string _root;

    public string Title { get; }
    public string Subtitle { get; }
    public bool Ok { get; }
    public bool HasWarning { get; }
    public string StatusGlyph => Ok ? "" : "";
    public List<Stat> Stats { get; } = [];
    public List<CheckLine> Problems { get; } = [];
    public List<CheckLine> Checklist { get; } = [];
    public string Root => _root;
    public bool HasProblems => Problems.Count > 0;

    public CollectDoneVM(Shell shell, string root, PackageManifest? m, bool cancelled, bool diskFull, string? error,
        IReadOnlyList<PlanItem> items, ScanResult scan)
    {
        _shell = shell;
        _root = root;
        Ok = !cancelled && !diskFull && error is null && m?.IsComplete == true;
        Title = Ok ? "Вещи собраны" : diskFull ? "Не хватило места" : cancelled ? "Сборы остановлены" : "Сборы не закончены";
        Subtitle = Ok
            ? $"Всё лежит в {root} — вместе с программой. После переустановки запусти оттуда «{Package.ExeName}»."
            : diskFull
                ? "Освободи место или выбери диск побольше и запусти сборы снова в ту же папку: скопированное повторно не копируется."
                : cancelled
                    ? "Скопированное осталось в сборке. Запусти сборы ещё раз в ту же папку — программа докопирует остальное."
                    : "Что-то пошло не так: " + (error ?? "подробности в журнале") + ". Запусти сборы ещё раз — скопированное не пропадёт.";

        if (m is not null)
        {
            int auto = m.Programs.Count(p => p.AutoInstallable);
            Stats.Add(new Stat { Value = Fmt.Size(m.TotalBytes), Label = "в сборке" });
            Stats.Add(new Stat { Value = m.Items.Sum(i => i.Files).ToString("N0", Fmt.Ru), Label = Fmt.Plural(m.Items.Sum(i => i.Files), "файл", "файла", "файлов") });
            Stats.Add(new Stat { Value = $"{auto} из {m.Programs.Count}", Label = "программ встанут сами" });
            Stats.Add(new Stat { Value = (m.Windows?.Settings.Count ?? 0).ToString(), Label = "настроек Windows" });

            foreach (var i in m.Items.Where(i => i.FailedCount > 0))
                Problems.Add(new CheckLine
                {
                    Text = $"{i.Title}: не удалось взять {Fmt.Count(i.FailedCount, "файл", "файла", "файлов")}",
                    Detail = string.Join("\n", i.Failed.Take(5)),
                    Glyph = "",
                    Warn = true,
                });
        }
        foreach (var i in items.Where(i => i.Status == ItemStatus.Failed))
            Problems.Add(new CheckLine { Text = $"{i.Title}: {i.StatusText}", Glyph = "", Warn = true });
        HasWarning = Problems.Count > 0;

        // Что сделать перед переустановкой.
        Checklist.Add(new CheckLine
        {
            Text = "Сделай установочную флешку Windows",
            Detail = "Утилита Microsoft Media Creation Tool. Флешку со сборкой под установщик не бери — утилита её сотрёт.",
            ActionText = "Сайт Microsoft",
            Action = new RelayCommand(() => Proc.OpenAsUser("https://www.microsoft.com/ru-ru/software-download/windows11")),
            Glyph = "",
        });
        if (!DriveIsRemovable(root))
            Checklist.Add(new CheckLine
            {
                Text = $"При установке не трогай диск {Path.GetPathRoot(root)?.TrimEnd('\\')}",
                Detail = "Ставь Windows на тот раздел, где она была (обычно C:). Остальные диски не удаляй и не форматируй.",
                Glyph = "",
                Warn = true,
            });
        foreach (var p in items.Where(i => i.Id.StartsWith("important.passwords.")))
            Checklist.Add(new CheckLine
            {
                Text = p.Title,
                Detail = "Включи синхронизацию или выгрузи пароли в файл и положи его в «Документы» — до переустановки.",
                ActionText = "Открыть пароли",
                Action = new CollectVM_PasswordsOpener(shell, p).Command,
                Glyph = "",
                Warn = true,
            });
        if (scan.BitLocker.Count > 0)
            Checklist.Add(new CheckLine
            {
                Text = $"Перепиши ключи BitLocker ({string.Join(", ", scan.BitLocker.Select(v => v.Drive))})",
                Detail = $"Они в файле «{Package.BitLockerName}» в сборке. Без них зашифрованные диски на новой винде не откроются.",
                Glyph = "",
                Warn = true,
            });
        var steamOnC = scan.SteamGames.Where(g => shell.Paths.OnSystemDrive(g.Library)).ToList();
        if (steamOnC.Count > 0)
            Checklist.Add(new CheckLine
            {
                Text = $"Steam скачает заново: {string.Join(", ", steamOnC.Select(g => g.Name))}",
                Detail = $"Это {Fmt.Size(steamOnC.Sum(g => g.Bytes))}. Чтобы не качать — перенеси игры на другой диск в Steam: Свойства → Установленные файлы → Переместить.",
                Glyph = "",
            });
        Checklist.Add(new CheckLine
        {
            Text = "Флешку со сборкой никому не давай",
            Detail = "Там входы в программы, ключи и пароли Wi-Fi. Когда переезд закончится, сборку можно удалить.",
            Glyph = "",
        });
    }

    private static bool DriveIsRemovable(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Removable; }
        catch { return false; }
    }

    public ICommand OpenFolder => new RelayCommand(() => Proc.OpenAsUser(_root));
    public ICommand OpenReport => new RelayCommand(() =>
    {
        var report = Path.Combine(_root, Package.ReportName);
        if (File.Exists(report)) Proc.OpenAsUser(report);
    });
    public ICommand Home => new RelayCommand(() =>
    {
        _shell.LoadPackage(Package.Find(_root));
        _shell.GoHome();
    });
    public ICommand Again => new RelayCommand(() => _shell.Go(new CollectVM(_shell), "Сборы · старая винда"));
    public ICommand Exit => new RelayCommand(() => Application.Current.Shutdown());
}

/// <summary>Открыть страницу паролей браузера (общая логика для экрана сборов и итога).</summary>
public sealed class CollectVM_PasswordsOpener(Shell shell, PlanItem item)
{
    public ICommand Command => new RelayCommand(() =>
    {
        if (item.PasswordsUrl is null) return;
        if (item.BrowserExe is not null && shell.Paths.TryResolve(item.BrowserExe, out var exe) && File.Exists(exe))
            Proc.RunAsUser(exe, "\"" + item.PasswordsUrl + "\"");
        else
            _ = shell.InfoAsync("Открой страницу паролей", $"Вставь в адресную строку браузера: {item.PasswordsUrl}");
    });
}
