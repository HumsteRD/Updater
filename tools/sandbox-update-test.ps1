# Проверка самообновления на чистой Windows — в Windows Sandbox.
#
# Собирает Updater.exe с заниженной версией (по умолчанию 1.0.0) из текущего кода, запускает песочницу и там:
#   - делает «флешку» FAT32 (виртуальный диск) и кладёт exe на неё — как у человека перед переустановкой;
#   - запускает его без служебных ключей (с правами администратора, как в песочнице по умолчанию);
#   - ждёт, пока программа сама скачает последний выпуск с GitHub, проверит его, подменит себя и перезапустится;
#   - сверяет версию, контрольную сумму, права новой копии, хвосты и отсутствие обновления по кругу.
#
# Нужно: виртуализация в BIOS, компонент «Песочница Windows», интернет и выпуск на GitHub новее -Version.
# Запуск: powershell -ExecutionPolicy Bypass -File tools\sandbox-update-test.ps1 [-Version 1.0.0] [-KeepOpen]
#
# -Test rights — другая проверка в той же обвязке (sandbox-rights-inner.ps1): запрос прав для обычной учётной
# записи и администратора под UAC (окно появляется, ответ «Нет»), ответы netsh, когда Wi-Fi нет.

param(
    [ValidateSet('update', 'rights')][string]$Test = 'update',
    [string]$Version = "",
    [switch]$KeepOpen,
    [int]$TimeoutMinutes = 15
)
if (-not $Version -and $Test -eq 'update') { $Version = '1.0.0' }

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = 'D:\Projects\Velocity\.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$work = Join-Path $env:TEMP 'updater-sandbox-update'
$in = Join-Path $work 'in'
$results = Join-Path $work 'results'

if (-not (Test-Path "$env:WINDIR\System32\WindowsSandbox.exe")) {
    throw "Windows Sandbox не включена: нужен компонент «Песочница Windows» и виртуализация в BIOS."
}
if (-not (Get-CimInstance Win32_ComputerSystem).HypervisorPresent) {
    throw "Гипервизор Windows не запущен. В терминале от администратора: bcdedit /set hypervisorlaunchtype auto — и перезагрузка."
}
if (Get-Process WindowsSandbox, WindowsSandboxClient, WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) {
    throw "Песочница уже запущена — закрой её и повтори."
}

if (Test-Path $work) { cmd /c rmdir /s /q "$work" | Out-Null }
New-Item -ItemType Directory -Force $in, $results | Out-Null

Write-Host "Собираю Updater.exe $(if ($Version) { $Version } else { '(версия из Directory.Build.props)' }) из текущего кода…"
$versionArg = if ($Version) { @("-p:Version=$Version") } else { @() }
& $dotnet publish (Join-Path $repo 'src\Updater.App\Updater.App.csproj') -c Release -o (Join-Path $work 'publish') -nologo -v q @versionArg
if ($LASTEXITCODE -ne 0) { throw "Публикация не удалась" }
Copy-Item (Join-Path $work 'publish\Updater.exe') (Join-Path $in 'Updater.exe')
Copy-Item (Join-Path $PSScriptRoot "sandbox-$Test-inner.ps1") (Join-Path $in 'inner.ps1')
if ($KeepOpen) { Set-Content (Join-Path $results 'keep-open.flag') '1' }

$wsb = @"
<Configuration>
  <Networking>Default</Networking>
  <MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$in</HostFolder><SandboxFolder>C:\In</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$results</HostFolder><SandboxFolder>C:\Results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -ExecutionPolicy Bypass -File C:\In\inner.ps1</Command></LogonCommand>
</Configuration>
"@
$wsbFile = Join-Path $work 'update.wsb'
[IO.File]::WriteAllText($wsbFile, $wsb, [Text.UTF8Encoding]::new($false))

Write-Host "Запускаю песочницу…"
$sandbox = Start-Process -FilePath "$env:WINDIR\System32\WindowsSandbox.exe" -ArgumentList "`"$wsbFile`"" -PassThru
Start-Sleep -Seconds 15
if ($sandbox.HasExited -and $sandbox.ExitCode -ne 0 -and -not (Get-Process WindowsSandboxRemoteSession, WindowsSandboxServer -ErrorAction SilentlyContinue)) {
    throw ("Песочница не запустилась: код 0x{0:X8}" -f ([uint32]($sandbox.ExitCode -band 0xFFFFFFFF)))
}

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path (Join-Path $results 'done.flag'))) {
    if ((Get-Date) -gt $deadline) { Write-Warning "Не дождался окончания за $TimeoutMinutes мин."; break }
    Start-Sleep -Seconds 5
}
Write-Host "Результаты: $results"
if (Test-Path (Join-Path $results 'verify.txt')) { Get-Content (Join-Path $results 'verify.txt') -Encoding UTF8 }
