using System.Text;
using System.Text.RegularExpressions;

namespace Pereezd.Core;

/// <summary>
/// Набор масок исключения в духе .gitignore.
/// Маска без «/» сравнивается с именем файла или папки на любой глубине («Cache», «*.tmp»).
/// Маска с «/» — со всем относительным путём, «**» означает любое число папок («**/Service Worker/CacheStorage»).
/// Регистр не важен.
/// </summary>
public sealed class GlobSet
{
    private readonly List<Regex> _byName = [];
    private readonly List<Regex> _byPath = [];

    public static readonly GlobSet Empty = new([]);

    public GlobSet(IEnumerable<string>? patterns)
    {
        if (patterns is null) return;
        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var t = raw.Replace('\\', '/').Trim();
            // «/имя» — только в корне (как в .gitignore), без «/» — на любой глубине.
            bool anchored = t.StartsWith('/');
            var p = t.Trim('/');
            if (p.Length == 0) continue;
            if (anchored || p.Contains('/')) _byPath.Add(Compile(p));
            else _byName.Add(Compile(p));
        }
    }

    public bool IsEmpty => _byName.Count == 0 && _byPath.Count == 0;

    /// <summary>Подходит ли относительный путь (файла или папки) под одну из масок.</summary>
    public bool Matches(string relativePath)
    {
        if (IsEmpty) return false;
        var rel = relativePath.Replace('\\', '/').Trim('/');
        int slash = rel.LastIndexOf('/');
        var name = slash >= 0 ? rel[(slash + 1)..] : rel;
        foreach (var r in _byName)
            if (r.IsMatch(name)) return true;
        foreach (var r in _byPath)
            if (r.IsMatch(rel)) return true;
        return false;
    }

    public static GlobSet Of(params IEnumerable<string>?[] lists) =>
        new(lists.Where(l => l is not null).SelectMany(l => l!));

    private static Regex Compile(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                bool dbl = i + 1 < glob.Length && glob[i + 1] == '*';
                if (dbl)
                {
                    bool slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                    sb.Append(slashAfter ? "(?:.*/)?" : ".*");
                    i += slashAfter ? 2 : 1;
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
