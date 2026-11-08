# Выполняется ВНУТРИ Windows Sandbox (см. sandbox-rights-test.ps1).
# Проверяет запрос прав (UAC) для трёх учётных записей и поведение Wi-Fi без адаптера.

$R = 'C:\Results'
function Log([string]$t) { Add-Content -Path "$R\verify.txt" -Value $t -Encoding UTF8 }
$ok = 0; $bad = 0
function Check([bool]$cond, [string]$name, [string]$detail = '') {
    if ($cond) { $script:ok++; Log "  ✓ $name $(if ($detail) { "[$detail]" })" } else { $script:bad++; Log "  × $name $(if ($detail) { "[$detail]" })" }
}

Log "ПРАВА И WI-FI В ПЕСОЧНИЦЕ  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lua = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue).EnableLUA
Log "  UAC (EnableLUA) = $lua"

# Программа — в общей папке, чтобы её видели другие учётные записи; туда же они пишут ответы.
New-Item -ItemType Directory -Force C:\App, C:\Pub | Out-Null
Copy-Item C:\In\Updater.exe C:\App\Updater.exe
icacls C:\Pub /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
$exe = 'C:\App\Updater.exe'

$pw = 'Upd-Test-' + [Guid]::NewGuid().ToString('N').Substring(0, 12) + '!'
net user obychny $pw /add /y | Out-Null
net user admin2 $pw /add /y | Out-Null
net localgroup Administrators admin2 /add | Out-Null
$sec = ConvertTo-SecureString $pw -AsPlainText -Force
$creds = @{
    obychny = New-Object Management.Automation.PSCredential('.\obychny', $sec)
    admin2  = New-Object Management.Automation.PSCredential('.\admin2', $sec)
}

function Info([string]$who) {
    $out = "C:\Pub\info-$who.txt"
    Remove-Item $out -ErrorAction SilentlyContinue
    try {
        if ($who -eq 'self') { Start-Process $exe -ArgumentList "--info `"$out`"" -Wait -WorkingDirectory C:\App }
        else { Start-Process $exe -ArgumentList "--info `"$out`"" -Credential $creds[$who] -LoadUserProfile -Wait -WorkingDirectory C:\App }
    } catch { Log "  (${who}: запуск не удался: $($_.Exception.Message))" }
    $h = @{}
    if (Test-Path $out) { Get-Content $out -Encoding UTF8 | ForEach-Object { $k, $v = $_ -split '=', 2; if ($k) { $h[$k] = $v } } }
    Log "  $who → admin=$($h['admin']), account=$($h['account']), can-elevate=$($h['can-elevate'])"
    return $h
}

Log ""
Log "── Кто есть кто (--info)"
$self = Info 'self'
Check ($self['admin'] -eq 'True' -and $self['can-elevate'] -eq 'False') "администратор с правами: просить нечего"
$std = Info 'obychny'
Check ($std['admin'] -eq 'False' -and $std['can-elevate'] -eq 'False' -and $std['account'] -eq 'обычная учётная запись') "обычная учётная запись: права не просим"
$adm = Info 'admin2'
if ($lua -eq 1) { Check ($adm['admin'] -eq 'False' -and $adm['can-elevate'] -eq 'True') "администратор под UAC: права просим" }
else { Check ($adm['admin'] -eq 'True') "UAC выключен — администратор сразу с правами" }

# Без функции: функция развернула бы массив из одного процесса в сам процесс.
function Owner($p) { try { (Invoke-CimMethod -InputObject $p -MethodName GetOwner).User } catch { '?' } }

Log ""
Log "── Обычная учётная запись запускает программу"
Get-Process consent -ErrorAction SilentlyContinue | Stop-Process -Force
$p = Start-Process $exe -ArgumentList '--offscreen --no-update' -Credential $creds.obychny -LoadUserProfile -WorkingDirectory C:\App -PassThru
Start-Sleep -Seconds 12
$list = @(Get-CimInstance Win32_Process -Filter "Name='Updater.exe'")
$consent = [bool](Get-Process consent -ErrorAction SilentlyContinue)
Log ("  процессы: " + (($list | ForEach-Object { "$(Owner $_): $($_.CommandLine)" }) -join ' | '))
Check (-not $consent) "окно UAC не появилось"
$own = if ($list.Count -gt 0) { Owner $list[0] } else { '' }
Check ($list.Count -eq 1 -and $own -eq 'obychny' -and $list[0].CommandLine -notmatch '--elevated') "программа работает сама, без перезапуска с правами" "процессов $($list.Count), владелец $own"
$list | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

if ($lua -eq 1) {
    Log ""
    Log "── Администратор под UAC: окно появляется, ответ «Нет»"
    $p = Start-Process $exe -ArgumentList '--offscreen --no-update' -Credential $creds.admin2 -LoadUserProfile -WorkingDirectory C:\App -PassThru
    $consent = $null
    for ($i = 0; $i -lt 20 -and -not $consent; $i++) { Start-Sleep -Milliseconds 500; $consent = Get-Process consent -ErrorAction SilentlyContinue }
    Check ([bool]$consent) "окно UAC появилось"
    # Закрыть окно UAC = ответить «Нет» (то же, что нажать «Нет» или крестик).
    if ($consent) { $consent | Stop-Process -Force }
    Start-Sleep -Seconds 8
    $list = @(Get-CimInstance Win32_Process -Filter "Name='Updater.exe'")
    Log ("  процессы: " + (($list | ForEach-Object { "$(Owner $_): $($_.CommandLine)" }) -join ' | '))
    $own = if ($list.Count -gt 0) { Owner $list[0] } else { '' }
    Check ($list.Count -eq 1 -and $own -eq 'admin2' -and $list[0].CommandLine -notmatch '--elevated') "после «Нет» программа работает дальше без прав" "процессов $($list.Count), владелец $own"
    $list | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

Log ""
Log "── Wi-Fi без адаптера"
$svc = Get-Service wlansvc -ErrorAction SilentlyContinue
Log "  служба wlansvc: $(if ($svc) { "$($svc.Status) / $($svc.StartType)" } else { 'нет' })"
$noWifi = 'wlansvc|no wireless interface|нет беспроводн\w* интерфейс|беспроводн\w* интерфейс\w* отсутству'
$show = (netsh wlan show interfaces 2>&1) -join ' '
Log "  netsh wlan show interfaces: $show"
Check ($show -match $noWifi) "программа поймёт, что Wi-Fi здесь нет (show interfaces)"
$xml = @"
<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1"><name>Дом</name><SSIDConfig><SSID><name>Дом</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>test12345</keyMaterial></sharedKey></security></MSM></WLANProfile>
"@
[IO.File]::WriteAllText('C:\Pub\wifi-test.xml', $xml, [Text.UTF8Encoding]::new($false))
$add = (netsh wlan add profile filename="C:\Pub\wifi-test.xml" user=all 2>&1) -join ' '
Log "  netsh wlan add profile: $add (код $LASTEXITCODE)"
Check ($add -match $noWifi) "программа поймёт, что Wi-Fi здесь нет (add profile)"

net user obychny /delete | Out-Null
net user admin2 /delete | Out-Null
Log ""
Log "ИТОГО: пройдено $ok, провалено $bad"
Set-Content "$R\done.flag" (Get-Date -Format o)
if (-not (Test-Path "$R\keep-open.flag")) { shutdown /s /t 5 }
