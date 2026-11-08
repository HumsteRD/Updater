# Выполняется ВНУТРИ Windows Sandbox (см. sandbox-update-test.ps1).
# Делает «флешки» FAT32 и exFAT (виртуальные диски), кладёт на каждую старый Updater.exe, запускает его как
# обычный человек (без служебных ключей, кроме --offscreen) и смотрит, обновится ли он сам с GitHub.

$R = 'C:\Results'
$exeIn = 'C:\In\Updater.exe'
function Log([string]$t) { Add-Content -Path "$R\verify.txt" -Value $t -Encoding UTF8 }
$ok = 0; $bad = 0
function Check([bool]$cond, [string]$name, [string]$detail = '') {
    if ($cond) { $script:ok++; Log "  ✓ $name $(if ($detail) { "[$detail]" })" } else { $script:bad++; Log "  × $name $(if ($detail) { "[$detail]" })" }
}

Add-Type @"
using System; using System.Diagnostics; using System.Runtime.InteropServices;
public static class Elev {
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int ret);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    public static string Of(int pid) {
        try {
            var p = Process.GetProcessById(pid); IntPtr t;
            if (!OpenProcessToken(p.Handle, 8, out t)) return "?";
            int e, r; bool ok = GetTokenInformation(t, 20, out e, 4, out r); CloseHandle(t);
            return ok ? (e != 0 ? "да" : "нет") : "?";
        } catch (Exception ex) { return "? " + ex.Message; }
    }
}
public static class Vhd {
    [StructLayout(LayoutKind.Sequential)] public struct StorageType { public uint DeviceId; public Guid VendorId; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] public struct CreateParamsV1 {
        [FieldOffset(0)] public int Version;
        [FieldOffset(8)] public Guid UniqueId;
        [FieldOffset(24)] public ulong MaximumSize;
        [FieldOffset(32)] public uint BlockSizeInBytes;
        [FieldOffset(36)] public uint SectorSizeInBytes;
        [FieldOffset(40)] public IntPtr ParentPath;
        [FieldOffset(48)] public IntPtr SourcePath;
    }
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    static extern int CreateVirtualDisk(ref StorageType type, string path, int access, IntPtr sd, int flags, int providerFlags,
        ref CreateParamsV1 p, IntPtr overlapped, out IntPtr handle);
    // Динамический VHD: файл растёт по мере записи.
    public static int Create(string path, ulong size) {
        var t = new StorageType { DeviceId = 2, VendorId = new Guid("EC984AEC-A0F9-47e9-901F-71415A66345B") };
        var p = new CreateParamsV1 { Version = 1, UniqueId = Guid.NewGuid(), MaximumSize = size, SectorSizeInBytes = 512 };
        IntPtr h;
        int rc = CreateVirtualDisk(ref t, path, 0x003f0000, IntPtr.Zero, 0, 0, ref p, IntPtr.Zero, out h);
        if (rc == 0) Elev.CloseHandle(h);
        return rc;
    }
}
// В песочнице нет ни службы виртуальных дисков (diskpart), ни Storage WMI (Initialize-Disk), но сам диск
// подключается. Поэтому таблицу разделов пишем напрямую, том ищем по номеру диска, форматируем format.com.
public static class RawDisk {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, int len, out int written, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int ret, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr FindFirstVolume(System.Text.StringBuilder name, int len);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool FindNextVolume(IntPtr h, System.Text.StringBuilder name, int len);
    [DllImport("kernel32.dll")] static extern bool FindVolumeClose(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetVolumeMountPoint(string mountPoint, string volumeName);

    static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path, uint access) {
        var h = CreateFile(path, access, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), path);
        return h;
    }

    public static void WriteMbr(int disk, byte type, ulong bytes) {
        var mbr = new byte[512];
        BitConverter.GetBytes((uint)Environment.TickCount).CopyTo(mbr, 0x1B8);
        int e = 0x1BE;
        mbr[e + 1] = 0xFE; mbr[e + 2] = 0xFF; mbr[e + 3] = 0xFF;
        mbr[e + 4] = type;
        mbr[e + 5] = 0xFE; mbr[e + 6] = 0xFF; mbr[e + 7] = 0xFF;
        BitConverter.GetBytes((uint)2048).CopyTo(mbr, e + 8);
        BitConverter.GetBytes((uint)(bytes / 512 - 2048)).CopyTo(mbr, e + 12);
        mbr[510] = 0x55; mbr[511] = 0xAA;
        using (var h = Open(@"\\.\PhysicalDrive" + disk, 0xC0000000)) {
            int w;
            if (!WriteFile(h, mbr, 512, out w, IntPtr.Zero) || w != 512) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "MBR");
            int r;
            DeviceIoControl(h, 0x70140 /* IOCTL_DISK_UPDATE_PROPERTIES */, IntPtr.Zero, 0, null, 0, out r, IntPtr.Zero);
        }
    }

    public static string FindVolume(int disk) {
        var sb = new System.Text.StringBuilder(300);
        var fh = FindFirstVolume(sb, sb.Capacity);
        if (fh == new IntPtr(-1)) return null;
        try {
            do {
                var name = sb.ToString();
                var h = CreateFile(name.TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;
                using (h) {
                    var buf = new byte[256]; int r;
                    if (DeviceIoControl(h, 0x560000 /* IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS */, IntPtr.Zero, 0, buf, buf.Length, out r, IntPtr.Zero)
                        && BitConverter.ToInt32(buf, 0) >= 1 && BitConverter.ToInt32(buf, 8) == disk)
                        return name;
                }
            } while (FindNextVolume(fh, sb, sb.Capacity));
        } finally { FindVolumeClose(fh); }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetVolumePathNamesForVolumeName(string volume, char[] names, int len, out int needed);

    /// <summary>Пустая строка — смонтировано; иначе код ошибки Windows.</summary>
    public static string Mount(string mountPoint, string volume) {
        if (!mountPoint.EndsWith("\\")) mountPoint += "\\";
        return SetVolumeMountPoint(mountPoint, volume) ? "" : "ошибка " + Marshal.GetLastWin32Error();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetVolumeInformation(string root, System.Text.StringBuilder label, int labelLen, out uint serial,
        out uint maxComp, out uint flags, System.Text.StringBuilder fs, int fsLen);

    /// <summary>Файловая система тома по корню («F:\» или папке монтирования «C:\Mnt\»).</summary>
    public static string FileSystem(string root) {
        if (!root.EndsWith("\\")) root += "\\";
        var label = new System.Text.StringBuilder(64); var fs = new System.Text.StringBuilder(64); uint a, b, c;
        return GetVolumeInformation(root, label, 64, out a, out b, out c, fs, 64) ? fs.ToString() : "ошибка " + Marshal.GetLastWin32Error();
    }

    /// <summary>Куда том уже смонтирован (буквы и папки), через «;».</summary>
    public static string Paths(string volume) {
        var buf = new char[1024]; int n;
        if (!GetVolumePathNamesForVolumeName(volume, buf, buf.Length, out n)) return "";
        return string.Join(";", new string(buf, 0, Math.Max(0, n)).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
"@

# «Флешка»: VHD → подключить → MBR → раздел → формат. Возвращает корень или $null.
function New-Flash([string]$letter, [string]$fs) {
    $vhd = "C:\flash-$letter.vhd"
    $size = 600MB
    try {
        $rc = [Vhd]::Create($vhd, $size)
        if ($rc -ne 0) { throw "CreateVirtualDisk вернул $rc" }
        $num = (Mount-DiskImage -ImagePath $vhd -StorageType VHD -PassThru -ErrorAction Stop | Get-DiskImage).Number
        if ($null -eq $num) { throw "диск не получил номер" }
        [RawDisk]::WriteMbr([int]$num, $(if ($fs -eq 'exFAT') { 0x07 } else { 0x0C }), [uint64]$size)
        $vol = $null
        for ($i = 0; $i -lt 20 -and -not $vol; $i++) { Start-Sleep -Milliseconds 500; $vol = [RawDisk]::FindVolume([int]$num) }
        if (-not $vol) { throw "том на диске $num не появился" }
        Log "  том $vol, уже смонтирован: «$([RawDisk]::Paths($vol))»"
        # Буква: та, что дала сама Windows; иначе назначаем; иначе — пустая папка на C:.
        $root = ([RawDisk]::Paths($vol) -split ';' | Where-Object { $_ -match '^[A-Z]:\\$' } | Select-Object -First 1)
        if (-not $root) {
            $err = [RawDisk]::Mount("${letter}:", $vol)
            if ($err -eq '') { $root = "${letter}:\" }
            else {
                Log "  (буква $letter не назначилась: $err — монтирую в папку)"
                $mp = "C:\Mnt$fs"; New-Item -ItemType Directory -Force $mp | Out-Null
                $err2 = [RawDisk]::Mount($mp, $vol)
                if ($err2 -ne '') { throw "и в папку не смонтировалось: $err2" }
                $root = "$mp\"
            }
        }
        $target = $root.TrimEnd('\')
        $out = cmd /c "echo.| format $target /FS:$fs /Q /V:FLASH /Y" 2>&1
        Add-Content "$R\format-$fs.txt" ($out -join "`r`n")
        $got = [RawDisk]::FileSystem($root)
        if ($got -ne $fs) { throw "после format файловая система «$got» (см. format-$fs.txt)" }
        Log "  $target — $got"
        return $target
    } catch {
        Log "  (флешку $fs напрямую сделать не вышло: $($_.Exception.Message))"
    }
    # Запасной путь — diskpart (ему нужна служба виртуальных дисков).
    try { Start-Service vds -ErrorAction Stop } catch { Log "  (служба vds: $($_.Exception.Message))" }
    $dp = "create vdisk file=`"C:\flash2-$letter.vhd`" maximum=600 type=expandable`r`nselect vdisk file=`"C:\flash2-$letter.vhd`"`r`nattach vdisk`r`ncreate partition primary`r`nformat fs=$fs quick label=FLASH`r`nassign letter=$letter`r`n"
    Set-Content -Path C:\diskpart.txt -Value $dp -Encoding ASCII
    diskpart /s C:\diskpart.txt *> "$R\diskpart-$fs.txt"
    Start-Sleep -Seconds 2
    try { if ((Get-Volume -DriveLetter $letter -ErrorAction Stop).FileSystem -eq $fs) { return "${letter}:" } } catch { }
    Log "  (diskpart тоже не смог — см. diskpart-$fs.txt)"
    return $null
}

function Test-Update([string]$root, [string]$label) {
    Log ""
    Log "── $label"
    Get-CimInstance Win32_Process -Filter "Name='Updater.exe'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    $dir = "$root\Updater"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item $exeIn "$dir\Updater.exe" -Force
    $exe = "$dir\Updater.exe"
    $before = (Get-Item $exe).VersionInfo
    Log "  на флешке: $exe, версия $($before.FileVersion)"

    # Запуск как у человека: двойной щелчок = без ключей. --offscreen только чтобы окно не мешало.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $exe -ArgumentList '--offscreen' -PassThru
    $seen = @{}
    $newProc = $null
    while ($sw.Elapsed.TotalSeconds -lt 300) {
        Start-Sleep -Seconds 2
        $files = (Get-ChildItem $dir -Force | ForEach-Object { "{0}({1:N1} МБ)" -f $_.Name, ($_.Length / 1MB) }) -join ', '
        if (-not $seen[$files]) { $seen[$files] = 1; Log ("  {0,4:N0} с  {1}" -f $sw.Elapsed.TotalSeconds, $files) }
        $newProc = Get-CimInstance Win32_Process -Filter "Name='Updater.exe'" | Where-Object { $_.CommandLine -match '--updated-from' -and $_.ExecutablePath -eq $exe } | Select-Object -First 1
        if ($newProc -and -not (Get-ChildItem $dir -Filter '*.old' -Force)) { break }
    }
    Log ("  ждал {0:N0} с" -f $sw.Elapsed.TotalSeconds)

    $after = (Get-Item $exe).VersionInfo
    $afterVer = "$($after.FileMajorPart).$($after.FileMinorPart).$($after.FileBuildPart)"
    Check ($script:want -and $afterVer -eq $script:want) "exe обновился до версии с GitHub" "$($before.FileVersion) → $($after.FileVersion)"
    Check ($null -ne $newProc) "новая версия запустилась сама" $(if ($newProc) { $newProc.CommandLine } else { 'нет процесса с --updated-from' })
    Check (-not (Get-ChildItem $dir -Filter '*.old' -Force) -and -not (Get-ChildItem $dir -Filter '*.download*' -Force)) "хвосты (.old, .download) убраны" ((Get-ChildItem $dir -Force).Name -join ', ')
    if ($newProc) {
        $el = [Elev]::Of([int]$newProc.ProcessId)
        Check ($el -eq 'да') "новая версия работает с правами администратора" $el
        Start-Sleep -Seconds 12
        $still = Get-Process -Id $newProc.ProcessId -ErrorAction SilentlyContinue
        $others = @(Get-CimInstance Win32_Process -Filter "Name='Updater.exe'")
        Check ($still -and $others.Count -eq 1) "без повторного обновления по кругу" "процессов Updater: $($others.Count)"
    }
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
    Check ($script:sum -and $script:sum.Trim().StartsWith($hash)) "контрольная сумма exe совпадает с выпуском" $hash
    Get-CimInstance Win32_Process -Filter "Name='Updater.exe'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

Log "ПРОВЕРКА ОБНОВЛЕНИЯ В ПЕСОЧНИЦЕ  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Log "  скрипт запущен с правами администратора: $admin"
try {
    $api = Invoke-RestMethod 'https://api.github.com/repos/HumsteRD/Updater/releases/latest' -Headers @{ 'User-Agent' = 'Updater-sandbox-test' } -TimeoutSec 20
    $want = $api.tag_name.TrimStart('v')
    $sum = (Invoke-WebRequest "https://github.com/HumsteRD/Updater/releases/download/$($api.tag_name)/Updater.exe.sha256" -UseBasicParsing -TimeoutSec 30).Content
    if ($sum -is [byte[]]) { $sum = [Text.Encoding]::ASCII.GetString($sum) }
    Log "  последний выпуск на GitHub: $($api.tag_name)"
} catch { $want = $null; $sum = $null; Log "  × GitHub недоступен: $($_.Exception.Message)" }

$tested = 0
foreach ($spec in @(@{ L = 'F'; FS = 'FAT32' }, @{ L = 'G'; FS = 'exFAT' })) {
    $root = New-Flash $spec.L $spec.FS
    Check ($null -ne $root) "флешка $($spec.FS) создана" "$root"
    if ($root) { Test-Update $root "Флешка $($spec.FS)"; $tested++ }
}
if ($tested -eq 0) { Test-Update 'C:\Flash' 'Папка на NTFS (флешку сделать не удалось)' }

$data = Join-Path $env:LOCALAPPDATA 'Updater'
foreach ($f in 'обновления.txt', 'ошибки.txt') {
    if (Test-Path "$data\$f") { Copy-Item "$data\$f" "$R\$f"; Log ""; Log "--- ${f}:"; Get-Content "$data\$f" -Encoding UTF8 | ForEach-Object { Log "    $_" } }
}
Log ""
Log "ИТОГО: пройдено $ok, провалено $bad"
Set-Content "$R\done.flag" (Get-Date -Format o)
if (-not (Test-Path "$R\keep-open.flag")) { shutdown /s /t 5 }
