@echo off
setlocal EnableExtensions
chcp 65001 >nul 2>nul
title StudentAge QQ AI Moments Config
set "QQAI_BAT=%~f0"
set "QQAI_ARG1=%~1"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $enc=New-Object System.Text.UTF8Encoding -ArgumentList $false; $raw=[System.IO.File]::ReadAllText($env:QQAI_BAT,$enc); $marker='### POWERSHELL_PAYLOAD_BELOW ###'; $idx=$raw.LastIndexOf($marker); if($idx -lt 0){ throw 'config opener payload missing' }; $script=$raw.Substring($idx + $marker.Length); $sb=[ScriptBlock]::Create($script); & $sb"
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" (
  echo.
  echo Open config failed. Please check the message above.
  pause >nul
)
exit /b %ERR%
### POWERSHELL_PAYLOAD_BELOW ###
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$GameDirName = 'StudentAge'
$GameExe = 'StudentAge.exe'
$ConfigName = 'studentage.qqai.moments.cfg'
$ScriptPath = $env:QQAI_BAT
$ScriptDir = Split-Path -Parent $ScriptPath
$DryRun = ($env:QQAI_ARG1 -ieq '/dry-run' -or $env:QQAI_ARG1 -ieq '--dry-run')

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
    Write-Host "[错误] $Title" -ForegroundColor Red
    if ($Lines) {
        Write-Host ''
        foreach ($line in $Lines) { Write-Host $line }
    }
    Wait-Exit
    exit 1
}

Write-Host '============================================================'
Write-Host ' StudentAge QQ AI Moments 配置文件打开工具'
Write-Host '============================================================'
Write-Host ''

function Try-GameDir([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try {
        $candidate = [System.IO.Path]::GetFullPath(($Path.Trim('"')))
    } catch {
        return $null
    }

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

$GameDir = Find-GameDir
if (-not $GameDir) {
    Fail '没有找到 StudentAge 的 Steam 安装目录。' @(
        '解决办法：',
        '  1. 确认游戏是通过 Steam 安装的；',
        '  2. 先打开一次 Steam；',
        '  3. 如果是非标准安装，可设置环境变量 STUDENTAGE_DIR 指向游戏根目录。'
    )
}

$BepInExDir = Join-Path $GameDir 'BepInEx'
$ConfigPath = Join-Path $BepInExDir "config\$ConfigName"
$ConfigDir = Split-Path -Parent $ConfigPath

Write-Host '已定位到游戏目录：'
Write-Host "  $GameDir"
Write-Host ''
Write-Host '配置文件路径：'
Write-Host "  $ConfigPath"
Write-Host ''

if (-not (Test-Path -LiteralPath (Join-Path $BepInExDir 'core\BepInEx.dll') -PathType Leaf)) {
    Fail '找到游戏目录，但没有找到 BepInEx。' @(
        "需要存在：$BepInExDir\core\BepInEx.dll",
        '',
        '解决办法：先安装 BepInEx，再从 Steam 启动游戏一次。'
    )
}

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    if (Test-Path -LiteralPath $ConfigDir -PathType Container) {
        try { Start-Process explorer.exe -ArgumentList "`"$ConfigDir`"" } catch {}
    }
    Fail '没有找到 QQ AI 配置文件。' @(
        '常见原因：',
        '  1. 插件还没有安装到 BepInEx\plugins；',
        '  2. 安装后还没有启动过一次游戏；',
        '  3. BepInEx 没有正常加载插件。',
        '',
        '解决办法：',
        '  1. 先运行 install_qqai_moments.bat 安装插件；',
        '  2. 从 Steam 正常启动游戏一次；',
        '  3. 回到这里再次运行本脚本。'
    )
}

if ($DryRun) {
    Write-Host '[Dry Run] 已找到配置文件，不打开记事本。' -ForegroundColor Cyan
    Wait-Exit
    exit 0
}

Write-Host '正在用记事本打开配置文件...' -ForegroundColor Green
Start-Process notepad.exe -ArgumentList "`"$ConfigPath`""
exit 0
