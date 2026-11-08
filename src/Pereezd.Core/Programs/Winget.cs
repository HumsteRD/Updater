using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Pereezd.Core;

public enum InstallOutcome { Installed, AlreadyInstalled, NeedsReboot, NotFound, NeedsUserContext, Failed, Cancelled }

public sealed record InstallResult(InstallOutcome Outcome, int ExitCode, string Message)
{
    public bool Ok => Outcome is InstallOutcome.Installed or InstallOutcome.AlreadyInstalled or InstallOutcome.NeedsReboot;
}

/// <summary>
/// Установка программ через winget. На свежей Windows winget обычно есть, но бывает не зарегистрирован
/// в первые минуты после входа, а в «облегчённых» сборках удалён совсем — тогда ставим его сами.
/// </summary>
public static class Winget
{
    // Коды возврата из официальной таблицы winget (returnCodes.md).
    private const uint UpdateNotApplicable = 0x8A15002B;
    private const uint PackageAlreadyInstalled = 0x8A150061;
    private const uint InstallAlreadyInstalled = 0x8A15010D;
    private const uint InstallDowngrade = 0x8A15010E;
    private const uint NoApplicationsFound = 0x8A150014;
    private const uint ProhibitsElevation = 0x8A150056;
    private const uint AdminContextProhibited = 0x8A15007D;
    private const uint RebootToFinish = 0x8A150109;
    private const uint RebootForInstall = 0x8A15010A;
    private const uint PackageInUse = 0x8A150101;
    private const uint FileInUse = 0x8A150103;
    private const uint DiskFull = 0x8A150105;
    private const uint NoNetwork = 0x8A150107;
    private const uint DownloadFailed = 0x8A150008;
    private const uint HashMismatch = 0x8A150011;
    private const uint CancelledByUser = 0x8A15010C;
    private const uint BlockedByPolicy = 0x8A15010F;
    private const uint ContactSupport = 0x8A150108;
    private const uint MultipleFound = 0x8A150016;

