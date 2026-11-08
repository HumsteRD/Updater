# Проверка распаковки на чистой Windows — в Windows Sandbox.
#
# 1. Собирает с этого ПК тестовую сборку из несекретных мелочей (настройки программ, сейвы, настройки Windows,
#    сетевой драйвер) и кладёт рядом опубликованный Updater.exe — как на флешке.
# 2. Запускает песочницу: сборка подключена только для чтения, результаты пишутся в отдельную папку.
# 3. Внутри песочницы Updater сам проходит распаковку (--auto-restore), снимает экраны и сверяет итог.
#
# Нужно: виртуализация (SVM / VT-x) в BIOS и компонент «Песочница Windows».
# Запуск: powershell -ExecutionPolicy Bypass -File tools\sandbox-test.ps1 [-Programs "7zip.7zip,..."] [-Settings was]
#
# Два режима:
#   по умолчанию   — установка с флешки без интернета: программы из -Installers скачиваются на флешку при сборе
#                    и ставятся в песочнице с выключенным winget;
#   -UseWinget     — настоящий путь новой винды: Updater сам ставит winget (в песочнице его нет) и через него программы.

param(
    [string]$Installers = "Valve.Steam,Microsoft.VCRedist.2015+.x86",
    [string]$Programs = "Valve.Steam,Microsoft.VCRedist.2015+.x86",
    [switch]$UseWinget,
    [string]$Settings = "was",
    [string]$Include = "files.pictures,files.music",
    [int]$TimeoutMinutes = 40
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = 'D:\Projects\Velocity\.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$work = Join-Path $env:TEMP 'updater-sandbox'
$usb = Join-Path $work 'usb'
$pkg = Join-Path $usb 'Updater'
$results = Join-Path $work 'results'

if (-not (Test-Path "$env:WINDIR\System32\WindowsSandbox.exe")) {
    throw "Windows Sandbox не включена: нужен компонент «Песочница Windows» и виртуализация в BIOS."
}
# Песочнице нужен работающий гипервизор. Его часто выключают «игровые» твики: bcdedit /set hypervisorlaunchtype off.
if (-not (Get-CimInstance Win32_ComputerSystem).HypervisorPresent) {
    throw "Гипервизор Windows не запущен. В терминале от администратора: bcdedit /set hypervisorlaunchtype auto — и перезагрузка."
}
if (Get-Process WindowsSandbox, WindowsSandboxClient, WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) {
    throw "Песочница уже запущена — закрой её и повтори."
}

Write-Host "Публикую exe…"
& $dotnet publish (Join-Path $repo 'src\Updater.App\Updater.App.csproj') -c Release -o (Join-Path $repo 'publish') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Публикация не удалась" }
& $dotnet build (Join-Path $repo 'tests\Updater.SelfCheck\Updater.SelfCheck.csproj') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Сборка проверок не удалась" }

Write-Host "Собираю тестовую сборку…"
if (Test-Path $work) { cmd /c rmdir /s /q "$work" | Out-Null }
New-Item -ItemType Directory -Force $usb, $results | Out-Null
Write-Host "  (скачиваю установщики на флешку: $Installers)"
& $dotnet run --project (Join-Path $repo 'tests\Updater.SelfCheck') --no-build -- --demo-package $pkg --include $Include --installers $Installers | Select-Object -Last 3
Copy-Item (Join-Path $repo 'publish\Updater.exe') (Join-Path $pkg 'Updater.exe')

# Запуск внутри песочницы. Кириллицу в пути передаём через скрипт с BOM, а не через командную строку .wsb.
$noWingetArg = if ($UseWinget) { "" } else { ", '--no-winget'" }
$run = @"
`$exe = 'C:\USB\Updater\Updater.exe'
Start-Process -FilePath `$exe -ArgumentList @('--auto-restore', 'C:\Results', '--programs', '$Programs', '--settings', '$Settings'$noWingetArg)
"@
[IO.File]::WriteAllText((Join-Path $results 'run.ps1'), $run, [Text.UTF8Encoding]::new($true))

$wsb = @"
<Configuration>
  <Networking>Default</Networking>
  <MemoryInMB>8192</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$usb</HostFolder><SandboxFolder>C:\USB</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$results</HostFolder><SandboxFolder>C:\Results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -ExecutionPolicy Bypass -File C:\Results\run.ps1</Command></LogonCommand>
</Configuration>
"@
$wsbFile = Join-Path $work 'updater.wsb'
[IO.File]::WriteAllText($wsbFile, $wsb, [Text.UTF8Encoding]::new($false))

Write-Host "Запускаю песочницу…"
# Напрямую, а не по ассоциации: в «облегчённых» сборках Windows у .wsb её может не быть.
$sandbox = Start-Process -FilePath "$env:WINDIR\System32\WindowsSandbox.exe" -ArgumentList "`"$wsbFile`"" -PassThru
Start-Sleep -Seconds 15
if ($sandbox.HasExited -and $sandbox.ExitCode -ne 0 -and -not (Get-Process WindowsSandboxRemoteSession, WindowsSandboxServer -ErrorAction SilentlyContinue)) {
    $code = '0x{0:X8}' -f ([uint32]($sandbox.ExitCode -band 0xFFFFFFFF))
    $hint = if ($code -eq '0xC0351000') { 'гипервизор не запущен (bcdedit /set hypervisorlaunchtype auto и перезагрузка)' } else { 'см. журнал событий Windows' }
    throw "Песочница не запустилась: код $code — $hint."
}

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path (Join-Path $results 'done.flag'))) {
    if ((Get-Date) -gt $deadline) { Write-Warning "Не дождался окончания за $TimeoutMinutes мин."; break }
    Start-Sleep -Seconds 10
}
Write-Host "Результаты: $results"
if (Test-Path (Join-Path $results 'verify.txt')) { Get-Content (Join-Path $results 'verify.txt') -Encoding UTF8 }
