using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Updater.Core;

/// <summary>Обновление не удалось — текст для человека.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Самообновление с GitHub. При запуске смотрим последний выпуск; если он новее — качаем Updater.exe рядом
/// с собой, сверяем размер, SHA-256 из выпуска и версию внутри файла, меняем файлы местами и перезапускаемся.
/// Работающий exe переименовать можно, а удалить нельзя: прежний остаётся как «.old» и убирается при следующем запуске.
/// Качаем только с выпусков этого репозитория — чужие ссылки в ответе отбрасываются.
/// </summary>
public static class SelfUpdater
{
    public const string Repo = "HumsteRD/Updater";
    public const string AssetName = Package.ExeName;
    public const string ChecksumName = AssetName + ".sha256";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";
    public const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
    public const string DownloadSuffix = ".download";
    public const string OldSuffix = ".old";

    public sealed record Release(Version Version, string Tag, string ExeUrl, long ExeSize, string ChecksumUrl, string PageUrl, string? Notes);

    /// <summary>Версия этой программы (три числа: 1.1.0).</summary>
    public static Version Current { get; } = Normalize(typeof(SelfUpdater).Assembly.GetName().Version ?? new Version(1, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub требует User-Agent; называемся программой, ничего о пользователе не сообщаем.
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"{Package.AppName}/{Current.ToString(3)}");
        return h;
    }

    public static Version Normalize(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>«v1.2.3» / «1.2» → версия; «v1.2.3-beta» и мусор → null.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    public static bool IsNewer(Version remote, Version current) => Normalize(remote) > Normalize(current);

    public static bool IsOfficialUrl(string? url) =>
        url is not null && url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase) && !url.Contains("..", StringComparison.Ordinal);

    /// <summary>Ответ GitHub API о выпуске → то, что нужно для обновления. null — выпуск не годится.</summary>
    public static Release? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        var tag = Str(root, "tag_name");
        var version = ParseVersion(tag);
        if (version is null) return null;

