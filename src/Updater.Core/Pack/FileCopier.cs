using System.Buffers;
using System.Text.RegularExpressions;

namespace Updater.Core;

/// <summary>
/// Копирование папок «файл в файл» с докачкой: уже скопированные файлы (тот же размер и время) пропускаются,
/// поэтому прерванные сборы можно просто запустить ещё раз. Файл сначала пишется под временным именем —
/// оборванный на середине файл никогда не выдаётся за целый.
/// На FAT32 файлы больше 4 ГБ режутся на части «имя.__part001», при восстановлении части склеиваются.
/// </summary>
public static class FileCopier
{
    public const string PartSuffix = ".__part";
    public const string TempSuffix = ".updater-tmp";
    /// <summary>Чуть меньше предела FAT32 (4 ГиБ − 1 байт).</summary>
    public const long Fat32Split = 4_000_000_000;

    private const int BufferSize = 1 << 20;
    private static readonly Regex PartRx = new(@"^(?<base>.+)\.__part(?<n>\d{3})$", RegexOptions.Compiled);

    public sealed class Outcome
    {
        public long Bytes;
        public int Files;
        public int Skipped;
        public long SkippedBytes;
        public int Kept;
        public List<string> Failed { get; } = [];
        public bool DiskFull;
    }

    public enum Conflict
    {
        /// <summary>Заменять, только если у нас файл новее (или другой при той же дате).</summary>
        NewerWins,
        Overwrite,
    }

    /// <summary>Сборы: папка-источник → папка в сборке.</summary>
    public static async Task<Outcome> MirrorAsync(string source, string dest, GlobSet exclude, bool topOnly, long splitSize,
        JobState state, WalkStats? stats, CancellationToken ct)
    {
        var o = new Outcome();
        foreach (var f in FileWalker.Walk(source, exclude, stats, ct, topOnly))
        {
            ct.ThrowIfCancellationRequested();
            state.WaitIfPaused(ct);
            var target = Path.Combine(dest, f.RelativePath);
            state.CurrentFile = f.RelativePath;
            try
            {
                if (f.Length > splitSize)
                {
                    if (PartsUpToDate(target, f.Length, f.LastWriteUtc, splitSize))
                    {
                        Skip(o, state, f.Length);
                        continue;
                    }
                    await CopySplitAsync(f.FullPath, target, f.Length, f.LastWriteUtc, splitSize, state, ct).ConfigureAwait(false);
                }
                else
                {
                    if (SameFile(target, f.Length, f.LastWriteUtc))
                    {
                        Skip(o, state, f.Length);
                        continue;
                    }
                    await CopyOneAsync(f.FullPath, target, f.LastWriteUtc, state, ct).ConfigureAwait(false);
                }
                o.Bytes += f.Length;
                o.Files++;
                state.FileDone();
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                o.DiskFull = true;
                o.Failed.Add(f.RelativePath + " — нет места");
                break;
            }
            catch (Exception ex)
            {
                o.Failed.Add($"{f.RelativePath} — {Explain(ex)}");
                state.Fail();
                state.Add(f.Length);
            }
        }
        return o;
    }

