using System.Globalization;

namespace Updater.Core;

/// <summary>Числа, размеры и сроки по-русски: «1,2 ГБ», «3 файла», «6 мин».</summary>
public static class Fmt
{
    public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Size(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes} Б";
        string[] units = ["КБ", "МБ", "ГБ", "ТБ"];
        double v = bytes;
        int u = -1;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        string num = v >= 100 ? v.ToString("0", Ru) : v >= 10 ? v.ToString("0.#", Ru) : v.ToString("0.##", Ru);
        return $"{num} {units[u]}";
    }

    public static string Speed(double bytesPerSecond) => Size((long)bytesPerSecond) + "/с";

    /// <summary>Форма слова после числа: 1 файл, 2 файла, 5 файлов.</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        n = Math.Abs(n);
        long n10 = n % 10, n100 = n % 100;
        if (n10 == 1 && n100 != 11) return one;
        if (n10 is >= 2 and <= 4 && (n100 < 12 || n100 > 14)) return few;
        return many;
    }

    public static string Count(long n, string one, string few, string many) =>
        $"{n.ToString("N0", Ru)} {Plural(n, one, few, many)}";

    public static string Files(long n) => Count(n, "файл", "файла", "файлов");

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalSeconds < 60) return $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} с";
        if (t.TotalMinutes < 60) return $"{(int)Math.Round(t.TotalMinutes)} мин";
        int h = (int)t.TotalHours;
        int m = t.Minutes;
        return m == 0 ? $"{h} ч" : $"{h} ч {m} мин";
    }

    public static string Date(DateTime d) => d.ToLocalTime().ToString("d MMMM yyyy, HH:mm", Ru);

    public static string ShortDate(DateTime d) => d.ToLocalTime().ToString("dd.MM.yyyy", Ru);

    /// <summary>Имя, годное для папки или файла: без запрещённых символов и хвостовых точек.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.', ' ');
        if (s.Length == 0) s = "_";
        if (s.Length > 80) s = s[..80].TrimEnd('.', ' ');
        return s;
    }
}
