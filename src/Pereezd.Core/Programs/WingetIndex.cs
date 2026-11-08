using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Pereezd.Core;

/// <summary>
/// Встроенный снимок индекса winget (≈16 тыс. пакетов): коды продуктов из «Программ и компонентов»,
/// нормализованные имена и издатели. По нему установленная программа находит свой пакет так же,
/// как это делает сам winget, — даже на винде, где winget нет.
/// </summary>
public sealed class WingetIndex
{
    public sealed record Package(string Id, string Name);

    private readonly List<Package> _packages = [];
    private readonly Dictionary<string, int[]> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _publishers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _pfns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _byId = new(StringComparer.OrdinalIgnoreCase);

    public string Stamp { get; private set; } = "";
    public int Count => _packages.Count;

    private static readonly Lazy<WingetIndex> _shared = new(LoadEmbedded);
    public static WingetIndex Shared => _shared.Value;

    public static WingetIndex LoadEmbedded()
    {
        using var s = typeof(WingetIndex).Assembly.GetManifestResourceStream("Pereezd.Catalog.winget-index.tsv.gz")
            ?? throw new FileNotFoundException("Нет встроенного индекса winget");
        using var gz = new GZipStream(s, CompressionMode.Decompress);
        return Load(gz);
    }

    public static WingetIndex Load(Stream tsv)
    {
        var idx = new WingetIndex();
        using var r = new StreamReader(tsv, Encoding.UTF8);
        string? line;
        while ((line = r.ReadLine()) is not null)
        {
            if (line.Length < 3) continue;
            var parts = line.Split('\t');
            // Повреждённую строку пропускаем, а не падаем: индекс — подсказка, а не условие работы.
            if (parts.Length < (parts[0] == "P" ? 2 : 3)) continue;
            switch (parts[0])
            {
                case "#":
                    if (parts.Length > 2) idx.Stamp = parts[2];
                    break;
                case "P":
                    idx._byId[parts[1]] = idx._packages.Count;
                    idx._packages.Add(new Package(parts[1], parts.Length > 2 ? parts[2] : parts[1]));
                    break;
                case "C": idx._codes[parts[1]] = Ints(parts[2]); break;
                case "N": idx._names[parts[1]] = Ints(parts[2]); break;
                case "U": idx._publishers[parts[1]] = Ints(parts[2]); break;
                case "F": idx._pfns[parts[1]] = Ints(parts[2]); break;
            }
        }
        return idx;
    }

    private static int[] Ints(string csv) => csv.Split(',').Select(int.Parse).ToArray();

    public Package? Find(string id) => _byId.TryGetValue(id, out var i) ? _packages[i] : null;

    public bool Contains(string id) => _byId.ContainsKey(id);

    public Package? FindByPfn(string familyName) =>
        _pfns.TryGetValue(familyName.ToLowerInvariant(), out var p) && p.Length > 0 ? _packages[p[0]] : null;

