using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Updater.Core;

/// <summary>
/// Сторонние драйверы (oemNN.inf). Пакет драйвера — папка в DriverStore\FileRepository: в ней INF, файлы и
/// подписанный каталог, этого достаточно, чтобы pnputil поставил драйвер на новой винде без интернета.
/// </summary>
public static class Drivers
{
    public static string FileRepository =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");

    private static readonly Dictionary<string, string> ClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NET"] = "Сеть", ["DISPLAY"] = "Видеокарта", ["MEDIA"] = "Звук", ["SYSTEM"] = "Системные устройства",
        ["USB"] = "USB", ["HIDCLASS"] = "Устройства ввода", ["KEYBOARD"] = "Клавиатура", ["MOUSE"] = "Мышь",
        ["BLUETOOTH"] = "Bluetooth", ["IMAGE"] = "Камеры и сканеры", ["CAMERA"] = "Камера", ["PRINTER"] = "Принтер",
        ["MONITOR"] = "Монитор", ["SCSIADAPTER"] = "Контроллеры дисков", ["HDC"] = "Контроллеры дисков",
        ["BIOMETRIC"] = "Биометрия", ["SOFTWARECOMPONENT"] = "Компонент", ["EXTENSION"] = "Дополнение",
        ["AUDIOENDPOINT"] = "Звук", ["PORTS"] = "Порты", ["SECURITYDEVICES"] = "Безопасность", ["SOFTWAREDEVICE"] = "Программное устройство",
        ["NETSERVICE"] = "Сетевая служба", ["NETTRANS"] = "Сетевой протокол", ["VOLUME"] = "Тома", ["DISKDRIVE"] = "Диски",
    };

    public static string ClassName(string cls) => ClassNames.TryGetValue(cls, out var n) ? n : cls;

    private static readonly Regex VirtualDevice = new(
        @"virtual|tunnel|\btap\b|vpn|wintun|wireguard|hyper-v|vmware|virtualbox|loopback|npcap|windivert",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Брать ли по умолчанию. Видеодрайвер огромный и лучше свежий с сайта; виртуальные адаптеры (VPN, туннели)
    /// ставят сами их программы. Остальное — да: особенно сеть, чтобы на новой винде сразу был интернет.
    /// </summary>
    public static bool DefaultSelected(DriverRecord d) =>
        !d.Class.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) &&
        !Regex.IsMatch(d.Provider, "NVIDIA", RegexOptions.IgnoreCase) &&
        !VirtualDevice.IsMatch(d.Device) && !VirtualDevice.IsMatch(d.Provider);

    public static List<DriverRecord> List()
    {
        var byInf = new Dictionary<string, DriverRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, DeviceClass, DriverProviderName, DriverVersion, InfName FROM Win32_PnPSignedDriver");
            searcher.Options.Timeout = TimeSpan.FromSeconds(60);
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var inf = mo["InfName"] as string;
                    if (string.IsNullOrEmpty(inf) || !inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase)) continue;
                    var device = (mo["DeviceName"] as string)?.Trim() ?? "";
                    if (byInf.TryGetValue(inf, out var existing))
                    {
                        if (device.Length > 0 && !existing.Device.Contains(device, StringComparison.OrdinalIgnoreCase) && existing.Device.Length < 120)
                            existing.Device = existing.Device.Length == 0 ? device : existing.Device + "; " + device;
                        continue;
                    }
                    byInf[inf] = new DriverRecord
                    {
                        Inf = inf,
                        Device = device,
                        Class = (mo["DeviceClass"] as string)?.Trim() ?? "",
                        Provider = (mo["DriverProviderName"] as string)?.Trim() ?? "",
                        Version = (mo["DriverVersion"] as string)?.Trim() ?? "",
                    };
                }
            }
        }
        catch { }

        foreach (var d in byInf.Values)
        {
            d.Package = PackageFolder(d.Inf) ?? "";
            if (d.Package.Length > 0)
            {
                var dir = Path.Combine(FileRepository, d.Package);
                if (Directory.Exists(dir)) d.Bytes = FileWalker.Measure(dir).Bytes;
            }
        }
        return byInf.Values
            .Where(d => d.Package.Length > 0)
            .OrderBy(d => ClassName(d.Class), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.Device, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// oem12.inf → имя папки пакета в FileRepository. Сначала через DriverDatabase, а если там записи нет
    /// (так бывает у драйверов, поставленных установщиками), — по совпадению содержимого: Windows\INF\oem12.inf —
    /// точная копия INF из папки пакета.
    /// </summary>
    public static string? PackageFolder(string oemInf)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\DriverDatabase\DriverInfFiles\{oemInf}");
            if (k?.GetValue("") is string[] multi && multi.Length > 0 && Directory.Exists(Path.Combine(FileRepository, multi[^1])))
                return multi[^1];
            if (k?.GetValue("") is string s && s.Length > 0 && Directory.Exists(Path.Combine(FileRepository, s))) return s;
        }
        catch { }

        try
        {
            var oemPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", oemInf);
            if (!File.Exists(oemPath)) return null;
            var bytes = File.ReadAllBytes(oemPath);
            var bySize = RepositoryIndex();
            if (!bySize.TryGetValue(bytes.LongLength, out var candidates)) return null;
            foreach (var c in candidates)
            {
                try
                {
                    if (File.ReadAllBytes(c).AsSpan().SequenceEqual(bytes))
                        return Path.GetFileName(Path.GetDirectoryName(c));
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static Dictionary<long, List<string>>? _repoIndex;

    /// <summary>INF-файлы всех пакетов хранилища драйверов, сгруппированные по размеру (для быстрого сравнения).</summary>
    private static Dictionary<long, List<string>> RepositoryIndex()
    {
        if (_repoIndex is not null) return _repoIndex;
        var index = new Dictionary<long, List<string>>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(FileRepository))
            {
                try
                {
                    foreach (var inf in Directory.EnumerateFiles(dir, "*.inf"))
                    {
                        var len = new FileInfo(inf).Length;
                        if (!index.TryGetValue(len, out var list)) index[len] = list = [];
                        list.Add(inf);
                    }
                }
                catch { }
            }
        }
        catch { }
        return _repoIndex = index;
    }

    /// <summary>Поставить драйверы из папки сборки (нужны права администратора).</summary>
    public static Task<Proc.Result> InstallAsync(string folder, CancellationToken ct) =>
        Proc.RunAsync(Proc.System32("pnputil.exe"), $"/add-driver \"{Path.Combine(folder, "*.inf")}\" /subdirs /install", ct,
            timeout: TimeSpan.FromMinutes(20));
}

