using System.IO;
using System.Windows;
using Updater.Core;

namespace Updater.App;

/// <summary>Состояние всего окна: текущий экран, найденная сборка, диалог поверх.</summary>
public sealed class Shell : Observable
{
    public static Shell Current { get; private set; } = null!;

    public PathResolver Paths { get; init; } = PathResolver.Live();
    /// <summary>Реестр: настоящий или песочница (--sandbox-root для безопасной проверки на рабочем ПК).</summary>
    public IRegistryAccess Registry { get; init; } = new LiveRegistry();
    public Journal Log { get; } = new();
    public bool IsAdmin { get; } = Native.IsAdmin();
    public string WindowsName { get; } = Native.WindowsName();
    public string UserName { get; } = Environment.UserName;
    public string ComputerName { get; } = Environment.MachineName;
    public bool CanElevate { get; init; } = true;

    public Shell() => Current = this;

    private object? _page;
    public object? Page { get => _page; private set => Set(ref _page, value); }

    private string _breadcrumb = "";
    public string Breadcrumb { get => _breadcrumb; set => Set(ref _breadcrumb, value); }

    private DialogVM? _dialog;
    public DialogVM? Dialog { get => _dialog; private set => Set(ref _dialog, value); }

    /// <summary>Сборка, найденная рядом с программой или на флешке.</summary>
    public string? FoundPackage { get; set; }
    public PackageManifest? FoundManifest { get; set; }

    public void Go(object page, string breadcrumb)
    {
        Page = page;
        Breadcrumb = breadcrumb;
    }

    public void GoHome() => Go(new HomeVM(this), "");

    private SelfUpdater.Release? _pendingUpdate;
    /// <summary>Вышла новая версия. Если человек уже ушёл с главного экрана, не дёргаем — там будет ссылка «обновить».</summary>
    public SelfUpdater.Release? PendingUpdate
    {
        get => _pendingUpdate;
        set { if (Set(ref _pendingUpdate, value)) Raise(nameof(HasUpdate), nameof(UpdateLinkText)); }
    }
    public bool HasUpdate => PendingUpdate is not null;
    public string UpdateLinkText => PendingUpdate is { } r ? $"Вышла версия {r.Version.ToString(3)} — обновить" : "";

    /// <summary>Автопроверка: вопросов не задаём, отвечаем «да» и пишем вопрос в журнал.</summary>
    public bool Auto { get; set; }

    public async Task<bool> AskAsync(string title, string text, string ok = "Продолжить", string? cancel = "Отмена", bool danger = false)
    {
        if (Auto)
        {
            Log.Info($"[авто] {title}: {text} → «{ok}»");
            return true;
        }
        var d = new DialogVM { Title = title, Text = text, OkText = ok, CancelText = cancel, Danger = danger };
        Dialog = d;
        try { return await d.Result; }
        finally { Dialog = null; }
    }

    public Task InfoAsync(string title, string text) => AskAsync(title, text, "Понятно", null);

    public void LoadPackage(string? root)
    {
        FoundPackage = null;
        FoundManifest = null;
        if (root is null) return;
        var m = Package.TryLoad(root);
        if (m is null) return;
        FoundPackage = root;
        FoundManifest = m;
    }

    /// <summary>Перезапуск с правами администратора (одно окно UAC).</summary>
    public static void RestartElevated()
    {
        if (App.TryRelaunchElevated(App.Args)) Application.Current.Shutdown();
    }

    public static string ExeDirectory => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
}