    /// <summary>Распаковка: папка в сборке → настоящее место на новой винде, со склейкой частей.</summary>
    public static async Task<Outcome> RestoreAsync(string storedDir, string target, Conflict conflict, JobState state, CancellationToken ct)
    {
        var o = new Outcome();
        if (!Directory.Exists(storedDir)) return o;
        var files = FileWalker.Walk(storedDir, ct: ct).ToList();
        var parts = files.Where(f => PartRx.IsMatch(Path.GetFileName(f.RelativePath)))
            .GroupBy(f => PartRx.Match(f.RelativePath).Groups["base"].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            state.WaitIfPaused(ct);
            var name = Path.GetFileName(f.RelativePath);
            if (name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            {
                state.Add(f.Length);
                continue;
            }
            if (PartRx.IsMatch(name)) continue; // части собираются ниже, целиком

            var dst = SafeCombine(target, f.RelativePath);
            state.CurrentFile = f.RelativePath;
            try
            {
                if (ShouldSkip(dst, f.Length, f.LastWriteUtc, conflict, o))
                {
                    state.Add(f.Length);
                    continue;
                }
                await CopyOneAsync(f.FullPath, dst, f.LastWriteUtc, state, ct).ConfigureAwait(false);
                o.Bytes += f.Length;
                o.Files++;
                state.FileDone();
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                o.DiskFull = true;
                o.Failed.Add(f.RelativePath + " — нет места");
                return o;
            }
            catch (Exception ex)
            {
                o.Failed.Add($"{f.RelativePath} — {Explain(ex)}");
                state.Fail();
                state.Add(f.Length);
            }
        }

        foreach (var (baseRel, list) in parts)
        {
            ct.ThrowIfCancellationRequested();
            var dst = SafeCombine(target, baseRel);
            long total = list.Sum(p => p.Length);
            var mtime = list[0].LastWriteUtc;
            state.CurrentFile = baseRel;
            try
            {
                if (ShouldSkip(dst, total, mtime, conflict, o))
                {
                    state.Add(total);
                    continue;
                }
                await JoinPartsAsync(list.Select(p => p.FullPath).ToList(), dst, mtime, state, ct).ConfigureAwait(false);
                o.Bytes += total;
                o.Files++;
                state.FileDone();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                o.Failed.Add($"{baseRel} — {Explain(ex)}");
                state.Fail();
            }
        }
        return o;
    }

    private static void Skip(Outcome o, JobState state, long length)
    {
        o.Skipped++;
        o.SkippedBytes += length;
        state.Add(length);
        state.FileDone();
    }

    private static bool ShouldSkip(string dst, long length, DateTime mtimeUtc, Conflict conflict, Outcome o)
    {
        if (!File.Exists(dst)) return false;
        var fi = new FileInfo(dst);
        if (fi.Length == length && Math.Abs((fi.LastWriteTimeUtc - mtimeUtc).TotalSeconds) <= 2)
        {
            o.Skipped++;
            return true;
        }
        // На новой винде файл уже успели поменять, и он новее того, что в сборке, — не затираем.
        if (conflict == Conflict.NewerWins && fi.LastWriteTimeUtc > mtimeUtc.AddSeconds(2))
        {
            o.Kept++;
            return true;
        }
        return false;
    }

    /// <summary>Защита от путей вида «..\..\Windows» в чужой или испорченной сборке.</summary>
    public static string SafeCombine(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('\\');
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (!full.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase) && !full.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь выходит за пределы папки: " + relative);
        return full;
    }

    public static bool SameFile(string path, long length, DateTime mtimeUtc)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var fi = new FileInfo(path);
            // У FAT32 точность времени — 2 секунды.
            return fi.Length == length && Math.Abs((fi.LastWriteTimeUtc - mtimeUtc).TotalSeconds) <= 2;
        }
        catch
        {
            return false;
        }
    }

    private static bool PartsUpToDate(string target, long length, DateTime mtimeUtc, long splitSize)
    {
        long sum = 0;
        int count = (int)((length + splitSize - 1) / splitSize);
        for (int i = 1; i <= count; i++)
        {
            var p = PartName(target, i);
            if (!File.Exists(p)) return false;
            var fi = new FileInfo(p);
            if (Math.Abs((fi.LastWriteTimeUtc - mtimeUtc).TotalSeconds) > 2) return false;
            sum += fi.Length;
        }
        return sum == length;
    }

    public static string PartName(string target, int index) => $"{target}{PartSuffix}{index:000}";

    /// <summary>Чтение следующего куска идёт одновременно с записью текущего: на флешке это заметно быстрее.</summary>
    public static async Task CopyOneAsync(string src, string dst, DateTime mtimeUtc, JobState state, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var tmp = dst + TempSuffix;
        var a = ArrayPool<byte>.Shared.Rent(BufferSize);
        var b = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 0,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int n = await input.ReadAsync(a.AsMemory(0, BufferSize), ct).ConfigureAwait(false);
                while (n > 0)
                {
                    state.WaitIfPaused(ct);
                    var write = output.WriteAsync(a.AsMemory(0, n), ct);
                    var read = input.ReadAsync(b.AsMemory(0, BufferSize), ct);
                    await write.ConfigureAwait(false);
                    state.Add(n);
                    n = await read.ConfigureAwait(false);
                    (a, b) = (b, a);
                }
            }
            File.Move(tmp, dst, overwrite: true);
            TrySetTime(dst, mtimeUtc);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    private static async Task CopySplitAsync(string src, string target, long length, DateTime mtimeUtc, long splitSize,
        JobState state, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            int part = 1;
            long left = length;
            while (left > 0)
            {
                var name = PartName(target, part);
                var tmp = name + TempSuffix;
                long chunk = Math.Min(splitSize, left);
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 0,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    long written = 0;
                    while (written < chunk)
                    {
                        state.WaitIfPaused(ct);
                        int want = (int)Math.Min(BufferSize, chunk - written);
                        int n = await input.ReadAsync(buf.AsMemory(0, want), ct).ConfigureAwait(false);
                        if (n == 0) break;
                        await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        written += n;
                        state.Add(n);
                    }
                    left -= written;
                    if (written < chunk) left = 0; // файл укоротился, пока его читали
                }
                File.Move(tmp, name, overwrite: true);
                TrySetTime(name, mtimeUtc);
                part++;
            }
            // Если раньше файл был длиннее и частей было больше — лишние удаляем.
            for (int extra = part; File.Exists(PartName(target, extra)); extra++) TryDelete(PartName(target, extra));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static async Task JoinPartsAsync(List<string> parts, string dst, DateTime mtimeUtc, JobState state, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var tmp = dst + TempSuffix;
        var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 0,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                foreach (var p in parts)
                {
                    await using var input = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 0,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    int n;
                    while ((n = await input.ReadAsync(buf.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                    {
                        state.WaitIfPaused(ct);
                        await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        state.Add(n);
                    }
                }
            }
            File.Move(tmp, dst, overwrite: true);
            TrySetTime(dst, mtimeUtc);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public static void TrySetTime(string path, DateTime mtimeUtc)
    {
        try { File.SetLastWriteTimeUtc(path, mtimeUtc); } catch { }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static bool IsDiskFull(IOException ex) =>
        (ex.HResult & 0xFFFF) is 0x70 or 0x27; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

    public static string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "нет доступа",
        IOException io when (io.HResult & 0xFFFF) is 0x20 or 0x21 => "занят другой программой",
        PathTooLongException => "слишком длинный путь",
        FileNotFoundException => "файл пропал во время копирования",
        DirectoryNotFoundException => "папка пропала во время копирования",
        _ => ex.Message,
    };
}
