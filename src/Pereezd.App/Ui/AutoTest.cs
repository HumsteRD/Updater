using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pereezd.Core;

namespace Pereezd.App;

/// <summary>
/// Распаковка без человека — для проверки на чистой винде (Windows Sandbox, виртуалка):
/// «Updater.exe --auto-restore C:\Results [--programs id1,id2] [--settings was|recommended|none] [--no-winget]».
/// Проходит тот же путь, что и пользователь: экран плана, «Начать переезд», прогресс, итог —
/// снимает каждый экран, а потом сверяет, что всё действительно встало на место.
/// </summary>
public static class AutoTest
{
    public static async Task RunAsync(MainWindow window, Shell shell, string results)
    {
        Directory.CreateDirectory(results);
        var sw = Stopwatch.StartNew();
        shell.Auto = true;
        var winget = await Winget.VersionAsync();
        File.WriteAllText(Path.Combine(results, "start.txt"),
            $"exe={Environment.ProcessPath}\nadmin={Native.IsAdmin()}\nuser={Environment.UserName}\nwindows={Native.WindowsName()}\n" +
            $"package={shell.FoundPackage ?? "(нет)"}\nwinget={winget ?? "(нет)"}\nstarted={DateTime.Now:O}\n", Encoding.UTF8);

        if (shell.FoundPackage is null || shell.FoundManifest is null)
        {
            Finish(results, "Сборка не найдена", [], shell);
            return;
        }

        var vm = new RestoreVM(shell, shell.FoundPackage, shell.FoundManifest);
        shell.Go(vm, "Распаковка · новая винда");
        await vm.Ready;

        // Какие программы ставить: список из аргумента, иначе как предложил экран.
        if (App.Value("--programs") is { } list)
        {
            var wanted = list.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? new HashSet<string>()
                : list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in vm.Plan.Where(i => i.Kind == ItemKind.Programs))
                foreach (var c in item.Children.Where(c => c.Selectable))
                    c.Selected = wanted.Contains(c.Id);
        }
        switch (App.Value("--settings") ?? "was")
        {
            case "recommended": vm.AllRecommended.Execute(null); break;
            case "none": vm.NoneSettings.Execute(null); break;
            default: vm.AllAsWas.Execute(null); break;
        }
        await Settle(window, 800);

        Shot(window, vm, Section.Programs, false, results, "1-план-программы.png");
        Shot(window, vm, Section.Windows, true, results, "2-план-настройки.png");
        Shot(window, vm, Section.Important, false, results, "3-план-перед-стартом.png", warnings: true);

        vm.Start.Execute(null);
        RestoreJobVM? job = null;
        for (int i = 0; i < 300 && job is null; i++)
        {
            await Task.Delay(100);
            job = shell.Page as RestoreJobVM;
        }
        if (job is null)
        {
            Finish(results, "Переезд не начался", [], shell);
            return;
        }

        int n = 0;
        while (!job.Completion.IsCompleted)
        {
            await Task.WhenAny(job.Completion, Task.Delay(TimeSpan.FromSeconds(20)));
            if (job.Completion.IsCompleted) break;
            await Settle(window, 200);
            Save(window, Path.Combine(results, $"4-прогресс-{++n:00}.png"));
            File.WriteAllLines(Path.Combine(results, "journal.txt"), shell.Log.All.Select(l => l.ToString()), Encoding.UTF8);
        }
        await Settle(window, 1500);
        Save(window, Path.Combine(results, "5-итог.png"));

