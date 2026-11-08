using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Pereezd.Core;

/// <summary>Запуск консольных утилит (reg, netsh, pnputil, winget, powershell) с перехватом вывода.</summary>
public static class Proc
{
    public sealed record Result(int ExitCode, string Output, string Error)
    {
        public bool Ok => ExitCode == 0;
        public string All => string.IsNullOrWhiteSpace(Error) ? Output : Output + Environment.NewLine + Error;
    }

    static Proc()
    {
        // Системные утилиты пишут в кодировке OEM (866 для русской Windows) — без провайдера её не прочитать.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>
    /// Кодировка консольных утилит. Берётся из языка системы (GetOEMCP), а не из региональных настроек пользователя:
    /// на английской винде с русским форматом даты они расходятся, и вывод powercfg превращался в кракозябры.
    /// </summary>
    public static Encoding Oem
    {
        get
        {
            try { return Encoding.GetEncoding((int)GetOEMCP()); }
            catch
            {
                try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
                catch { return Encoding.UTF8; }
            }
        }
    }

    public static string System32(string exe) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);

    public static async Task<Result> RunAsync(
        string file, string args,
        CancellationToken ct = default,
        Action<string>? onLine = null,
        Encoding? encoding = null,
        TimeSpan? timeout = null,
        string? workDir = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Oem,
            StandardErrorEncoding = encoding ?? Oem,
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var error = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (error) error.AppendLine(e.Data);
        };

        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return new Result(-1, "", ex.Message);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new Result(-2, output.ToString(), "Превышено время ожидания");
        }
        // Дочитываем хвост асинхронного вывода.
        p.WaitForExit();
        return new Result(p.ExitCode, output.ToString(), error.ToString());
    }

    /// <summary>
    /// PowerShell-скрипт с выводом в UTF-8 (иначе кириллица в JSON ломается). Ошибки скрипта приходят
    /// обычным текстом: без -OutputFormat Text PowerShell отдаёт их в служебном CLIXML, и причина сбоя терялась.
    /// </summary>
    public static async Task<Result> PowerShellAsync(string script, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var full = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var ps = System32(@"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps)) ps = "powershell.exe";
        var r = await RunAsync(ps, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand {encoded}",
            ct, encoding: Encoding.UTF8, timeout: timeout ?? TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        return r with { Output = FromClixml(r.Output), Error = FromClixml(r.Error) };
    }

    /// <summary>Если PowerShell всё же ответил в CLIXML — достаём из него текст ошибок.</summary>
    public static string FromClixml(string text)
    {
        int start = text.IndexOf("#< CLIXML", StringComparison.Ordinal);
        if (start < 0) return text;
        var before = text[..start];
        var errors = System.Text.RegularExpressions.Regex.Matches(text, "<S S=\"Error\">(.*?)</S>", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)
                .Replace("_x000D_", "").Replace("_x000A_", "\n"))
            .Where(s => s.Trim().Length > 0);
        return (before + string.Join("", errors)).Trim();
    }

    /// <summary>
    /// Открыть ссылку или папку от имени обычного пользователя. Из процесса с правами администратора
    /// браузер, запущенный напрямую, тоже получил бы эти права; Проводник передаёт запуск своей копии без них.
    /// </summary>
    /// <summary>Запустить программу с аргументами от имени обычного пользователя (через короткий .cmd и Проводник).</summary>
    public static void RunAsUser(string exe, string args)
    {
        try
        {
            var dir = Package.TempDir;
            Directory.CreateDirectory(dir);
            var cmd = Path.Combine(dir, $"run-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(cmd, $"@chcp 65001 >nul\r\n@start \"\" \"{exe}\" {args}\r\n@del \"%~f0\"\r\n", new UTF8Encoding(false));
            OpenAsUser(cmd);
        }
        catch { }
    }

    public static void OpenAsUser(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = false });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }
    }
}