    /// <summary>Подобрать пакет для записи из «Программ и компонентов».</summary>
    public Package? Match(string keyName, string displayName, string? publisher, string? version)
    {
        var fromCode = new HashSet<int>();
        if (_codes.TryGetValue(keyName.ToLowerInvariant(), out var c)) fromCode.UnionWith(c);

        var norm = NormalizeName(displayName, out bool x86);
        var fromName = new HashSet<int>();
        foreach (var variant in new[] { norm, norm + (x86 ? "(X86)" : "(X64)") })
            if (variant.Length > 0 && _names.TryGetValue(variant, out var n)) fromName.UnionWith(n);

        var pub = NormalizePublisher(publisher);
        var fromPub = new HashSet<int>();
        if (pub.Length > 0 && _publishers.TryGetValue(pub, out var u)) fromPub.UnionWith(u);

        var nameAndPub = new HashSet<int>(fromName);
        nameAndPub.IntersectWith(fromPub);

        HashSet<int> candidates;
        if (fromCode.Count > 0)
        {
            // Код продукта точнее имени. Имя и издатель только сужают выбор внутри найденного по коду
            // (Desktop Runtime 6, а не 5) и добавляют «родительский» пакет: Google.Chrome для Google.Chrome.EXE.
            candidates = new HashSet<int>(fromCode);
            candidates.IntersectWith(nameAndPub);
            if (candidates.Count == 0) candidates = new HashSet<int>(fromCode);
            foreach (var i in nameAndPub)
                if (fromCode.Any(c => _packages[c].Id.StartsWith(_packages[i].Id + ".", StringComparison.OrdinalIgnoreCase)))
                    candidates.Add(i);
        }
        else if (nameAndPub.Count > 0) candidates = nameAndPub;
        else if (fromName.Count > 0) candidates = fromName;
        else return null;

        var list = candidates.Select(i => _packages[i]).ToList();
        if (list.Count == 1) return list[0];

        // Семейства с версией в Id (Python.Python.3.13) — по установленной версии.
        var mm = MajorMinor(version);
        var byVersion = mm is null ? [] : list.Where(p => p.Id.EndsWith("." + mm, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byVersion.Count == 1) return byVersion[0];

        var ranked = list.OrderBy(Penalty).ThenBy(p => p.Id.Length).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        // Без кода продукта годится только явный победитель: из «Python 3.0 … 3.14» для 3.15 честнее сказать «вручную».
        if (fromCode.Count == 0 && ranked.Count(p => Penalty(p) == Penalty(ranked[0])) > 1) return null;
        return ranked[0];
    }

    private static readonly string[] Noisy =
    [
        "beta", "canary", "dev", "nightly", "preview", "insiders", "insider", "alpha", "rc", "prerelease",
        "arm64", "arm", "exe", "msi", "lt2", "portable", "legacy", "unstable", "developer", "lts",
    ];

    private static int Penalty(Package p)
    {
        int score = 0;
        var segs = p.Id.Split('.', '-');
        foreach (var s in segs.Skip(1))
            if (Noisy.Contains(s.ToLowerInvariant())) score += 2;
        if (p.Id.Contains("Enhanced", StringComparison.OrdinalIgnoreCase)) score += 2;
        return score;
    }

    private static string? MajorMinor(string? version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        var m = Regex.Match(version, @"^(\d+)\.(\d+)");
        return m.Success ? $"{m.Groups[1].Value}.{int.Parse(m.Groups[2].Value)}" : null;
    }

    // ───────────────────────────── Нормализация как в winget ─────────────────────────────

    private static readonly Regex ArchRx = new(
        @"\(?\b(x64|x86_64|x86|amd64|arm64|win64|win32|ia32|64[- ]?bit|32[- ]?bit)\b\)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionRx = new(
        @"(\bversion\s*)?\bv?\d+(\.\d+)+[a-z0-9\-\+]*|\(\d+\)|\bversion\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScopeRx = new(@"\((user|machine|system|per[- ]user|all users)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocaleRx = new(@"\b[a-z]{2}-[a-z]{2}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string NormalizeName(string name, out bool x86)
    {
        var s = name.ToLowerInvariant();
        x86 = Regex.IsMatch(s, @"\b(x86|32[- ]?bit|win32|ia32)\b");
        s = ScopeRx.Replace(s, " ");
        s = ArchRx.Replace(s, " ");
        s = VersionRx.Replace(s, " ");
        s = LocaleRx.Replace(s, " ");
        return Squash(s);
    }

    private static readonly HashSet<string> LegalWords =
    [
        "inc", "incorporated", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "gmbh", "ag",
        "sa", "sas", "srl", "bv", "pty", "plc", "oy", "ab", "kg", "llp", "lp", "sro", "spzoo", "kk", "nv", "spa",
    ];

    public static string NormalizePublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return "";
        var words = Regex.Split(publisher.ToLowerInvariant(), @"[\s,]+")
            .Select(w => Squash(w))
            .Where(w => w.Length > 0 && !LegalWords.Contains(w));
        return string.Concat(words);
    }

    private static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }
}
