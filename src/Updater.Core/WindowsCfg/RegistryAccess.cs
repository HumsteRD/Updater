using System.Globalization;
using Microsoft.Win32;

namespace Updater.Core;

/// <summary>Значение реестра в переносимом виде (в сборке и в файле отката).</summary>
public sealed class RegData
{
    /// <summary>absent, dword, qword, sz, expand, multi, binary.</summary>
    public string Type { get; set; } = "absent";
    /// <summary>dword/qword — десятичное число, binary — hex, multi — строки через \n.</summary>
    public string? Value { get; set; }

    public bool IsAbsent => Type == "absent";

    public static RegData Absent() => new();

    public bool SameAs(RegData other)
    {
        if (IsAbsent || other.IsAbsent) return IsAbsent && other.IsAbsent;
        if (Type is "dword" or "qword" && other.Type is "dword" or "qword")
            return Num(Value) == Num(other.Value);
        // Числа в строковых параметрах («1», « 1») сравниваем как числа, остальное — как текст.
        if (long.TryParse(Value?.Trim(), out var a) && long.TryParse(other.Value?.Trim(), out var b)) return a == b;
        return string.Equals(Value?.Trim(), other.Value?.Trim(), StringComparison.Ordinal);
    }

    private static ulong Num(string? v) =>
        ulong.TryParse(v, out var u) ? u : long.TryParse(v, out var l) ? unchecked((ulong)l) : 0;

    public override string ToString() => IsAbsent ? "—" : $"{Type}:{Value}";
}

/// <summary>Доступ к реестру. В проверках подменяется песочницей, чтобы не трогать настоящие настройки.</summary>
public interface IRegistryAccess
{
    RegData Read(string key, string name);
    void Write(string key, string name, RegData data);
    bool KeyExists(string key);
    void DeleteKeyTree(string key);
    void EnsureKey(string key);
}

public class LiveRegistry : IRegistryAccess
{
    protected virtual (RegistryKey Root, string Path) Split(string key)
    {
        int slash = key.IndexOf('\\');
        var hive = slash < 0 ? key : key[..slash];
        var path = slash < 0 ? "" : key[(slash + 1)..];
        RegistryKey root = hive.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            _ => throw new ArgumentException("Неизвестный раздел реестра: " + hive),
        };
        return (root, path);
    }

    public RegData Read(string key, string name)
    {
        var (root, path) = Split(key);
        using (root)
        using (var k = root.OpenSubKey(path))
        {
            if (k is null) return RegData.Absent();
            var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v is null) return RegData.Absent();
            var kind = k.GetValueKind(name);
            return kind switch
            {
                RegistryValueKind.DWord => new RegData { Type = "dword", Value = unchecked((uint)(int)v).ToString(CultureInfo.InvariantCulture) },
                RegistryValueKind.QWord => new RegData { Type = "qword", Value = unchecked((ulong)(long)v).ToString(CultureInfo.InvariantCulture) },
                RegistryValueKind.String => new RegData { Type = "sz", Value = (string)v },
                RegistryValueKind.ExpandString => new RegData { Type = "expand", Value = (string)v },
                RegistryValueKind.MultiString => new RegData { Type = "multi", Value = string.Join("\n", (string[])v) },
                RegistryValueKind.Binary => new RegData { Type = "binary", Value = Convert.ToHexString((byte[])v) },
                _ => RegData.Absent(),
            };
        }
    }

    public void Write(string key, string name, RegData data)
    {
        var (root, path) = Split(key);
        using (root)
        {
            if (data.IsAbsent)
            {
                using var k = root.OpenSubKey(path, writable: true);
                k?.DeleteValue(name, throwOnMissingValue: false);
                return;
            }
            using var w = root.CreateSubKey(path, writable: true)
                ?? throw new UnauthorizedAccessException("Нет доступа к " + key);
            var v = data.Value ?? "";
            switch (data.Type)
            {
                case "dword":
                    w.SetValue(name, unchecked((int)uint.Parse(v, CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
                    break;
                case "qword":
                    w.SetValue(name, unchecked((long)ulong.Parse(v, CultureInfo.InvariantCulture)), RegistryValueKind.QWord);
                    break;
                case "expand":
                    w.SetValue(name, v, RegistryValueKind.ExpandString);
                    break;
                case "multi":
                    w.SetValue(name, v.Split('\n'), RegistryValueKind.MultiString);
                    break;
                case "binary":
                    w.SetValue(name, Convert.FromHexString(v), RegistryValueKind.Binary);
                    break;
                default:
                    w.SetValue(name, v, RegistryValueKind.String);
                    break;
            }
        }
    }

    public bool KeyExists(string key)
    {
        var (root, path) = Split(key);
        using (root)
        using (var k = root.OpenSubKey(path))
            return k is not null;
    }

    public void DeleteKeyTree(string key)
    {
        var (root, path) = Split(key);
        using (root) root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    public void EnsureKey(string key)
    {
        var (root, path) = Split(key);
        using (root)
        using (root.CreateSubKey(path, writable: true)) { }
    }
}

/// <summary>
/// Песочница: HKCU\X → HKCU\Software\Updater.SelfCheck\HKCU\X, HKLM\X → …\HKLM\X.
/// Проверки пишут только туда и потом удаляют ветку целиком.
/// </summary>
public sealed class SandboxRegistry(string root = @"Software\Updater.SelfCheck") : LiveRegistry
{
    public string Root { get; } = root;

    protected override (RegistryKey Root, string Path) Split(string key)
    {
        int slash = key.IndexOf('\\');
        var hive = (slash < 0 ? key : key[..slash]).ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" => "HKCU",
            "HKEY_LOCAL_MACHINE" => "HKLM",
            var h => h,
        };
        var path = slash < 0 ? "" : key[(slash + 1)..];
        return (RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64), $@"{Root}\{hive}\{path}".TrimEnd('\\'));
    }

    public void Wipe()
    {
        using var cu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        cu.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
    }
}
