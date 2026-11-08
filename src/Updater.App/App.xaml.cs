using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Updater.Core;

namespace Updater.App;

public partial class App : Application
{
    public static string[] Args { get; private set; } = [];

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Args = e.Args;
        DispatcherUnhandledException += OnCrash;

        // Служебная проверка: что программа знает о себе (для самокопирования на флешку).
        if (Value("--info") is { } infoFile)
        {
            File.WriteAllText(infoFile,
                $"exe={Collector.SingleFileExe() ?? "(не один файл)"}\nversion={Collector.AppVersion()}\nadmin={Native.IsAdmin()}\n" +
                $"package={Package.Discover(Shell.ExeDirectory) ?? "(нет)"}\nwinget-index={WingetIndex.Shared.Count}\n");
            Shutdown();
            return;
        }

        bool shot = Has("--shot");
        if (Value("--auto-restore") is { } autoDir)
        {
            // Первая отметка ещё до запроса прав: если окно UAC повиснет, это будет видно по результатам.
            try
            {
                Directory.CreateDirectory(autoDir);
                File.AppendAllText(Path.Combine(autoDir, "boot.txt"), $"{DateTime.Now:O} admin={Native.IsAdmin()} args={string.Join(' ', e.Args)}\n");
            }
            catch { }
        }
        // Песочница на рабочем ПК: все пути — во временной папке, реестр — в своей ветке, система не трогается.
        var sandboxRoot = Value("--sandbox-root");

        // Одно окно UAC при старте: драйверы, пароли Wi-Fi и системные настройки без прав не сделать.
        if (!shot && sandboxRoot is null && !Has("--no-elevate") && !Has("--elevated") && !Native.IsAdmin())
        {
            if (TryRelaunchElevated(e.Args))
            {
                Shutdown();
                return;
            }
        }

        var shell = sandboxRoot is null
            ? new Shell { CanElevate = !Native.IsAdmin() }
            : new Shell { CanElevate = false, Paths = PathResolver.Sandbox(sandboxRoot), Registry = new SandboxRegistry(@"Software\Updater.AutoTest") };

        // Права выдала другая учётная запись — тогда «мои папки» оказались бы чужими.
        if (Value("--for-user") is { } sid && Native.IsAdmin() && WindowsIdentity.GetCurrent().User?.Value != sid)
        {
            MessageBox.Show(
                "Права администратора выданы другой учётной записью, и программа видит не твои папки, а её.\n\n" +
                $"Запусти {Package.AppName} из своей учётной записи администратора или ответь «Нет» в окне запроса прав — тогда программа поработает без них.",
                Package.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        shell.LoadPackage(Value("--package") is { } p ? Package.Find(p) : Package.Discover(Shell.ExeDirectory));
        shell.GoHome();

        var window = new MainWindow { DataContext = shell };
        MainWindow = window;

        if (shot)
        {
            await Screenshot.RunAsync(window, shell, e.Args);
            Shutdown();
            return;
        }
        if (Has("--offscreen"))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
        }
        window.Show();
        // Проверка обновлений — фоном, окно уже открыто. Не в служебных режимах и не в отладочной сборке.
        if (Value("--auto-restore") is null && sandboxRoot is null && !Has("--no-update") && Collector.SingleFileExe() is { } selfExe)
            _ = CheckForUpdateAsync(shell, selfExe);
        if (Value("--auto-restore") is { } results)
        {
            try { await AutoTest.RunAsync(window, shell, results); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(results, "verify.txt"), "ОШИБКА автопроверки: " + ex);
                File.WriteAllText(Path.Combine(results, "done.flag"), DateTime.Now.ToString("O"));
            }
        }
    }

    public static bool Has(string flag) => Args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    public static string? Value(string flag)
    {
        int i = Array.FindIndex(Args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;
    }

    /// <summary>Перезапуск с правами администратора. false — пользователь отказался (работаем без прав).</summary>
    public static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
            var rest = args.Where(a => !a.Equals("--elevated", StringComparison.OrdinalIgnoreCase)).Select(Quote);
            var psi = new ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", rest.Append("--elevated").Append("--for-user").Append(sid)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Shell.ExeDirectory,
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Есть выпуск новее — сразу открываем экран обновления, если человек ещё на главном экране.
    /// Если он уже начал сборы или распаковку, не мешаем: на главном останется ссылка «обновить».
    /// </summary>
    private static async Task CheckForUpdateAsync(Shell shell, string exe)
    {
        try
        {
            await SelfUpdater.CleanupLeftoversAsync(exe);
            if (!SelfUpdater.CanWriteNextTo(exe))
            {
                shell.Log.Info("Обновления: папка программы только для чтения — проверку пропускаю");
                return;
            }
            var (release, error) = await SelfUpdater.GetLatestAsync();
            if (release is null)
            {
                shell.Log.Info("Обновления: " + error);
                return;
            }
            if (!SelfUpdater.IsNewer(release.Version, SelfUpdater.Current)) return;
            shell.Log.Info($"Обновления: вышла версия {release.Version.ToString(3)}");
            shell.PendingUpdate = release;
            if (shell.Page is HomeVM && shell.Dialog is null)
                shell.Go(UpdateVM.Create(shell, release, exe), "Обновление");
        }
        catch (Exception ex)
        {
            shell.Log.Info("Обновления: " + ex.Message);
        }
    }

    /// <summary>
    /// Запуск новой версии с теми же параметрами. Права администратора наследуются; если их не дали,
    /// новая копия не спросит второй раз.
    /// </summary>
    public static bool RestartAfterUpdate(string exe)
    {
        var args = new List<string>();
        for (int i = 0; i < Args.Length; i++)
        {
            if (Args[i].Equals("--updated-from", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            args.Add(Args[i]);
        }
        args.Add("--updated-from");
        args.Add(SelfUpdater.Current.ToString(3));
        if (!Native.IsAdmin() && !args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase)) args.Add("--no-elevate");
        try
        {
            Process.Start(new ProcessStartInfo(exe, string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Shell.ExeDirectory,
            });
        }
        catch
        {
            return false;
        }
        Current.Shutdown();
        return true;
    }

    private static string Quote(string a) => a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

    private void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            var dir = Package.DataDir;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "ошибки.txt"), $"{DateTime.Now}: {e.Exception}\n\n");
        }
        catch { }
        if (Has("--shot"))
        {
            Console.Error.WriteLine(e.Exception);
            Shutdown(3);
            return;
        }
        MessageBox.Show("Что-то пошло не так:\n\n" + e.Exception.Message + $"\n\nПодробности — в %LOCALAPPDATA%\\{Package.AppName}\\ошибки.txt",
            Package.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
