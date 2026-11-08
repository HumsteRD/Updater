namespace Updater.Core;

/// <summary>Скачивание файлов с показом прогресса и докачкой: уже скачанный установщик второй раз не берём.</summary>
public static class Downloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Updater/1.0");
        return h;
    }

    public sealed record Result(bool Ok, string? Path, long Bytes, string? Error);

    /// <summary>
    /// Скачать в dir под именем fileName. Если файл уже есть и сервер отдаёт тот же размер — не перекачиваем.
    /// onProgress получает скачанные байты (для общего счётчика работы).
    /// </summary>
    public static async Task<Result> GetAsync(string url, string dir, string fileName, Action<long>? onProgress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, fileName);
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            if (total is { } size && File.Exists(target) && new FileInfo(target).Length == size)
            {
                onProgress?.Invoke(size);
                return new Result(true, target, size, null);
            }

            var tmp = target + FileCopier.TempSuffix;
            long written = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    written += n;
                    onProgress?.Invoke(n);
                }
            }
            File.Move(tmp, target, overwrite: true);
            return new Result(true, target, written, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileCopier.TryDelete(target + FileCopier.TempSuffix);
            return new Result(false, null, 0, ex.Message);
        }
    }
}
