$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
} catch {}

$AppId = '1991040'
$GameDirName = 'StudentAge'
$GameExe = 'StudentAge.exe'
$PluginName = 'StudentAge.QQAIMoments.dll'
$RequiredVersion = [Version]'1.1.13'
$ScriptPath = if ($env:QQAI_PS1) { $env:QQAI_PS1 } else { $PSCommandPath }
$ScriptDir = Split-Path -Parent $ScriptPath
$Stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$LogFile = Join-Path $ScriptDir ("qqai_diagnose_fix_{0}.log" -f $Stamp)
$CheckOnly = $false
$ResetConsent = $false
$FixDuplicates = $false
$UserGameDir = $null

function Out-Line([string]$Text, [ConsoleColor]$Color = [ConsoleColor]::Gray) {
    Write-Host $Text -ForegroundColor $Color
    try { Add-Content -LiteralPath $LogFile -Encoding UTF8 -Value $Text } catch {}
}
function Info([string]$Text) { Out-Line $Text Cyan }
function Ok([string]$Text) { Out-Line "[OK] $Text" Green }
function Warn([string]$Text) { Out-Line "[提示] $Text" Yellow }
function Bad([string]$Text) { Out-Line "[问题] $Text" Red }
function Section([string]$Text) { Out-Line ''; Out-Line "==== $Text ====" White }
function Wait-Exit {
    try {
        if (-not [Console]::IsInputRedirected) {
            Out-Line ''
            [void](Read-Host '按回车键退出')
        }
    } catch {}
}
function Fail([string]$Title, [string[]]$Lines) {
    Bad $Title
    foreach ($line in ($Lines | Where-Object { $_ })) { Out-Line "  $line" }
    Out-Line ''
    Out-Line "日志文件：$LogFile"
    Wait-Exit
    exit 1
}
function Safe-FullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try { return [System.IO.Path]::GetFullPath($Path.Trim('"')) } catch { return $null }
}
function Get-VersionFromFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try {
        $raw = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
        if ($raw -match '(\d+\.\d+\.\d+(?:\.\d+)?)') { return [Version]$Matches[1] }
    } catch {}
    return $null
}
function Get-FileSha256([string]$Path) {
    try { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash } catch { return $null }
}
function Is-GameDir([string]$Path) {
    $p = Safe-FullPath $Path
    if (-not $p) { return $false }
    return (Test-Path -LiteralPath (Join-Path $p $GameExe) -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $p 'StudentAge_Data\Managed\Assembly-CSharp.dll') -PathType Leaf)
}
function Try-GameDir([string]$Path) {
    $p = Safe-FullPath $Path
    if (-not $p) { return $null }
    if (Test-Path -LiteralPath (Join-Path $p 'core\BepInEx.dll') -PathType Leaf) { $p = Split-Path -Parent $p }
    if (Is-GameDir $p) { return $p }
    return $null
}
function Add-Candidate([System.Collections.Generic.List[string]]$List, [string]$Path) {
    $p = Safe-FullPath $Path
    if ($p -and -not $List.Contains($p)) { [void]$List.Add($p) }
}
function Get-SteamRoots {
    $roots = [System.Collections.Generic.List[string]]::new()
    foreach ($p in @('C:\Program Files (x86)\Steam','C:\Program Files\Steam','D:\Program Files (x86)\Steam','D:\Steam','D:\ruanjian\steam')) {
        if (Test-Path -LiteralPath (Join-Path $p 'steamapps') -PathType Container) { Add-Candidate $roots $p }
    }
    foreach ($regPath in @('HKCU:\Software\Valve\Steam','HKLM:\SOFTWARE\WOW6432Node\Valve\Steam','HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $prop = Get-ItemProperty -Path $regPath -ErrorAction Stop
            foreach ($name in @('SteamPath','InstallPath')) {
                if ($prop.$name) { Add-Candidate $roots ($prop.$name -replace '/', '\') }
            }
        } catch {}
    }
    $more = @()
    foreach ($root in @($roots)) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf -PathType Leaf)) { continue }
        try {
            $text = Get-Content -LiteralPath $vdf -Raw -Encoding UTF8
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"\r\n]+)"')) {
                $more += ($m.Groups[1].Value -replace '\\\\','\')
            }
        } catch {}
    }
    foreach ($p in $more) {
        if (Test-Path -LiteralPath (Join-Path $p 'steamapps') -PathType Container) { Add-Candidate $roots $p }
    }
    return @($roots)
}
function Find-GameDir {
    foreach ($p in @($UserGameDir, $env:STUDENTAGE_DIR)) {
        $g = Try-GameDir $p
        if ($g) { return $g }
    }
    $cur = Safe-FullPath $ScriptDir
    for ($i = 0; $i -lt 6 -and $cur; $i++) {
        $g = Try-GameDir $cur
        if ($g) { return $g }
        $parent = Split-Path -Parent $cur
        if ($parent -eq $cur) { break }
        $cur = $parent
    }
    foreach ($root in Get-SteamRoots) {
        $direct = Try-GameDir (Join-Path $root "steamapps\common\$GameDirName")
        if ($direct) { return $direct }
        $manifest = Join-Path $root "steamapps\appmanifest_$AppId.acf"
        if (Test-Path -LiteralPath $manifest -PathType Leaf) {
            try {
                $text = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8
                if ($text -match '"installdir"\s+"([^"\r\n]+)"') {
                    $g = Try-GameDir (Join-Path $root ("steamapps\common\" + $Matches[1]))
                    if ($g) { return $g }
                }
            } catch {}
        }
    }
    return $null
}
function Find-PluginSource {
    $candidates = @(
        (Join-Path $ScriptDir $PluginName),
        (Join-Path $ScriptDir "plugins\$PluginName"),
        (Join-Path $ScriptDir "BepInEx\plugins\$PluginName"),
        (Join-Path $ScriptDir "..\bin\Release\net472\$PluginName"),
        (Join-Path $ScriptDir "..\qq\bin\Release\net472\$PluginName")
    )
    foreach ($p in $candidates) {
        $full = Safe-FullPath $p
        if ($full -and (Test-Path -LiteralPath $full -PathType Leaf)) { return $full }
    }
    return $null
}
function Backup-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $bak = "$Path.bak_$Stamp"
    Copy-Item -LiteralPath $Path -Destination $bak -Force
    return $bak
}
function Set-CfgValue([string[]]$Lines, [string]$Key, [string]$Value) {
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match ('^\s*' + [regex]::Escape($Key) + '\s*=')) {
            if ($Lines[$i] -eq "$Key = $Value") { return @{ Lines = $Lines; Changed = $false } }
            $Lines[$i] = "$Key = $Value"
            return @{ Lines = $Lines; Changed = $true }
        }
    }
    $Lines += "$Key = $Value"
    return @{ Lines = $Lines; Changed = $true }
}
function Read-CfgMap([string]$Path) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $map }
    try {
        foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
            if ($line -match '^\s*#') { continue }
            if ($line -match '^\s*([^=\s]+)\s*=\s*(.*)$') { $map[$Matches[1]] = $Matches[2].Trim() }
        }
    } catch {}
    return $map
}
function Repair-Config([string]$ConfigPath) {
    if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
        Warn "配置文件暂不存在：$ConfigPath。首次进游戏后插件会自动生成。"
        return
    }
    $map = Read-CfgMap $ConfigPath
    $apiState = if ($map.ContainsKey('ApiKey') -and -not [string]::IsNullOrWhiteSpace($map['ApiKey'])) { '已填写' } else { '未填写' }
    $baseState = if ($map.ContainsKey('BaseUrl') -and -not [string]::IsNullOrWhiteSpace($map['BaseUrl'])) { '已填写' } else { '未填写' }
    Info "配置摘要：Enabled=$($map['Enabled']) UseAI=$($map['UseAI']) BaseUrl=$baseState ApiKey=$apiState Model=$($map['Model'])"
    Info "隐私摘要：ShareUsageData=$($map['ShareUsageData']) Prompted=$($map['ShareUsageDataPrompted']) PromptVersion=$($map['ShareUsageDataPromptVersion']) RawText=$($map['ShareUsageRawText']) RawPrompted=$($map['ShareUsageRawTextPrompted'])"
    if ($CheckOnly) { return }

    $lines = @(Get-Content -LiteralPath $ConfigPath -Encoding UTF8)
    $changedAny = $false
    foreach ($pair in @(
        @('Enabled','true'),
        @('DebugLog','true'),
        @('HotReloadConfig','true'),
        @('HotReloadPersonas','true')
    )) {
        $r = Set-CfgValue $lines $pair[0] $pair[1]
        $lines = [string[]]$r.Lines
        if ($r.Changed) { $changedAny = $true }
    }
    if ($ResetConsent) {
        foreach ($pair in @(@('ShareUsageDataPrompted','false'), @('ShareUsageDataPromptVersion','0'))) {
            $r = Set-CfgValue $lines $pair[0] $pair[1]
            $lines = [string[]]$r.Lines
            if ($r.Changed) { $changedAny = $true }
        }
    }
    if ($changedAny) {
        $bak = Backup-File $ConfigPath
        Set-Content -LiteralPath $ConfigPath -Encoding UTF8 -Value $lines
        Ok "已修复配置：开启插件、调试日志、热加载。原文件备份：$bak"
        if ($ResetConsent) { Ok '已重置数据共享问卷状态：下次启动会重新询问。' }
    } else {
        Ok '配置关键项无需修复。'
    }
}
function Inspect-Logs([string]$GameDir) {
    $paths = @(
        (Join-Path $GameDir 'BepInEx\LogOutput.log'),
        (Join-Path $env:USERPROFILE 'AppData\LocalLow\PakyiGame\StudentAge\Player.log')
    )
    foreach ($p in $paths) {
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
            Warn "日志不存在：$p"
            continue
        }
        Info "日志：$p"
        try { $tail = Get-Content -LiteralPath $p -Tail 1200 -Encoding UTF8 -ErrorAction Stop }
        catch {
            try { $tail = Get-Content -LiteralPath $p -Tail 1200 -ErrorAction Stop }
            catch { Warn "读取失败：$($_.Exception.Message)"; continue }
        }
        $joined = $tail -join "`n"
        $load = [regex]::Matches($joined, 'Loading \[StudentAge QQ AI Moments ([^\]]+)\]') | Select-Object -Last 1
        if ($load) {
            $runtimeVersionText = $load.Groups[1].Value.Trim()
            $runtimeVersion = $null
            try { $runtimeVersion = [Version]$runtimeVersionText } catch {}
            if ($runtimeVersion -and $runtimeVersion -lt $RequiredVersion) {
                Warn "日志显示 QQAI 运行版本仍是 $runtimeVersionText，低于建议版本 $RequiredVersion。请完全退出游戏和 Steam 后重新启动；如果仍旧如此，请检查是否有重复旧 DLL。"
            } elseif (-not $runtimeVersion) {
                Warn "日志显示 QQAI 加载版本：$runtimeVersionText，但无法解析版本号。请确认期望版本为 $RequiredVersion。"
            } else {
                Ok "日志显示 QQAI 加载版本：$runtimeVersionText"
            }
        }
        else { Bad '日志尾部没有看到 QQAI 加载记录。可能插件没装到正确目录，或还没重新启动游戏。' }

        $checks = @(
            @{ Label = 'QQAI loaded'; Regex = 'StudentAge QQ AI Moments .* loaded' },
            @{ Label = '自由回复入口就绪'; Regex = [regex]::Escape('QQ空间自由回复入口配置已就绪') },
            @{ Label = '官方原生数据共享弹窗'; Regex = [regex]::Escape('使用官方原生确认框显示数据共享选择') },
            @{ Label = '官方原生原文共享弹窗'; Regex = [regex]::Escape('使用官方原生确认框显示原文共享选择') },
            @{ Label = '内置兜底弹窗'; Regex = [regex]::Escape('已切换到内置大字兜底弹窗') },
            @{ Label = '数据共享选择'; Regex = [regex]::Escape('数据共享选择') },
            @{ Label = 'Saves\user'; Regex = 'Saves\\user|Saves/user' },
            @{ Label = '错误/异常'; Regex = 'Exception|\[Error\]|\[Fatal\]' }
        )
        foreach ($check in $checks) {
            $hits = $tail | Where-Object { $_ -match ([string]$check.Regex) } | Select-Object -Last 8
            if ($hits) {
                Out-Line ("-- 关键片段：" + $check.Label) DarkCyan
                foreach ($h in $hits) { Out-Line ("   " + $h) Gray }
            }
        }
    }
}
function Inspect-Saves {
    $root = Join-Path $env:USERPROFILE 'AppData\LocalLow\PakyiGame\StudentAge\Saves'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        Warn "未找到存档目录：$root"
        return
    }
    Info "存档目录：$root"
    $dirs = @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)
    foreach ($d in $dirs) {
        $global = Test-Path -LiteralPath (Join-Path $d.FullName '_global') -PathType Leaf
        $link = ''
        try { if ($d.LinkType) { $link = " LinkType=$($d.LinkType) Target=$($d.Target)" } } catch {}
        Info ("  - {0}  _global={1}{2}" -f $d.Name, $global, $link)
    }
    $userDir = $dirs | Where-Object { $_.Name -ieq 'user' } | Select-Object -First 1
    $realDirs = @($dirs | Where-Object { $_.Name -match '^\d{10,}$' })
    if ($userDir -and $realDirs.Count -gt 0) {
        Warn '同时存在 user 和 SteamID 存档目录。旧版可能误读 user；新版会保护已有存档，不会删除或移动。'
    } elseif ($userDir) {
        Warn '只看到 user 存档目录。插件会保留它，避免玩家误以为存档丢失。'
    } elseif ($realDirs.Count -gt 0) {
        Ok '存在 SteamID 存档目录。'
    }
}