        var lines = await VerifyAsync(shell, vm, job);
        lines.Add($"Время: {Fmt.Duration(sw.Elapsed)}");
        var s = job.Restorer.Summary;
        lines.Add($"Сводка: программ поставлено {s.ProgramsInstalled}, уже было {s.ProgramsAlready}, не встало {s.ProgramsFailed.Count}; " +
                  $"файлов {s.FilesRestored} ({Fmt.Size(s.BytesRestored)}), оставлено новее {s.FilesKept}, ошибок {s.FilesFailed}; " +
                  $"драйверов {s.DriversInstalled}; настроек {s.Settings?.Applied ?? 0}, не вышло {s.Settings?.Failed.Count ?? 0}; перезагрузка {(s.NeedsReboot ? "нужна" : "не нужна")}");
        foreach (var p in s.ProgramsFailed) lines.Add("  не встало: " + p);
        foreach (var p in s.Problems) lines.Add("  проблема: " + p);
        if (s.Settings is not null) foreach (var f in s.Settings.Failed) lines.Add("  настройка: " + f);
        Finish(results, null, lines, shell);
    }

    private static void Shot(MainWindow window, RestoreVM vm, Section key, bool settings, string results, string name, bool warnings = false)
    {
        vm.SelectedSection = vm.Sections.FirstOrDefault(s => warnings ? s.IsWarnings : settings ? s.IsSettings : s.Key == key && s.IsItems) ?? vm.SelectedSection;
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Save(window, Path.Combine(results, name));
    }

    /// <summary>Сверка: файлы на местах, программы в «Программах и компонентах», настройки в реестре.</summary>
    private static async Task<List<string>> VerifyAsync(Shell shell, RestoreVM plan, RestoreJobVM job)
    {
        var lines = new List<string>();
        int ok = 0, bad = 0;
        void Line(bool good, string text)
        {
            lines.Add((good ? "✓ " : "× ") + text);
            if (good) ok++;
            else bad++;
        }

        foreach (var item in plan.Plan.Where(i => i.Stored is not null && i.Selected && i.Selectable))
        {
            foreach (var r in item.Stored!.Roots)
            {
                if (!shell.Paths.TryResolve(r.Target, out var target))
                {
                    Line(false, $"{item.Title}: неизвестная метка {r.Target}");
                    continue;
                }
                if (r.IsFile) Line(File.Exists(target), $"{item.Title}: {target}");
                else
                {
                    int files = Directory.Exists(target) ? FileWalker.Measure(target).Files : 0;
                    Line(files > 0, $"{item.Title}: {target} — {Fmt.Files(files)}");
                }
            }
            if (item.Stored.RegFiles.Count > 0 && item.Id == "app.7zip")
                Line(shell.Paths.IsSandbox || shell.Registry.KeyExists(@"HKCU\Software\7-Zip"), "7-Zip: ветка реестра вернулась");
        }

        foreach (var item in plan.Plan.Where(i => i.Kind is ItemKind.Drivers or ItemKind.Wifi or ItemKind.Fonts && i.Selected))
            Line(item.Status is ItemStatus.Done or ItemStatus.Warning || shell.Paths.IsSandbox && item.Status == ItemStatus.Skipped,
                $"{item.Title}: {item.Status} — {item.StatusText}");

        var installed = ProgramInventory.ReadArp()
            .Select(e => WingetIndex.Shared.Match(e.Key, e.Name, e.Publisher, e.Version)?.Id)
            .Where(id => id is not null).Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in job.Programs)
        {
            var id = (p.Tag as ProgramRecord)?.WingetId ?? p.Tag as string ?? p.Id;
            bool there = installed.Contains(id);
            Line(there || p.Status == ItemStatus.Done && p.StatusText?.Contains("Уже") == true,
                $"Программа {p.Title} ({id}): {p.StatusText}; в «Программах и компонентах» {(there ? "есть" : "не видно")}");
        }

        var engine = new WindowsSettings(shell.Registry, shell.Log, shell.Paths.IsSandbox);
        var snap = plan.Manifest.Windows;
        foreach (var c in job.Choices)
        {
            var now = await engine.ReadCurrentAsync(c.Def);
            var captured = snap?.Settings.GetValueOrDefault(c.Def.Id);
            bool good;
            string detail;
            if (!c.Def.IsSpecial && !c.Def.IsRaw)
            {
                var want = c.OptionId == SettingChoice.Old ? captured?.Option : c.OptionId;
                good = now?.Option == want;
                detail = $"нужно «{c.Def.Option(want)?.Label ?? want}», стоит «{now?.Summary}»";
            }
            else if (c.Def.IsRaw)
            {
                good = now is not null && captured is not null &&
                       now.Values.Zip(captured.Values).All(v => v.Second.IsAbsent || v.First.SameAs(v.Second));
                detail = $"стоит «{now?.Summary}», было «{captured?.Summary}»";
            }
            else
            {
                var wallpaper = shell.Registry.Read(@"HKCU\Control Panel\Desktop", "WallPaper").Value ?? "";
                // В песочнице на рабочем ПК особые настройки (обои, пояс, питание) нарочно не применяются.
                good = shell.Paths.IsSandbox && c.Def.Handler is not ("classicmenu" or "env") || c.Def.Handler switch
                {
                    "classicmenu" => now?.Option == (c.OptionId == SettingChoice.Old ? captured?.Option : c.OptionId),
                    "timezone" => now?.Extra.GetValueOrDefault("id") == captured?.Extra.GetValueOrDefault("id"),
                    "wallpaper" => File.Exists(wallpaper) && wallpaper.Contains("Обои", StringComparison.OrdinalIgnoreCase),
                    _ => true,
                };
                detail = c.Def.Handler == "wallpaper" ? $"обои: {wallpaper}" : $"стоит «{now?.Summary}»";
            }
            Line(good, $"Настройка «{c.Def.Title}»: {detail}");
        }

        Line(Process.GetProcessesByName("explorer").Length > 0, "Проводник работает после перезапуска");
        lines.Insert(0, $"ИТОГО: ✓ {ok}  × {bad}");
        return lines;
    }

    private static void Finish(string results, string? error, List<string> lines, Shell shell)
    {
        if (error is not null) lines.Insert(0, "ОШИБКА: " + error);
        File.WriteAllLines(Path.Combine(results, "verify.txt"), lines, Encoding.UTF8);
        File.WriteAllLines(Path.Combine(results, "journal.txt"), shell.Log.All.Select(l => l.ToString()), Encoding.UTF8);
        // Проверка на рабочем ПК: своя ветка реестра убирается за собой.
        if (shell.Registry is SandboxRegistry sandbox) sandbox.Wipe();
        File.WriteAllText(Path.Combine(results, "done.flag"), DateTime.Now.ToString("O"));
    }

    private static async Task Settle(Window w, int ms)
    {
        await Task.Delay(ms);
        w.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window w, string file)
    {
        try
        {
            var root = (FrameworkElement)w.Content;
            var dpi = VisualTreeHelper.GetDpi(w);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(file);
            enc.Save(fs);
        }
        catch { }
    }
}
