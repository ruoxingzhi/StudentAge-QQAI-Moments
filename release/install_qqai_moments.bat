@echo off
setlocal EnableExtensions
chcp 65001 >nul 2>nul
title StudentAge QQ AI Moments Installer
set "QQAI_BAT=%~f0"
set "QQAI_ARG1=%~1"
set "QQAI_ARG2=%~2"
set "QQAI_ARG3=%~3"
set "QQAI_ARG4=%~4"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $enc=New-Object System.Text.UTF8Encoding -ArgumentList $false; $raw=[System.IO.File]::ReadAllText($env:QQAI_BAT,$enc); $marker='### POWERSHELL_PAYLOAD_BELOW ###'; $idx=$raw.LastIndexOf($marker); if($idx -lt 0){ throw 'installer payload missing' }; $script=$raw.Substring($idx + $marker.Length); $sb=[ScriptBlock]::Create($script); & $sb"
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" (
  echo.
  echo Installer failed. Please check the message above.
  pause >nul
)
exit /b %ERR%
### POWERSHELL_PAYLOAD_BELOW ###
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$GameDirName = 'StudentAge'
$GameExe = 'StudentAge.exe'
$PluginName = 'StudentAge.QQAIMoments.dll'
$ScriptPath = $env:QQAI_BAT
$ScriptDir = Split-Path -Parent $ScriptPath
$LogFile = Join-Path $ScriptDir 'install_qqai_moments.log'
$DryRun = $false
$UserGameDir = $null

function Write-Info([string]$Text) { Write-Host $Text -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host $Text -ForegroundColor Green }
function Write-WarnLine([string]$Text) { Write-Host $Text -ForegroundColor Yellow }
function Write-ErrLine([string]$Text) { Write-Host $Text -ForegroundColor Red }
function Log([string]$Text) {
    Add-Content -LiteralPath $LogFile -Encoding UTF8 -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Text)
}
function Wait-Exit {
    try {
        if (-not [Console]::IsInputRedirected) {
            Write-Host ''
            [void](Read-Host '按回车键退出')
        }
    } catch {}
}
function Fail([string]$Title, [string[]]$Lines) {
    Write-Host ''
    Write-ErrLine "[错误] $Title"
    if ($Lines) {
        Write-Host ''
        foreach ($line in $Lines) { Write-Host $line }
    }
    Log "failed: $Title"
    Write-Host ''
    Write-Host "日志文件：$LogFile"
    Wait-Exit
    exit 1
}

$InputArgs = @($env:QQAI_ARG1, $env:QQAI_ARG2, $env:QQAI_ARG3, $env:QQAI_ARG4) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
foreach ($arg in $InputArgs) {
    if ($arg -ieq '/dry-run' -or $arg -ieq '--dry-run') {
        $DryRun = $true
        continue
    }
    if (-not $UserGameDir) {
        $UserGameDir = $arg
    }
}

Set-Content -LiteralPath $LogFile -Encoding UTF8 -Value ("[{0}] StudentAge QQ AI Moments installer started." -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))

Write-Host '============================================================'
Write-Host ' StudentAge QQ AI Moments 自动安装脚本'
Write-Host '============================================================'
Write-Host ''
Write-Host '这个脚本会自动定位《StudentAge》的 Steam 安装目录，'
Write-Host "并把 $PluginName 复制到 BepInEx\plugins。"
Write-Host ''

function Find-PluginSource {
    $candidates = @(
        (Join-Path $ScriptDir $PluginName),
        (Join-Path $ScriptDir "plugins\$PluginName"),
        (Join-Path $ScriptDir "BepInEx\plugins\$PluginName"),
        (Join-Path $ScriptDir "..\bin\Release\net472\$PluginName")
    )
    foreach ($candidate in $candidates) {
        $full = [System.IO.Path]::GetFullPath($candidate)
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            return $full
        }
    }
    return $null
}

