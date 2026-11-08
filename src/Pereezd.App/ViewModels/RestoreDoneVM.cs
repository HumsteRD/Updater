using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Pereezd.Core;

namespace Pereezd.App;

public sealed class RestoreDoneVM : Observable
{
    private readonly Shell _shell;
    private readonly RestoreVM _plan;
    private readonly RestoreSummary _summary;

    public string Title { get; }
    public string Subtitle { get; }
    public bool Ok { get; }
    public string StatusGlyph => Ok ? "" : "";
    public List<Stat> Stats { get; } = [];
    public ObservableCollection<CheckLine> Todo { get; } = [];
    public List<CheckLine> Problems { get; } = [];
    public bool HasProblems => Problems.Count > 0;
    public bool NeedsReboot { get; }
    public bool CanRollback { get; }

    public RestoreDoneVM(Shell shell, RestoreVM plan, RestoreSummary s, bool cancelled, string? error)
    {
        _shell = shell;
        _plan = plan;
        _summary = s;
        Ok = !cancelled && error is null && s.ProgramsFailed.Count == 0 && s.FilesFailed == 0;
        Title = cancelled ? "Переезд остановлен" : error is not null ? "Переезд прерван" : "Переезд закончен";
        Subtitle = cancelled
            ? "Вернувшееся осталось на месте. Распаковку можно запустить ещё раз — уже скопированное повторно не копируется."
            : error is not null
                ? "Ошибка: " + error
                : Ok ? "Всё на своих местах. Осталось пару мелочей ниже." : "Почти всё на месте. Что не получилось — ниже, с подсказками.";

        Stats.Add(new Stat { Value = $"{s.ProgramsInstalled + s.ProgramsAlready}", Label = "программ на месте" });
        Stats.Add(new Stat { Value = Fmt.Size(s.BytesRestored), Label = "файлов вернулось" });
        Stats.Add(new Stat { Value = (s.Settings?.Applied ?? 0).ToString(), Label = "настроек Windows" });
        if (s.DriversInstalled > 0) Stats.Add(new Stat { Value = s.DriversInstalled.ToString(), Label = "драйверов" });

        NeedsReboot = s.NeedsReboot || s.Settings?.NeedsReboot == true;
        CanRollback = s.Settings is { Applied: > 0, RollbackFile: not null } && File.Exists(s.Settings.RollbackFile);

        foreach (var f in s.ProgramsFailed)
            Problems.Add(new CheckLine { Text = f, Glyph = "", Warn = true });
        foreach (var p in s.Problems)
            Problems.Add(new CheckLine { Text = p, Glyph = "", Warn = true });
        if (s.Settings is not null)
            foreach (var f in s.Settings.Failed)
                Problems.Add(new CheckLine { Text = f, Glyph = "", Warn = true });
        if (s.FilesKept > 0)
            Problems.Add(new CheckLine
            {
                Text = $"{Fmt.Count(s.FilesKept, "файл", "файла", "файлов")} не заменены: на этой винде они новее",
                Detail = "Свежие версии оставлены, старые лежат в сборке, если понадобятся.",
                Glyph = "",
            });

        // Что осталось руками.
        if (s.Settings?.NeedsRelogin == true)
            Todo.Add(new CheckLine { Text = "Выйди из учётной записи и войди снова", Detail = "Так вступят в силу сочетания клавиш для раскладки.", Glyph = "" });
        foreach (var r in plan.Manifest.Reminders)
            Todo.Add(new CheckLine { Text = r, Glyph = "" });

        // Руками — только то, у чего есть ссылка «где взять»: портативное с C: и программы с других дисков вернутся сами.
        var manual = plan.Plan.FirstOrDefault(i => i.Id == RestorePlan.ProgramsId)?.Children.Where(c => !c.Selectable && c.Link is not null).ToList() ?? [];
        foreach (var m in manual)
            if (m.Tag is ProgramRecord rec)
                Todo.Add(new CheckLine
                {
                    Text = $"Поставь {rec.Name}",
                    Detail = rec.Url is null ? "В winget её нет — найди установщик на сайте." : rec.Url,
                    ActionText = rec.Url is null ? "Найти" : "Открыть сайт",
                    Action = new RelayCommand(() => Proc.OpenAsUser(rec.Url ?? $"https://www.google.com/search?q={Uri.EscapeDataString(rec.Name + " скачать")}")),
                    Glyph = "",
                });
        foreach (var a in plan.Manifest.StoreApps)
            Todo.Add(new CheckLine
            {
                Text = $"{a.Name} из Microsoft Store",
                ActionText = "Открыть в Store",
                Action = new RelayCommand(() => Proc.OpenAsUser($"ms-windows-store://pdp/?PFN={a.FamilyName}")),
                Glyph = "",
            });
        Todo.Add(new CheckLine
        {
            Text = "Когда убедишься, что всё на месте, удали сборку с флешки",
            Detail = "В ней входы в программы, ключи и пароли Wi-Fi.",
            ActionText = "Открыть папку",
            Action = new RelayCommand(() => Proc.OpenAsUser(plan.Root)),
            Glyph = "",
        });
    }

    public ICommand Reboot => new RelayCommand(async () =>
    {
        if (await _shell.AskAsync("Перезагрузить?", "Сохрани открытые документы — компьютер перезагрузится через 10 секунд.", "Перезагрузить", "Позже"))
            await Proc.RunAsync(Proc.System32("shutdown.exe"), $"/r /t 10 /c \"{Package.AppName}: перезагрузка, чтобы всё вступило в силу\"");
    });

    public ICommand Rollback => new RelayCommand(async () =>
    {
        if (_summary.Settings?.RollbackFile is not { } file) return;
        if (!await _shell.AskAsync("Откатить настройки Windows?", "Вернуть значения, которые были на этой винде до переезда. Файлы и программы останутся.",
                "Откатить", "Отмена"))
            return;
        var n = await new WindowsSettings(_shell.Registry, _shell.Log, _shell.Paths.IsSandbox).RollbackAsync(file);
        await _shell.InfoAsync("Готово", $"Вернул {Fmt.Count(n, "значение", "значения", "значений")}.");
    });

    public ICommand OpenLog => new RelayCommand(() =>
        Proc.OpenAsUser(Package.DataDir));

    public ICommand Again => new RelayCommand(() => _shell.Go(new RestoreVM(_shell, _plan.Root, _plan.Manifest), "Распаковка · новая винда"));
    public ICommand Exit => new RelayCommand(() => Application.Current.Shutdown());
}