$InputArgs = @($args) + @($env:QQAI_ARG1,$env:QQAI_ARG2,$env:QQAI_ARG3,$env:QQAI_ARG4,$env:QQAI_ARG5,$env:QQAI_ARG6,$env:QQAI_ARG7,$env:QQAI_ARG8) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
for ($i = 0; $i -lt $InputArgs.Count; $i++) {
    $arg = $InputArgs[$i]
    switch -Regex ($arg) {
        '^(--check-only|/check-only|--dry-run|/dry-run)$' { $CheckOnly = $true; continue }
        '^(--reset-consent|/reset-consent)$' { $ResetConsent = $true; continue }
        '^(--fix-duplicates|/fix-duplicates)$' { $FixDuplicates = $true; continue }
        default { if (-not $UserGameDir) { $UserGameDir = $arg } }
    }
}

Set-Content -LiteralPath $LogFile -Encoding UTF8 -Value ("StudentAge QQ AI Moments 检测修复日志 {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Out-Line '============================================================' White
Out-Line ' StudentAge QQ AI Moments 检测修复工具' White
Out-Line '============================================================' White
$mode = if ($CheckOnly) { '只检测' } else { '检测并安全修复' }
if ($ResetConsent) { $mode += '；重置问卷' }
Info "模式：$mode"
Info "日志文件：$LogFile"

Section '1. 定位游戏与插件文件'
$GameDir = Find-GameDir
if (-not $GameDir) {
    Fail '没有自动找到 StudentAge 游戏目录。' @(
        '解决办法：把本脚本放到 Mod 包目录，或把游戏目录拖到本脚本上运行。',
        '也可以命令行运行：diagnose_fix_qqai_moments.bat "D:\Steam\steamapps\common\StudentAge"'
    )
}
Ok "游戏目录：$GameDir"
$BepInExDir = Join-Path $GameDir 'BepInEx'
$PluginDir = Join-Path $BepInExDir 'plugins'
if (-not (Test-Path -LiteralPath (Join-Path $BepInExDir 'core\BepInEx.dll') -PathType Leaf)) {
    Fail '该游戏目录下没有检测到 BepInEx。' @('请先正确安装 BepInEx，再运行本工具。', "期望位置：$BepInExDir")
}
if (-not (Test-Path -LiteralPath $PluginDir -PathType Container)) {
    if ($CheckOnly) { Bad "插件目录不存在：$PluginDir" }
    else { New-Item -ItemType Directory -Path $PluginDir -Force | Out-Null; Ok "已创建插件目录：$PluginDir" }
} else { Ok "BepInEx 插件目录：$PluginDir" }

$PluginSource = Find-PluginSource
if ($PluginSource) { Ok "本工具携带的插件：$PluginSource" } else { Warn '本工具旁边没有找到 StudentAge.QQAIMoments.dll，只能检测，不能自动复制修复。' }
$TargetPlugin = Join-Path $PluginDir $PluginName
$targetVersion = Get-VersionFromFile $TargetPlugin
if ($targetVersion) { Info "当前已安装 QQAI 版本：$targetVersion" } else { Warn '当前没有安装 QQAI 插件，或无法读取版本。' }
$sourceVersion = if ($PluginSource) { Get-VersionFromFile $PluginSource } else { $null }
if ($sourceVersion) { Info "待安装 QQAI 版本：$sourceVersion" }

if ($PluginSource -and -not $CheckOnly) {
    $needCopy = $false
    if (-not (Test-Path -LiteralPath $TargetPlugin -PathType Leaf)) { $needCopy = $true }
    elseif ((Get-FileSha256 $PluginSource) -ne (Get-FileSha256 $TargetPlugin)) { $needCopy = $true }
    if ($needCopy) {
        if (Test-Path -LiteralPath $TargetPlugin -PathType Leaf) { $bak = Backup-File $TargetPlugin; Warn "已备份旧 DLL：$bak" }
        Copy-Item -LiteralPath $PluginSource -Destination $TargetPlugin -Force
        $pdb = [System.IO.Path]::ChangeExtension($PluginSource, '.pdb')
        if (Test-Path -LiteralPath $pdb -PathType Leaf) {
            Copy-Item -LiteralPath $pdb -Destination ([System.IO.Path]::ChangeExtension($TargetPlugin, '.pdb')) -Force
        }
        Ok "已复制 QQAI 插件到：$TargetPlugin"
    } else {
        Ok '插件 DLL 已是当前版本，无需复制。'
    }
}

$installedVersion = Get-VersionFromFile $TargetPlugin
if ($installedVersion -and $installedVersion -lt $RequiredVersion) { Bad "安装版本低于建议版本 $RequiredVersion，请使用新版 DLL。" }
elseif ($installedVersion) { Ok "安装版本检查通过：$installedVersion" }

$duplicates = @(Get-ChildItem -LiteralPath $PluginDir -Recurse -Filter $PluginName -File -ErrorAction SilentlyContinue)
if ($duplicates.Count -gt 1) {
    Bad "检测到多个 QQAI DLL，可能导致加载到旧版本：$($duplicates.Count) 个。"
    foreach ($d in $duplicates) { Out-Line "  $($d.FullName)  version=$(Get-VersionFromFile $d.FullName)" }
    if ($FixDuplicates -and -not $CheckOnly) {
        foreach ($d in $duplicates) {
            if ($d.FullName -ieq $TargetPlugin) { continue }
            $bak = "$($d.FullName).disabled_$Stamp"
            Move-Item -LiteralPath $d.FullName -Destination $bak -Force
            Warn "已停用重复 DLL：$bak"
        }
    } else {
        Warn '如需自动停用重复 DLL，请加参数 /fix-duplicates。'
    }
} else {
    Ok '没有发现重复 QQAI DLL。'
}

Section '2. 检查并修复配置'
$ConfigDir = Join-Path $BepInExDir 'config'
$ConfigPath = Join-Path $ConfigDir 'studentage.qqai.moments.cfg'
Repair-Config $ConfigPath
$PersonaPath = Join-Path $ConfigDir 'QQAIMoments\personas.json'
if (Test-Path -LiteralPath $PersonaPath -PathType Leaf) { Ok "人设文件存在：$PersonaPath" }
else { Warn "人设文件暂不存在：$PersonaPath。1.1.13 起只要启动游戏并加载插件，就会自动释放默认人设。" }

Section '3. 检查存档路径'
Inspect-Saves

Section '4. 检查最近日志'
Inspect-Logs $GameDir

Section '5. 下一步验证'
Out-Line '请完全退出游戏后，从 Steam 正常启动。进入主菜单后，正常情况下会看到官方原生样式的“QQ空间AI数据共享”选择框。'
Out-Line '如果还没出现，请把本工具生成的日志，以及 BepInEx\LogOutput.log 发给作者。'
Out-Line '关键期望日志：'
Out-Line '  Loading [StudentAge QQ AI Moments 1.1.13]'
Out-Line '  BepInEx\config\QQAIMoments\personas.json 会在插件启动时自动出现。'
Out-Line '  [QQAI] [INFO][Consent] 使用官方原生确认框显示数据共享选择。'
Out-Line '  [QQAI] [INFO][Runtime] QQAI 运行时初始化完成：store=...'
Out-Line '  [QQAI] QQ空间自由回复入口配置已就绪。'
Out-Line ''
Ok '检测修复流程结束。'
Out-Line "日志文件：$LogFile"
Wait-Exit
exit 0