function Try-GameDir([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try {
        $candidate = [System.IO.Path]::GetFullPath(($Path.Trim('"')))
    } catch {
        return $null
    }

    # 允许用户直接粘贴 BepInEx 目录。
    if (Test-Path -LiteralPath (Join-Path $candidate 'core\BepInEx.dll') -PathType Leaf) {
        $candidate = Split-Path -Parent $candidate
    }

    $exe = Join-Path $candidate $GameExe
    $asm = Join-Path $candidate 'StudentAge_Data\Managed\Assembly-CSharp.dll'
    if ((Test-Path -LiteralPath $exe -PathType Leaf) -and (Test-Path -LiteralPath $asm -PathType Leaf)) {
        return $candidate
    }
    return $null
}

function Check-SteamRoot([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { return $null }
    try {
        $rootPath = [System.IO.Path]::GetFullPath(($Root.Trim('"')))
    } catch {
        return $null
    }
    if (-not (Test-Path -LiteralPath (Join-Path $rootPath 'steamapps') -PathType Container)) {
        return $null
    }

    $direct = Try-GameDir (Join-Path $rootPath "steamapps\common\$GameDirName")
    if ($direct) { return $direct }

    $vdf = Join-Path $rootPath 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf -PathType Leaf) {
        try {
            $text = Get-Content -LiteralPath $vdf -Raw -ErrorAction Stop
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $lib = $m.Groups[1].Value -replace '\\\\', '\'
                $found = Try-GameDir (Join-Path $lib "steamapps\common\$GameDirName")
                if ($found) { return $found }
            }
        } catch {}
    }
    return $null
}

