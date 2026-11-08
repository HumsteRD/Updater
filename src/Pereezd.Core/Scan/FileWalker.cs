namespace Pereezd.Core;

public readonly record struct WalkedFile(string FullPath, string RelativePath, long Length, DateTime LastWriteUtc);

/// <summary>Что обход пропустил — чтобы честно сказать об этом в отчёте.</summary>
public sealed class WalkStats
{
    private long _cloudFiles, _cloudBytes, _links, _denied;
    public long CloudFiles => Interlocked.Read(ref _cloudFiles);
    public long CloudBytes => Interlocked.Read(ref _cloudBytes);
    public long Links => Interlocked.Read(ref _links);
    public long Denied => Interlocked.Read(ref _denied);

    internal void Cloud(long bytes)
    {
        Interlocked.Increment(ref _cloudFiles);
        Interlocked.Add(ref _cloudBytes, bytes);
    }

    internal void Link() => Interlocked.Increment(ref _links);
    internal void Deny() => Interlocked.Increment(ref _denied);
}

/// <summary>
/// Обход папки без сюрпризов: ссылки и точки соединения не разворачиваются (иначе папка профиля
/// зацикливается через «Application Data»), файлы «только в облаке» OneDrive не скачиваются,
/// недоступные папки пропускаются, а исключённые — даже не открываются.
/// </summary>
public static class FileWalker
{
    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS / RECALL_ON_OPEN: содержимое файла лежит в облаке.
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>Мусор, который никому не нужен ни на старой винде, ни на новой.</summary>
    public static readonly GlobSet AlwaysSkip = new(["Thumbs.db", "~$*", "*.~tmp", "desktop.ini.bak"]);

    public static IEnumerable<WalkedFile> Walk(string root, GlobSet? exclude = null, WalkStats? stats = null,
        CancellationToken ct = default, bool topOnly = false)
    {
        exclude ??= GlobSet.Empty;

        if (File.Exists(root))
        {
            var fi = new FileInfo(root);
            if (IsCloudOnly(fi.Attributes))
            {
                stats?.Cloud(fi.Length);
                yield break;
            }
            yield return new WalkedFile(fi.FullName, fi.Name, fi.Length, fi.LastWriteTimeUtc);
            yield break;
        }
        if (!Directory.Exists(root)) yield break;

        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        var baseLen = root.TrimEnd('\\').Length + 1;
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToList();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                stats?.Deny();
                continue;
            }

            foreach (var e in entries)
            {
                string rel = e.FullName.Length > baseLen ? e.FullName[baseLen..] : e.Name;
                if (e is DirectoryInfo d)
                {
                    if (d.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsLink(d))
                    {
                        stats?.Link();
                        continue;
                    }
                    if (topOnly || exclude.Matches(rel)) continue;
                    stack.Push(d.FullName);
                }
                else if (e is FileInfo f)
                {
                    if (exclude.Matches(rel) || AlwaysSkip.Matches(rel)) continue;
                    long len;
                    FileAttributes attrs;
                    DateTime mtime;
                    try
                    {
                        len = f.Length;
                        attrs = f.Attributes;
                        mtime = f.LastWriteTimeUtc;
                    }
                    catch
                    {
                        stats?.Deny();
                        continue;
                    }
                    if (IsCloudOnly(attrs))
                    {
                        stats?.Cloud(len);
                        continue;
                    }
                    yield return new WalkedFile(f.FullName, rel, len, mtime);
                }
            }
        }
    }

    public static bool IsCloudOnly(FileAttributes a) =>
        (a & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;

    private static bool IsLink(FileSystemInfo d)
    {
        // Ссылки и точки соединения отдают цель; облачные папки OneDrive — тоже точки повторной обработки,
        // но цели у них нет, и в них надо заходить.
        try { return d.LinkTarget is not null; }
        catch { return true; }
    }

    /// <summary>Размер и число файлов.</summary>
    public static (long Bytes, int Files) Measure(string root, GlobSet? exclude = null, WalkStats? stats = null,
        CancellationToken ct = default, bool topOnly = false)
    {
        long bytes = 0;
        int files = 0;
        foreach (var f in Walk(root, exclude, stats, ct, topOnly))
        {
            bytes += f.Length;
            files++;
        }
        return (bytes, files);
    }
}