        string? exeUrl = null, sumUrl = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var name = Str(a, "name");
                var url = Str(a, "browser_download_url");
                if (!IsOfficialUrl(url)) continue;
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl = url;
                    size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var n) ? n : 0;
                }
                else if (string.Equals(name, ChecksumName, StringComparison.OrdinalIgnoreCase))
                    sumUrl = url;
            }
        if (exeUrl is null || sumUrl is null || size <= 0) return null;

        var page = Str(root, "html_url");
        if (page is null || !page.StartsWith("https://github.com/" + Repo + "/", StringComparison.OrdinalIgnoreCase)) page = ReleasesPage;
        return new Release(version, tag!, exeUrl, size, sumUrl, page, Str(root, "body"));
    }

    /// <summary>Последний выпуск на GitHub. Без интернета — быстро сдаёмся (8 секунд), программа работает дальше.</summary>
    public static async Task<(Release? Release, string? Error)> GetLatestAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, $"GitHub ответил {(int)resp.StatusCode}");
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cts.Token).ConfigureAwait(false);
            var r = Parse(doc.RootElement);
            return r is null ? (null, "в последнем выпуске нет подходящего файла") : (r, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "GitHub не ответил вовремя");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Можно ли положить файл рядом с exe (флешка не защищена от записи, папка не системная без прав).</summary>
    public static bool CanWriteNextTo(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (dir is null) return false;
            var probe = Path.Combine(dir, $".{Package.AppName}-write-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Скачать новую версию рядом с exe и проверить её. Возвращает путь к проверенному файлу.</summary>
    public static async Task<string> DownloadAsync(Release r, string exePath, Action<long>? onProgress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(exePath) ?? throw new UpdateException("Не понял, где лежит программа");
        string sumText;
        try { sumText = await Http.GetStringAsync(r.ChecksumUrl, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new UpdateException("Не скачалась контрольная сумма: " + ex.Message); }
        var expected = ParseChecksum(sumText) ?? throw new UpdateException("Контрольная сумма в выпуске повреждена");

        var res = await Downloader.GetAsync(r.ExeUrl, dir, Path.GetFileName(exePath) + DownloadSuffix, onProgress, ct).ConfigureAwait(false);
        if (!res.Ok || res.Path is null) throw new UpdateException("Не скачалось: " + res.Error);
        var problem = Verify(res.Path, r.ExeSize, expected, r.Version);
        if (problem is not null)
        {
            FileCopier.TryDelete(res.Path);
            throw new UpdateException(problem);
        }
        return res.Path;
    }

    /// <summary>null — файл тот самый; иначе — что не так.</summary>
    public static string? Verify(string path, long size, string sha256, Version version)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return "Файл не скачался";
        if (fi.Length != size) return $"Скачалось не целиком: {Fmt.Size(fi.Length)} из {Fmt.Size(size)}";
        if (!VerifyChecksum(path, sha256)) return "Контрольная сумма не совпала — файл повреждён при загрузке";
        if (!VerifyBinary(path, version)) return "В скачанном файле не та программа или не та версия";
        return null;
    }

    /// <summary>«ABCD…  Updater.exe» → «ABCD…» (64 шестнадцатеричных знака).</summary>
    public static string? ParseChecksum(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.Length < 64) return null;
        var h = t[..64];
        if (!h.All(Uri.IsHexDigit) || (t.Length > 64 && !char.IsWhiteSpace(t[64]))) return null;
        return h.ToUpperInvariant();
    }

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static bool VerifyChecksum(string path, string sha256) => Sha256(path).Equals(sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Это наш exe и его версия не ниже заявленной в выпуске.</summary>
    public static bool VerifyBinary(string path, Version atLeast)
    {
        try
        {
            using (var fs = File.OpenRead(path))
                if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') return false;
            var fvi = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(fvi.ProductName, Package.AppName, StringComparison.Ordinal)) return false;
            return new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart) >= Normalize(atLeast);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Поменять файлы местами: текущий exe → «.old», скачанный → на его место. Если второй шаг не удался,
    /// возвращаем всё как было — программа не должна остаться без exe.
    /// Свежий exe несколько секунд может держать антивирус (проверяет новый файл), поэтому каждый шаг
    /// повторяется: attempts раз с паузой 300 мс.
    /// </summary>
    public static void Apply(string exePath, string downloaded, int attempts = 12)
    {
        if (!File.Exists(downloaded)) throw new UpdateException("Скачанный файл пропал");
        var old = exePath + OldSuffix;
        if (File.Exists(old))
        {
            // Прежний «.old» ещё занят (например, не закрылась старая копия) — берём другое имя.
            try { File.Delete(old); }
            catch { old = $"{exePath}.{Guid.NewGuid().ToString("N")[..8]}{OldSuffix}"; }
        }
        try
        {
            MoveWithRetry(exePath, old, attempts);
        }
        catch (Exception ex)
        {
            throw new UpdateException("Не получилось отодвинуть текущую версию: " + ex.Message);
        }
        try
        {
            MoveWithRetry(downloaded, exePath, attempts);
        }
        catch (Exception ex)
        {
            try { MoveWithRetry(old, exePath, attempts); } catch { }
            throw new UpdateException("Не получилось заменить файл: " + ex.Message);
        }
    }

    private static void MoveWithRetry(string from, string to, int attempts)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                File.Move(from, to);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < attempts && File.Exists(from))
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>Журнал обновлений: %LOCALAPPDATA%\Updater\обновления.txt — чтобы было видно, что пошло не так.</summary>
    public static string LogPath => Path.Combine(Package.DataDir, "обновления.txt");

    public static void Note(string text)
    {
        try
        {
            Directory.CreateDirectory(Package.DataDir);
            var f = new FileInfo(LogPath);
            if (f.Exists && f.Length > 200_000) f.Delete();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Убрать хвосты прошлого обновления: «.old» (может быть занят, пока закрывается старая копия) и недокачанное.</summary>
    public static async Task CleanupLeftoversAsync(string exePath, int attempts = 10)
    {
        var dir = Path.GetDirectoryName(exePath);
        if (dir is null) return;
        var name = Path.GetFileName(exePath);
        for (int i = 0; i < attempts; i++)
        {
            bool left = false;
            IEnumerable<string> files;
            try { files = Directory.GetFiles(dir, name + "*" + OldSuffix); }
            catch { return; }
            foreach (var f in files.Append(exePath + DownloadSuffix).Append(exePath + DownloadSuffix + FileCopier.TempSuffix))
            {
                if (!File.Exists(f)) continue;
                try { File.Delete(f); }
                catch { left = true; }
            }
            if (!left) return;
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    /// <summary>Из описания выпуска — до пяти пунктов списка, без ссылок и служебной части GitHub («What's Changed»).</summary>
    public static string SummarizeNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .TakeWhile(l => !l.StartsWith("## What", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("**Full Changelog", StringComparison.OrdinalIgnoreCase))
            .Where(l => (l.StartsWith("- ") || l.StartsWith("* ")) && !l.Contains("http", StringComparison.OrdinalIgnoreCase))
            .Select(l => "•  " + l[2..].Replace("**", "").Replace("`", "").Trim())
            .Take(5);
        return string.Join("\n", lines);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
