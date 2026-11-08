using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace Updater.Core;

/// <summary>
/// Настройки программы или сохранения игры — в один zip: тысячи мелких файлов на флешку пишутся медленно,
/// а один архив — быстро. Внутри: «0/…», «1/…» — по корню на папку, «reg/N.reg» — ветки реестра.
/// Открывается и обычным Проводником.
/// </summary>
public static class ZipPacker
{
    /// <summary>Уже сжатое пережимать бесполезно — только время тратить.</summary>
    private static readonly HashSet<string> Stored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".heic", ".mp4", ".mkv", ".webm", ".mov", ".avi", ".mp3",
        ".ogg", ".opus", ".flac", ".m4a", ".aac", ".zip", ".7z", ".rar", ".gz", ".xz", ".zst", ".cab", ".msi", ".pak",
        ".vpk", ".crx", ".xpi", ".woff", ".woff2", ".jar", ".gguf", ".bin", ".pdf", ".docx", ".xlsx", ".pptx",
    };

    public sealed class Outcome
    {
        public long Bytes;
        public int Files;
        public List<string> Failed { get; } = [];
        public List<string> RegFiles { get; } = [];
        public bool DiskFull;
    }

    public static async Task<Outcome> PackAsync(string zipPath, IReadOnlyList<(string Full, GlobSet Exclude, bool TopOnly)> roots,
        IReadOnlyList<string> registryKeys, JobState state, WalkStats? stats, bool sandbox, CancellationToken ct)
    {
        var o = new Outcome();
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        var tmp = zipPath + FileCopier.TempSuffix;
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    var (full, exclude, topOnly) = roots[i];
                    bool isFile = File.Exists(full);
                    foreach (var f in FileWalker.Walk(full, exclude, stats, ct, topOnly))
                    {
                        ct.ThrowIfCancellationRequested();
                        state.WaitIfPaused(ct);
                        state.CurrentFile = f.RelativePath;
                        FileStream input;
                        try
                        {
                            // Открываем до создания записи: не открылся (занят) — записи в архиве не будет.
                            input = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                        }
                        catch (Exception ex)
                        {
                            o.Failed.Add($"{f.RelativePath} — {FileCopier.Explain(ex)}");
                            state.Fail();
                            state.Add(f.Length);
                            continue;
                        }
                        await using (input)
                        {
                            var name = $"{i}/{(isFile ? Path.GetFileName(full) : f.RelativePath).Replace('\\', '/')}";
                            // Большие файлы — почти всегда видео, модели и архивы: сжатие только тормозит.
                            var level = Stored.Contains(Path.GetExtension(f.FullPath)) || f.Length > 64L * 1024 * 1024
                                ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                            var entry = zip.CreateEntry(name, level);
                            entry.LastWriteTime = DosSafe(f.LastWriteUtc);
                            await using var es = entry.Open();
                            int n;
                            long copied = 0;
                            while ((n = await input.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                            {
                                await es.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                                state.Add(n);
                                copied += n;
                            }
                            o.Bytes += copied;
                            o.Files++;
                            state.FileDone();
                        }
                    }
                }

                // Ветки реестра программы (7-Zip, PuTTY…) — через reg export, на новой винде reg import.
                for (int r = 0; r < registryKeys.Count; r++)
                {
                    if (sandbox) break;
                    var regTmp = Path.Combine(Path.GetTempPath(), $"updater-{Guid.NewGuid():N}.reg");
                    try
                    {
                        var res = await Proc.RunAsync(Proc.System32("reg.exe"), $"export \"{registryKeys[r]}\" \"{regTmp}\" /y", ct).ConfigureAwait(false);
                        if (!res.Ok || !File.Exists(regTmp))
                        {
                            o.Failed.Add($"реестр {registryKeys[r]} — не выгрузился");
                            continue;
                        }
                        var name = $"reg/{r}.reg";
                        zip.CreateEntryFromFile(regTmp, name, CompressionLevel.Fastest);
                        o.RegFiles.Add(name);
                    }
                    finally
                    {
                        FileCopier.TryDelete(regTmp);
                    }
                }
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        catch (IOException ex) when (FileCopier.IsDiskFull(ex))
        {
            o.DiskFull = true;
            FileCopier.TryDelete(tmp);
        }
        catch
        {
            FileCopier.TryDelete(tmp);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return o;
    }

    /// <summary>Распаковать пункт: корень N — в свою папку на новой винде; reg-файлы отдаются наружу для импорта.</summary>
    public static async Task<(FileCopier.Outcome Result, List<string> RegFiles)> UnpackAsync(string zipPath,
        IReadOnlyList<(StoredRoot Root, string Target)> roots, string regTempDir, JobState state, CancellationToken ct)
    {
        var o = new FileCopier.Outcome();
        var regs = new List<string>();
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Read, Encoding.UTF8);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                state.WaitIfPaused(ct);
                if (entry.FullName.EndsWith('/')) continue;
                var slash = entry.FullName.IndexOf('/');
                if (slash < 0) continue;
                var prefix = entry.FullName[..slash];
                var rel = entry.FullName[(slash + 1)..];

                if (prefix == "reg")
                {
                    Directory.CreateDirectory(regTempDir);
                    var regFile = Path.Combine(regTempDir, $"{Guid.NewGuid():N}.reg");
                    entry.ExtractToFile(regFile, overwrite: true);
                    regs.Add(regFile);
                    continue;
                }

                var match = roots.FirstOrDefault(r => r.Root.Entry == prefix);
                if (match.Target is null)
                {
                    state.Add(entry.Length);
                    continue;
                }
                string dst;
                try
                {
                    dst = match.Root.IsFile ? match.Target : FileCopier.SafeCombine(match.Target, rel.Replace('/', '\\'));
                }
                catch (InvalidDataException ex)
                {
                    o.Failed.Add(ex.Message);
                    continue;
                }
                state.CurrentFile = rel;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    var tmp = dst + FileCopier.TempSuffix;
                    await using (var es = entry.Open())
                    await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        int n;
                        while ((n = await es.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                            state.Add(n);
                        }
                    }
                    File.Move(tmp, dst, overwrite: true);
                    FileCopier.TrySetTime(dst, entry.LastWriteTime.UtcDateTime);
                    o.Files++;
                    o.Bytes += entry.Length;
                    state.FileDone();
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException ex) when (FileCopier.IsDiskFull(ex))
                {
                    o.DiskFull = true;
                    o.Failed.Add(rel + " — нет места");
                    break;
                }
                catch (Exception ex)
                {
                    o.Failed.Add($"{rel} — {FileCopier.Explain(ex)}");
                    state.Fail();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return (o, regs);
    }

    /// <summary>Сколько байт внутри архива (для прогресса распаковки).</summary>
    public static long UncompressedSize(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Sum(e => e.Length);
        }
        catch
        {
            return 0;
        }
    }

    private static DateTimeOffset DosSafe(DateTime utc)
    {
        var local = utc.ToLocalTime();
        if (local.Year < 1980) local = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        if (local.Year > 2107) local = new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Local);
        return new DateTimeOffset(local);
    }
}
