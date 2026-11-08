using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Updater.Core;

namespace Updater.App;

/// <summary>
/// Снимки экранов для проверки вёрстки: «Updater.exe --shot collect out.png [--section 2] [--size 1200x780]».
/// Окно рисуется за пределами экрана и ничего не делает: сборы и распаковка в этом режиме не запускаются.
/// </summary>
public static class Screenshot
{
    public static async Task RunAsync(MainWindow window, Shell shell, string[] args)
    {
        var name = App.Value("--shot") ?? "home";
        var file = Path.GetFullPath(args.SkipWhile(a => !a.Equals("--shot", StringComparison.OrdinalIgnoreCase)).Skip(2).FirstOrDefault() ?? $"{name}.png");
        var size = (App.Value("--size") ?? "1200x780").Split('x');
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = 0;
        window.Width = double.Parse(size[0]);
        window.Height = double.Parse(size[1]);
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        int section = int.TryParse(App.Value("--section"), out var s) ? s : 0;

        switch (name)
        {
            case "home":
                break;

            case "collect":
            case "collect-progress":
            case "collect-done":
            {
                var vm = new CollectVM(shell);
                shell.Go(vm, "Сборы · старая винда");
                await vm.ScanTask;
                await Settle(window, 600);
                vm.SelectedSection = vm.Sections[Math.Min(section, vm.Sections.Count - 1)];
                if (App.Has("--expand"))
                    foreach (var i in vm.SelectedSection.Items) i.Selected = i.Selected;
                if (name == "collect-progress")
                {
                    var dest = vm.Destination ?? vm.Drives.First();
                    var job = new CollectJobVM(shell, vm.AllItems.Where(i => !i.Hidden).ToList(), vm.ScanResult, dest);
                    shell.Go(job, "Сборы · копирую");
                    job.Demo();
                }
                else if (name == "collect-done")
                {
                    var selected = vm.AllItems.Where(i => i.Selected && !i.Hidden).ToList();
                    var m = new PackageManifest
                    {
                        Status = "complete", Created = DateTime.Now, Finished = DateTime.Now,
                        Source = new SourcePc { Computer = shell.ComputerName, User = shell.UserName, Windows = shell.WindowsName },
                        Programs = vm.ScanResult.Programs,
                        Windows = new WindowsSnapshot { Settings = WinSettingsCatalog.Shared.Settings.ToDictionary(x => x.Id, _ => new CapturedSetting()) },
                        Items = selected.Where(i => i.IsData).Select(i => new StoredItem { Id = i.Id, Title = i.Title, Bytes = i.Bytes, Files = i.Files, Section = i.Section }).ToList(),
                    };
                    m.Items.FirstOrDefault()?.Let(x => { x.FailedCount = 2; x.Failed = ["занят.txt — занят другой программой", "Cookies — занят другой программой"]; });
                    var dest = vm.Destination?.Destination ?? Path.Combine(@"E:\", Package.FolderName);
                    shell.Go(new CollectDoneVM(shell, dest, m, false, false, null, vm.AllItems, vm.ScanResult), "Сборы · готово");
                }
                break;
            }

            case "restore":
            case "restore-progress":
            case "restore-done":
            {
                if (shell.FoundPackage is null || shell.FoundManifest is null)
                    throw new InvalidOperationException("Для снимка распаковки нужна сборка: --package <папка>");
                var vm = new RestoreVM(shell, shell.FoundPackage, shell.FoundManifest);
                shell.Go(vm, "Распаковка · новая винда");
                await Settle(window, 1500);
                vm.SelectedSection = vm.Sections[Math.Min(section, vm.Sections.Count - 1)];
                if (name == "restore-progress")
                {
                    var choices = vm.SettingRows.Where(r => r.Changes).Select(r => new SettingChoice(r.Def, r.Selected.Id)).ToList();
                    var job = new RestoreJobVM(shell, vm, choices, true);
                    shell.Go(job, "Распаковка · переезжаем");
                    job.Demo();
                }
                else if (name == "restore-done")
                {
                    var sum = new RestoreSummary { ProgramsInstalled = 21, ProgramsAlready = 3, BytesRestored = 48_300_000_000, FilesRestored = 18240, FilesKept = 2, DriversInstalled = 4, NeedsReboot = true };
                    sum.ProgramsFailed.Add("Octo Browser: Пакет не найден в winget");
                    sum.Settings = new ApplyReport { Applied = 31 };
                    shell.Go(new RestoreDoneVM(shell, vm, sum, false, null), "Распаковка · готово");
                }
                break;
            }
        }

        await Settle(window, 700);
        // --scroll 0.7 — прокрутить самый длинный список на эту долю высоты (1 — до конца).
        if (double.TryParse(App.Value("--scroll"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var frac))
        {
            var sv = Descendants<System.Windows.Controls.ScrollViewer>(window).OrderByDescending(s => s.ScrollableHeight).FirstOrDefault();
            sv?.ScrollToVerticalOffset(sv.ScrollableHeight * Math.Clamp(frac, 0, 1));
            await Settle(window, 400);
        }
        Save(window, file);
        Console.WriteLine("снимок: " + file);
    }

    private static async Task Settle(Window w, int ms)
    {
        await Task.Delay(ms);
        w.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        w.UpdateLayout();
    }

    private static void Save(Window w, string file)
    {
        var root = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(w);
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    private static void Let<T>(this T value, Action<T> action) => action(value);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }
}