    public static string? Locate()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public static async Task<string?> VersionAsync(CancellationToken ct = default)
    {
        var exe = Locate();
        if (exe is null) return null;
        var r = await Proc.RunAsync(exe, "--version", ct, encoding: Encoding.UTF8, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return r.Ok ? r.Output.Trim() : null;
    }

    /// <summary>
    /// Добиться, чтобы winget был: перерегистрировать App Installer, а если его нет — скачать с GitHub Microsoft.
    /// Каждый шаг пишется в журнал: если не вышло, видно, на чём именно.
    /// </summary>
    public static async Task<(bool Ok, string? Reason)> EnsureAsync(Action<string> status, Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (await VersionAsync(ct).ConfigureAwait(false) is { } have)
        {
            log?.Invoke($"winget {have} уже есть");
            return (true, null);
        }

        status("Регистрирую winget…");
        var reg = await Proc.PowerShellAsync(
            "Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe -ErrorAction Stop",
            ct, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        log?.Invoke("Регистрация App Installer: " + (reg.Ok ? "готово" : FirstMeaningful(reg.All)));
        if (await VersionAsync(ct).ConfigureAwait(false) is { } registered)
        {
            log?.Invoke($"winget {registered} включён");
            return (true, null);
        }

        // Скачиваем и распаковываем сами, без Invoke-WebRequest и Expand-Archive: в чистой Windows (проверено
        // в Windows Sandbox) модуль архивов PowerShell не загружается, а модуль Appx для установки — работает.
        var dir = Path.Combine(Package.TempDir, "winget");
        const string baseUrl = "https://github.com/microsoft/winget-cli/releases/latest/download/";

        long depBytes = 0;
        var deps = await Downloader.GetAsync(baseUrl + "DesktopAppInstaller_Dependencies.zip", dir, "deps.zip",
            b => { depBytes += b; status($"Скачиваю зависимости winget: {Fmt.Size(depBytes)}"); }, ct).ConfigureAwait(false);
        if (!deps.Ok) return Fail("не скачались зависимости winget: " + deps.Error);
        log?.Invoke($"winget: зависимости скачаны, {Fmt.Size(deps.Bytes)}");

        List<string> depFiles;
        try { depFiles = ExtractX64Packages(deps.Path!, Path.Combine(dir, "deps")); }
        catch (Exception ex) { return Fail("зависимости winget не распаковались: " + ex.Message); }
        if (depFiles.Count == 0) return Fail("в архиве зависимостей winget нет пакетов x64");
        log?.Invoke("winget: зависимости — " + string.Join(", ", depFiles.Select(Path.GetFileName)));

        long bundleBytes = 0;
        var bundle = await Downloader.GetAsync(baseUrl + "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle", dir, "winget.msixbundle",
            b => { bundleBytes += b; status($"Скачиваю winget: {Fmt.Size(bundleBytes)}"); }, ct).ConfigureAwait(false);
        if (!bundle.Ok) return Fail("не скачался winget: " + bundle.Error);
        log?.Invoke($"winget: пакет скачан, {Fmt.Size(bundle.Bytes)}");

        // Пакет вместе с зависимостями — одним вызовом. Если не вышло (например, какая-то зависимость уже стоит
        // новее), — зависимости по одной, ошибки по ним не страшны, и затем сам пакет.
        status("Ставлю winget…");
        static string Q(string p) => "'" + p.Replace("'", "''") + "'";
        string? lastError = null;
        var all = await Proc.PowerShellAsync(
            $"Add-AppxPackage -Path {Q(bundle.Path!)} -DependencyPath {string.Join(",", depFiles.Select(Q))} -ErrorAction Stop",
            ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        log?.Invoke("winget: установка с зависимостями — " + (all.Ok ? "готово" : FirstMeaningful(all.All)));
        if (!all.Ok)
        {
            foreach (var d in depFiles)
            {
                var r = await Proc.PowerShellAsync($"Add-AppxPackage -Path {Q(d)} -ErrorAction Stop", ct, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                log?.Invoke($"winget: {Path.GetFileName(d)} — {(r.Ok ? "готово" : FirstMeaningful(r.All))}");
            }
            var only = await Proc.PowerShellAsync($"Add-AppxPackage -Path {Q(bundle.Path!)} -ErrorAction Stop", ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            log?.Invoke("winget: установка пакета — " + (only.Ok ? "готово" : FirstMeaningful(only.All)));
            if (!only.Ok) lastError = FirstMeaningful(only.All);
        }

        // Псевдоним winget.exe появляется не мгновенно.
        for (int i = 0; i < 20; i++)
        {
            if (await VersionAsync(ct).ConfigureAwait(false) is { } installed)
            {
                log?.Invoke($"winget {installed} готов");
                return (true, null);
            }
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return Fail(lastError ?? "winget не появился после установки");

        (bool, string?) Fail(string reason)
        {
            log?.Invoke("winget: " + reason);
            return (false, reason);
        }
    }

    /// <summary>
    /// Достать из архива зависимостей winget только пакеты x64 (VCLibs, Windows App Runtime) —
    /// папки x86 и arm64 нам не нужны. Возвращает пути распакованных файлов.
    /// </summary>
    public static List<string> ExtractX64Packages(string zipPath, string destDir)
    {
        if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        Directory.CreateDirectory(destDir);
        var result = new List<string>();
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            bool package = name.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase);
            bool x64 = name.StartsWith("x64/", StringComparison.OrdinalIgnoreCase) || name.Contains("/x64/", StringComparison.OrdinalIgnoreCase)
                       || Path.GetFileName(name).Contains("_x64", StringComparison.OrdinalIgnoreCase);
            if (!package || !x64) continue;
            var target = Path.Combine(destDir, Fmt.SafeName(Path.GetFileName(name)));
            e.ExtractToFile(target, overwrite: true);
            result.Add(target);
        }
        return result;
    }

    public static string InstallArgs(string id, string? source) =>
        $"install --id \"{id}\" --exact --source {(source == "msstore" ? "msstore" : "winget")} --silent " +
        "--accept-package-agreements --accept-source-agreements --disable-interactivity";

    public static async Task<InstallResult> InstallAsync(string id, string? source, Action<string>? progress, CancellationToken ct)
    {
        var exe = Locate();
        if (exe is null) return new InstallResult(InstallOutcome.Failed, -1, "winget не найден");
        var r = await Proc.RunAsync(exe, InstallArgs(id, source), ct,
            onLine: line =>
            {
                var clean = CleanLine(line);
                if (clean.Length > 0) progress?.Invoke(clean);
            },
            encoding: Encoding.UTF8, timeout: TimeSpan.FromMinutes(40)).ConfigureAwait(false);
        return Interpret(r.ExitCode, r.All);
    }

    public static InstallResult Interpret(int exitCode, string output)
    {
        uint code = unchecked((uint)exitCode);
        return code switch
        {
            0 => new(InstallOutcome.Installed, exitCode, "Установлено"),
            UpdateNotApplicable or PackageAlreadyInstalled or InstallAlreadyInstalled or InstallDowngrade =>
                new(InstallOutcome.AlreadyInstalled, exitCode, "Уже установлено"),
            RebootToFinish or RebootForInstall => new(InstallOutcome.NeedsReboot, exitCode, "Установится после перезагрузки"),
            NoApplicationsFound => new(InstallOutcome.NotFound, exitCode, "Пакет не найден в winget"),
            MultipleFound => new(InstallOutcome.NotFound, exitCode, "Под это имя подходит несколько пакетов"),
            ProhibitsElevation or AdminContextProhibited =>
                new(InstallOutcome.NeedsUserContext, exitCode, "Установщик не работает с правами администратора"),
            PackageInUse or FileInUse => new(InstallOutcome.Failed, exitCode, "Программа сейчас запущена — закрой её и повтори"),
            DiskFull => new(InstallOutcome.Failed, exitCode, "Не хватает места на диске"),
            NoNetwork or DownloadFailed => new(InstallOutcome.Failed, exitCode, "Не скачалось — проверь интернет"),
            HashMismatch => new(InstallOutcome.Failed, exitCode, "Установщик изменился на сайте, winget его не принял — повтори позже"),
            CancelledByUser => new(InstallOutcome.Cancelled, exitCode, "Установку отменили"),
            BlockedByPolicy => new(InstallOutcome.Failed, exitCode, "Запрещено политикой"),
            ContactSupport => new(InstallOutcome.Failed, exitCode, "Установщик программы завершился с ошибкой"),
            _ => new(InstallOutcome.Failed, exitCode, $"Ошибка 0x{code:X8}: {FirstMeaningful(output)}"),
        };
    }

    /// <summary>Строка прогресса winget без «вертушки» и псевдографики полос.</summary>
    public static string CleanLine(string line)
    {
        var s = line.Replace("\r", " ");
        s = Regex.Replace(s, @"[█▒░▓■\-\\|/]{3,}", " ");
        s = Regex.Replace(s, @"^\s*[\-\\|/]\s*$", "");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s;
    }

    private static string FirstMeaningful(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var l = CleanLine(raw);
            if (l.Length > 8) return l.Length > 160 ? l[..160] : l;
        }
        return "без подробностей";
    }

    /// <summary>
    /// Запуск команды без прав администратора (для установщиков, которые с ними отказываются работать, — Spotify и др.).
    /// Через Проводник: он запускает файл от имени пользователя, как двойной щелчок.
    /// </summary>
    public static async Task<InstallResult> InstallAsUserAsync(string id, string? source, string workDir, CancellationToken ct)
    {
        var exe = Locate();
        if (exe is null) return new InstallResult(InstallOutcome.Failed, -1, "winget не найден");
        Directory.CreateDirectory(workDir);
        var tag = Fmt.SafeName(id);
        var done = Path.Combine(workDir, tag + ".exitcode");
        var cmd = Path.Combine(workDir, tag + ".cmd");
        if (File.Exists(done)) File.Delete(done);
        File.WriteAllText(cmd,
            "@echo off\r\nchcp 65001 >nul\r\n" +
            $"echo {Package.AppName}: ставлю {id} без прав администратора…\r\n" +
            $"\"{exe}\" {InstallArgs(id, source)}\r\n" +
            $"echo %errorlevel% > \"{done}\"\r\n", new UTF8Encoding(false));
        Proc.OpenAsUser(cmd);
        var deadline = DateTime.UtcNow.AddMinutes(40);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1500, ct).ConfigureAwait(false);
            if (!File.Exists(done)) continue;
            try
            {
                var text = (await File.ReadAllTextAsync(done, ct).ConfigureAwait(false)).Trim();
                if (int.TryParse(text, out var code)) return Interpret(code, "");
            }
            catch (IOException) { }
        }
        return new InstallResult(InstallOutcome.Failed, -1, "Не дождался окончания установки");
    }

    /// <summary>Скачать установщик по прямой ссылке и запустить (для программ, которых нет в winget).</summary>
    public static async Task<InstallResult> DownloadAndRunAsync(string url, string workDir, Action<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var name = Fmt.SafeName(Path.GetFileName(new Uri(url).LocalPath));
        if (string.IsNullOrEmpty(Path.GetExtension(name))) name += ".exe";
        var file = Path.Combine(workDir, name);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Pereezd/1.0");
            progress?.Invoke("Скачиваю " + name);
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(file);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            progress?.Invoke("Запускаю установщик — ответь на его вопросы");
            var r = await Proc.RunAsync(file, "", ct, timeout: TimeSpan.FromMinutes(40)).ConfigureAwait(false);
            return r.ExitCode == 0
                ? new InstallResult(InstallOutcome.Installed, 0, "Установлено")
                : new InstallResult(InstallOutcome.Failed, r.ExitCode, $"Установщик завершился с кодом {r.ExitCode}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new InstallResult(InstallOutcome.Failed, -1, "Не скачалось: " + ex.Message);
        }
    }
}