public static class Wifi
{
    public static async Task<List<string>> ProfilesAsync(CancellationToken ct = default)
    {
        var r = await Proc.RunAsync(Proc.System32("netsh.exe"), "wlan show profiles", ct, timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var list = new List<string>();
        if (!r.Ok) return list;
        foreach (var line in r.Output.Split('\n'))
        {
            // «    Все профили пользователей     : MyWiFi» / «    All User Profile     : MyWiFi»
            var m = Regex.Match(line, @"^\s{2,}\S.*?\s+:\s(.+?)\s*$");
            if (m.Success) list.Add(m.Groups[1].Value);
        }
        return list.Distinct().ToList();
    }

    /// <summary>Сохранить сети с паролями открытым текстом (key=clear работает только с правами администратора).</summary>
    public static Task<Proc.Result> ExportAsync(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        return Proc.RunAsync(Proc.System32("netsh.exe"), $"wlan export profile key=clear folder=\"{folder}\"", ct, timeout: TimeSpan.FromSeconds(60));
    }

    public static Task<Proc.Result> ImportAsync(string xmlFile, CancellationToken ct) =>
        Proc.RunAsync(Proc.System32("netsh.exe"), $"wlan add profile filename=\"{xmlFile}\" user=all", ct, timeout: TimeSpan.FromSeconds(30));

    /// <param name="Name">Имя сети (из самого профиля, а не из имени файла: netsh пишет «Wi-Fi-Дом.xml»).</param>
    /// <param name="NeedsKey">Сеть с паролем (WPA/WPA2/WPA3-Personal, WEP). Открытым и корпоративным он не нужен.</param>
    /// <param name="KeyClear">Пароль лежит открытым текстом — встанет на любом ПК. Зашифрованный («protected»)
    /// расшифровывается только на старом ПК: так бывает, если сохранять без прав администратора.</param>
    public sealed record ProfileInfo(string Name, bool NeedsKey, bool KeyClear)
    {
        public bool PasswordLost => NeedsKey && !KeyClear;
    }

    public static ProfileInfo? Inspect(string xmlFile)
    {
        try
        {
            var root = System.Xml.Linq.XDocument.Load(xmlFile).Root;
            if (root is null) return null;
            System.Xml.Linq.XElement? First(string local) => root.Descendants().FirstOrDefault(e => e.Name.LocalName == local);
            var name = root.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value
                       ?? First("SSID")?.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value
                       ?? Path.GetFileNameWithoutExtension(xmlFile);
            var auth = First("authentication")?.Value ?? "open";
            var keyType = First("keyType")?.Value;
            bool needsKey = keyType is not null || auth.Contains("PSK", StringComparison.OrdinalIgnoreCase)
                            || auth.Contains("SAE", StringComparison.OrdinalIgnoreCase) || auth.Equals("shared", StringComparison.OrdinalIgnoreCase);
            var key = First("keyMaterial")?.Value;
            bool isProtected = string.Equals(First("protected")?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            return new ProfileInfo(name, needsKey, !string.IsNullOrEmpty(key) && !isProtected);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Ответ netsh «Wi-Fi здесь нет»: служба не запущена или нет беспроводного адаптера.</summary>
    public static bool NoWifiHere(string output) =>
        Regex.IsMatch(output, @"wlansvc|no wireless interface|нет беспроводн\w* интерфейс|беспроводн\w* интерфейс\w* отсутству", RegexOptions.IgnoreCase);

    /// <summary>Похоже на адаптер Wi-Fi (по имени устройства у драйвера).</summary>
    public static bool LooksWireless(string? device) =>
        device is not null && Regex.IsMatch(device, @"wi-?fi|wireless|wlan|802\.11|беспровод", RegexOptions.IgnoreCase);

    /// <summary>
    /// Дождаться адаптера Wi-Fi: после установки драйвера он появляется не сразу, а служба WLAN стартует вслед за ним.
    /// </summary>
    public static async Task<bool> WaitForInterfaceAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var r = await Proc.RunAsync(Proc.System32("netsh.exe"), "wlan show interfaces", ct, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (r.Ok && !NoWifiHere(r.All)) return true;
            if (sw.Elapsed >= timeout) return false;
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }
}

public sealed record BitLockerVolume(string Drive, bool Protected);

public static class BitLocker
{
    /// <summary>
    /// Какие диски (кроме системного) закрыты BitLocker. Свойство оболочки читается без прав администратора:
    /// 1 — включён, 3 — шифруется, 5 — приостановлен, 6 — включён и заблокирован.
    /// </summary>
    public static async Task<List<BitLockerVolume>> ScanAsync(CancellationToken ct = default)
    {
        const string script =
            "$sh = New-Object -ComObject Shell.Application; " +
            "Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { " +
            "  $v = $sh.NameSpace($_.DeviceID + '\\').Self.ExtendedProperty('System.Volume.BitLockerProtection'); " +
            "  $_.DeviceID + '|' + $v }";
        var r = await Proc.PowerShellAsync(script, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var list = new List<BitLockerVolume>();
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\') ?? "C:";
        foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p.Length < 2 || p[0].Equals(system, StringComparison.OrdinalIgnoreCase)) continue;
            bool on = int.TryParse(p[1], out var v) && v is 1 or 3 or 5 or 6;
            if (on) list.Add(new BitLockerVolume(p[0], true));
        }
        return list;
    }

    /// <summary>48-значный ключ восстановления диска (нужны права администратора).</summary>
    public static async Task<string?> RecoveryKeyAsync(string drive, CancellationToken ct = default)
    {
        var r = await Proc.RunAsync(Proc.System32("manage-bde.exe"), $"-protectors -get {drive} -Type RecoveryPassword", ct,
            timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var m = Regex.Match(r.Output, @"\d{6}(-\d{6}){7}");
        return m.Success ? m.Value : null;
    }
}

public static class Activation
{
    /// <summary>Ключ Windows, зашитый производителем в BIOS (у ноутбуков и готовых ПК).</summary>
    public static string? OemKey()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT OA3xOriginalProductKey FROM SoftwareLicensingService");
            foreach (ManagementObject mo in s.Get())
                using (mo)
                    if (mo["OA3xOriginalProductKey"] is string k && k.Length > 10) return k;
        }
        catch { }
        return null;
    }
}