function Find-GameDir {
    $manual = Try-GameDir $UserGameDir
    if ($manual) { return $manual }

    $envPath = Try-GameDir $env:STUDENTAGE_DIR
    if ($envPath) { return $envPath }

    $steamRoots = New-Object System.Collections.Generic.List[string]
    $pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $pf = [Environment]::GetEnvironmentVariable('ProgramFiles')
    if ($pf86) { $steamRoots.Add((Join-Path $pf86 'Steam')) }
    if ($pf) { $steamRoots.Add((Join-Path $pf 'Steam')) }

    foreach ($regPath in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        try {
            $item = Get-ItemProperty -Path $regPath -ErrorAction Stop
            foreach ($name in @('SteamPath','InstallPath')) {
                $value = $item.$name
                if ($value) {
                    $steamRoots.Add(($value -replace '/', '\'))
                }
            }
        } catch {}
    }

    foreach ($letter in 'C','D','E','F','G','H','I','J','K','L','M','N','O','P','Q','R','S','T','U','V','W','X','Y','Z') {
        $steamRoots.Add("$letter`:\Steam")
        $steamRoots.Add("$letter`:\SteamLibrary")
        $steamRoots.Add("$letter`:\Program Files (x86)\Steam")
        $steamRoots.Add("$letter`:\Program Files\Steam")
    }

    foreach ($root in ($steamRoots | Select-Object -Unique)) {
        $found = Check-SteamRoot $root
        if ($found) { return $found }
    }
    return $null
}

$PluginSource = Find-PluginSource
if (-not $PluginSource) {
    Fail "没有找到插件文件：$PluginName" @(
        '解决办法：',
        "  1. 确认 $PluginName 和本脚本在同一个文件夹；",
        "  2. 或者把 $PluginName 放到本脚本旁边的 plugins 文件夹；",
        '  3. 或者重新解压完整发布包，不要只复制 bat。'
    )
}
Write-Ok '[1/4] 已找到插件文件：'
Write-Host "      $PluginSource"
Log "plugin source: $PluginSource"

$GameDir = Find-GameDir
if (-not $GameDir) {
    Write-Host ''
    Write-WarnLine '[需要手动选择] 自动定位失败。'
    Write-Host '请复制《StudentAge》游戏根目录，然后粘贴到这里。'
    Write-Host '示例：D:\Program Files (x86)\Steam\steamapps\common\StudentAge'
    Write-Host '也可以直接粘贴 BepInEx 目录。'
    Write-Host ''
    $typed = Read-Host '请输入路径，直接回车取消'
    $GameDir = Try-GameDir $typed
}
if (-not $GameDir) {
    Fail '没能定位 StudentAge 的安装目录。' @(
        '解决办法：',
        '  1. 确认游戏是通过 Steam 安装的，并至少启动过一次 Steam；',
        '  2. 用命令行带路径运行本脚本，例如：',
        '     install_qqai_moments.bat "D:\Program Files (x86)\Steam\steamapps\common\StudentAge"',
        '  3. 或设置环境变量 STUDENTAGE_DIR 指向游戏根目录后重试。'
    )
}
Write-Host ''
Write-Ok '[2/4] 已找到游戏目录：'
Write-Host "      $GameDir"
Log "game dir: $GameDir"

$BepInExDir = Join-Path $GameDir 'BepInEx'
$PluginDir = Join-Path $BepInExDir 'plugins'
if (-not (Test-Path -LiteralPath (Join-Path $BepInExDir 'core\BepInEx.dll') -PathType Leaf)) {
    Fail '找到游戏目录，但没有找到可用的 BepInEx。' @(
        "游戏目录：$GameDir",
        "需要存在：$BepInExDir\core\BepInEx.dll",
        '',
        '解决办法：',
        '  1. 先安装 BepInEx 5.x 到 StudentAge 游戏根目录；',
        '  2. 启动游戏一次，确认 BepInEx 能生成 config 和 plugins 目录；',
        '  3. 再重新运行本脚本。'
    )
}
if (-not (Test-Path -LiteralPath $PluginDir -PathType Container)) {
    New-Item -ItemType Directory -Path $PluginDir -Force | Out-Null
}
Write-Host ''
Write-Ok '[3/4] 已找到 BepInEx：'
Write-Host "      $BepInExDir"
Log "bepinex dir: $BepInExDir"

try {
    if (Get-Process -Name 'StudentAge' -ErrorAction SilentlyContinue) {
        Write-Host ''
        Write-WarnLine '[提示] 检测到游戏正在运行。'
        Write-Host '如果复制失败，请先完全退出游戏和 Steam 后再运行本脚本。'
    }
} catch {}

if ($DryRun) {
    Write-Host ''
    Write-Info '[Dry Run] 仅检测路径，不执行复制。'
    Write-Host "目标目录：$PluginDir"
    Log 'dry-run finished'
    Write-Host ''
    Write-Host "日志文件：$LogFile"
    Wait-Exit
    exit 0
}

Write-Host ''
Write-Info '[4/4] 正在复制插件...'
try {
    Copy-Item -LiteralPath $PluginSource -Destination (Join-Path $PluginDir $PluginName) -Force -ErrorAction Stop
    Log "copied dll to $(Join-Path $PluginDir $PluginName)"

    $pdbSource = [System.IO.Path]::ChangeExtension($PluginSource, '.pdb')
    if (Test-Path -LiteralPath $pdbSource -PathType Leaf) {
        Copy-Item -LiteralPath $pdbSource -Destination (Join-Path $PluginDir 'StudentAge.QQAIMoments.pdb') -Force -ErrorAction Stop
        Log "copied pdb to $(Join-Path $PluginDir 'StudentAge.QQAIMoments.pdb')"
    }
} catch {
    Fail '插件复制失败。' @(
        "源文件：$PluginSource",
        "目标目录：$PluginDir",
        '',
        '解决办法：',
        '  1. 完全退出游戏；',
        '  2. 如果游戏在 Program Files 下，右键本脚本选择“以管理员身份运行”；',
        '  3. 检查杀毒软件/系统权限是否拦截 DLL 复制；',
        '  4. 确认目标目录不是只读。',
        '',
        "系统错误：$($_.Exception.Message)"
    )
}

Write-Host ''
Write-Ok '[4/4] 安装完成。'
Write-Host '插件已复制到：'
Write-Host "  $PluginDir"
Write-Host ''
Write-Host '下一步：'
Write-Host '  1. 从 Steam 正常启动游戏。'
Write-Host '  2. 首次启动后，插件会在 BepInEx\config 下生成配置文件。'
Write-Host '  3. API 地址、Key、模型等在以下文件里配置：'
Write-Host "     $BepInExDir\config\studentage.qqai.moments.cfg"
Write-Host ''
Write-Host "日志文件：$LogFile"
Log 'install finished'
Wait-Exit
exit 0
