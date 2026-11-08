using System.Windows.Input;
using Microsoft.Win32;
using Pereezd.Core;

namespace Pereezd.App;

public sealed class HomeVM(Shell shell) : Observable
{
    public Shell Shell => shell;

    public bool HasPackage => shell.FoundManifest is not null;
    public bool NoPackage => !HasPackage;

    public string PackageTitle => shell.FoundManifest is { } m ? $"Сборка с {m.Source.Computer}" : "";

    public string PackageDetail
    {
        get
        {
            if (shell.FoundManifest is not { } m) return "";
            int auto = m.Programs.Count(p => p.AutoInstallable);
            var s = $"{Fmt.Date(m.Finished ?? m.Created)} · {Fmt.Size(m.TotalBytes)} · {auto} {Fmt.Plural(auto, "программа встанет", "программы встанут", "программ встанут")} сами";
            if (!m.IsComplete) s += " · сборы не закончены";
            return s;
        }
    }

    public string PackagePath => shell.FoundPackage ?? "";

    /// <summary>Сборка сделана на этом же компьютере и этой же винде — значит, переустановки ещё не было.</summary>
    public bool PackageFromThisWindows =>
        shell.FoundManifest is { } m && m.Source.Computer == shell.ComputerName && m.Source.Profile == shell.Paths.Get("Profile")
        && WindowsInstallDate() is { } installed && installed < m.Created.ToUniversalTime();

    public string SystemLine =>
        $"{shell.WindowsName} · {shell.UserName} · {(shell.IsAdmin ? "права администратора есть" : "без прав администратора")}";

    public bool ShowElevate => !shell.IsAdmin && shell.CanElevate;

    public ICommand Collect => new RelayCommand(() => shell.Go(new CollectVM(shell), "Сборы · старая винда"));

    public ICommand Restore => new RelayCommand(async () =>
    {
        if (shell.FoundPackage is null || shell.FoundManifest is null) return;
        if (PackageFromThisWindows && !await shell.AskAsync("Это та же Windows",
                "Сборка сделана на этой же системе, переустановки ещё не было. Распаковывать стоит на новой винде. Всё равно открыть?",
                "Открыть", "Отмена"))
            return;
        shell.Go(new RestoreVM(shell, shell.FoundPackage, shell.FoundManifest), "Распаковка · новая винда");
    });

    public ICommand Browse => new RelayCommand(async () =>
    {
        var dlg = new OpenFolderDialog { Title = $"Где лежит сборка {Package.AppName}?" };
        if (dlg.ShowDialog() != true) return;
        var root = Package.Find(dlg.FolderName);
        if (root is null)
        {
            await shell.InfoAsync("Сборка не найдена", $"В папке «{dlg.FolderName}» нет описи сборки ({PackageManifest.FileName}). Выбери папку «{Package.FolderName}» на флешке или диске.");
            return;
        }
        shell.LoadPackage(root);
        Raise(nameof(HasPackage), nameof(NoPackage), nameof(PackageTitle), nameof(PackageDetail), nameof(PackagePath), nameof(PackageFromThisWindows));
        if (shell.FoundManifest is not null) Restore.Execute(null);
    });

    public ICommand Elevate => new RelayCommand(Shell.RestartElevated);

    private static DateTime? WindowsInstallDate() => Fonts.InstallDate();
}
