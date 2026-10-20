<#
.SYNOPSIS
  原版 Minecraft 下载器（独立脚本，兼容 Windows PowerShell 5.1 / PowerShell 7）
.DESCRIPTION
  使用 Mojang 官方公开 API（可选 BMCLAPI 镜像）把指定原版版本安装到 .minecraft 目录，
  目录结构与官方启动器 / HMCL / PCL 兼容。所有文件均做 SHA1 校验。
.EXAMPLE
  .\mc-download.ps1 -List
  .\mc-download.ps1 -Version 1.20.1 -Source Auto -Threads 16
#>
[CmdletBinding()]
param(
    [string]$Version = 'latest',
    [string]$GameDir = '',
    [ValidateSet('Official', 'BMCLAPI', 'Auto')]
    [string]$Source = 'Auto',
    [ValidateRange(1, 64)]
    [int]$Threads = 8,
    [switch]$List,
    [switch]$IncludeSnapshots
)

$ErrorActionPreference = 'Stop'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
[Net.ServicePointManager]::DefaultConnectionLimit = 256

$ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
if ([string]::IsNullOrWhiteSpace($GameDir)) { $GameDir = Join-Path $ScriptDir '.minecraft' }
$GameDir = [IO.Path]::GetFullPath($GameDir)

# ---------------- 下载源 ----------------
$MirrorFirst = $false
switch ($Source) {
    'BMCLAPI'  { $MirrorFirst = $true }
    'Official' { $MirrorFirst = $false }
    'Auto' {
        $culture = (Get-Culture).Name
        $offset = [TimeZoneInfo]::Local.BaseUtcOffset.TotalHours
        $MirrorFirst = ($culture -like 'zh-CN*') -or ($offset -eq 8)
    }
}

function Get-MirrorUrl([string]$url) {
    $m = 'https://bmclapi2.bangbang93.com'
    $u = $url -replace '^http://', 'https://'
    $u = $u -replace '^https://(piston-meta|piston-data|launchermeta|launcher)\.mojang\.com', $m
    $u = $u -replace '^https://libraries\.minecraft\.net', "$m/maven"
    $u = $u -replace '^https://resources\.download\.minecraft\.net', "$m/assets"
    return $u
}

function Get-SourceUrls([string]$url) {
    $official = $url -replace '^http://', 'https://'
    $mirror = Get-MirrorUrl $official
    if ($Source -eq 'Official' -or $mirror -eq $official) { return , @($official) }
    if ($MirrorFirst) { return , @($mirror, $official) }
    return , @($official, $mirror)
}

# ---------------- 下载/校验核心（主线程与工作线程共用） ----------------
$CoreDef = @'
function Get-FileSha1([string]$Path) {
    $sha = [System.Security.Cryptography.SHA1]::Create()
    $fs = [System.IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '').ToLowerInvariant() }
    finally { $fs.Dispose(); $sha.Dispose() }
}

function Test-FileValid($Task) {
    if (-not [System.IO.File]::Exists($Task.Path)) { return $false }
    if ($Task.Size -gt 0 -and (New-Object System.IO.FileInfo $Task.Path).Length -ne $Task.Size) { return $false }
    if ($Task.Sha1) { return (Get-FileSha1 $Task.Path) -eq $Task.Sha1.ToLowerInvariant() }
    return $true
}

function Invoke-FileTask($Task) {
    $r = [pscustomobject]@{ Path = $Task.Path; Status = 'Failed'; Bytes = 0; Url = ''; Error = '' }
    try {
        if (Test-FileValid $Task) { $r.Status = 'Skipped'; return $r }
    } catch { }
    $dir = [System.IO.Path]::GetDirectoryName($Task.Path)
    if (-not [System.IO.Directory]::Exists($dir)) { [void][System.IO.Directory]::CreateDirectory($dir) }
    $tmp = $Task.Path + '.part'
    $errors = @()
    for ($round = 1; $round -le 3; $round++) {
        foreach ($url in $Task.Urls) {
            try {
                $wc = New-Object System.Net.WebClient
                $wc.Headers.Add('User-Agent', 'mc-download.ps1/1.0')
                try { $wc.DownloadFile($url, $tmp) } finally { $wc.Dispose() }
                $len = (New-Object System.IO.FileInfo $tmp).Length
                if ($Task.Size -gt 0 -and $len -ne $Task.Size) { throw "大小不符：期望 $($Task.Size)，实际 $len" }
                if ($Task.Sha1) {
                    $h = Get-FileSha1 $tmp
                    if ($h -ne $Task.Sha1.ToLowerInvariant()) { throw "SHA1 不符：期望 $($Task.Sha1)，实际 $h" }
                }
                if ([System.IO.File]::Exists($Task.Path)) { [System.IO.File]::Delete($Task.Path) }
                [System.IO.File]::Move($tmp, $Task.Path)
                $r.Status = 'Downloaded'; $r.Bytes = $len; $r.Url = $url
                return $r
            } catch {
                $errors += "[$round] $url -> $($_.Exception.Message)"
                try { if ([System.IO.File]::Exists($tmp)) { [System.IO.File]::Delete($tmp) } } catch { }
                Start-Sleep -Milliseconds (300 * $round)
            }
        }
    }
    $r.Error = ($errors -join '; ')
    return $r
}
'@
. ([scriptblock]::Create($CoreDef))

function New-FileTask([string]$Url, [string]$Path, [string]$Sha1, [long]$Size, [string]$Kind) {
    [pscustomobject]@{ Urls = (Get-SourceUrls $Url); Path = $Path; Sha1 = $Sha1; Size = $Size; Kind = $Kind }
}

function Invoke-Download([object[]]$Tasks, [string]$Title) {
    $total = $Tasks.Count
    if ($total -eq 0) { return @() }
    Write-Host ("[{0}] 共 {1} 个文件，使用 {2} 个线程" -f $Title, $total, $Threads) -ForegroundColor Cyan
    $queue = New-Object 'System.Collections.Concurrent.ConcurrentQueue[object]'
    foreach ($t in $Tasks) { $queue.Enqueue($t) }
    $bag = New-Object 'System.Collections.Concurrent.ConcurrentBag[object]'
    $worker = {
        param($Queue, $Bag, $Core)
        . ([scriptblock]::Create($Core))
        $t = $null
        while ($Queue.TryDequeue([ref]$t)) { $Bag.Add((Invoke-FileTask $t)) }
    }
    $n = [Math]::Min($Threads, $total)
    $pool = [runspacefactory]::CreateRunspacePool(1, $n)
    $pool.Open()
    $jobs = @()
    for ($i = 0; $i -lt $n; $i++) {
        $ps = [powershell]::Create()
        $ps.RunspacePool = $pool
        [void]$ps.AddScript($worker).AddArgument($queue).AddArgument($bag).AddArgument($CoreDef)
        $jobs += [pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke() }
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $last = -1
    while (@($jobs | Where-Object { -not $_.Handle.IsCompleted }).Count -gt 0) {
        Start-Sleep -Milliseconds 500
        $done = $bag.Count
        if ($done -ne $last -and ($sw.ElapsedMilliseconds -ge 1000 -or $done -eq $total)) {
            $bytes = 0; foreach ($x in $bag.ToArray()) { $bytes += $x.Bytes }
            $speed = if ($sw.Elapsed.TotalSeconds -gt 0) { $bytes / 1MB / $sw.Elapsed.TotalSeconds } else { 0 }
            Write-Progress -Activity $Title -Status ("{0}/{1}  {2:N1} MB  {3:N2} MB/s" -f $done, $total, ($bytes / 1MB), $speed) -PercentComplete ([int](100 * $done / $total))
            $last = $done
        }
    }
    foreach ($j in $jobs) {
        try { $j.PS.EndInvoke($j.Handle) } catch { Write-Warning "工作线程异常：$($_.Exception.Message)" }
        $j.PS.Dispose()
    }
    $pool.Close(); $pool.Dispose()
    Write-Progress -Activity $Title -Completed
    $res = $bag.ToArray()
    $d = @($res | Where-Object Status -eq 'Downloaded').Count
    $s = @($res | Where-Object Status -eq 'Skipped').Count
    $f = @($res | Where-Object Status -eq 'Failed').Count
    $mb = ($res | Measure-Object -Property Bytes -Sum).Sum / 1MB
    Write-Host ("[{0}] 完成：新下载 {1}，已存在跳过 {2}，失败 {3}，{4:N1} MB，用时 {5:N1} 秒" -f $Title, $d, $s, $f, $mb, $sw.Elapsed.TotalSeconds) -ForegroundColor $(if ($f) { 'Yellow' } else { 'Green' })
    foreach ($x in ($res | Where-Object Status -eq 'Failed' | Select-Object -First 10)) { Write-Warning "失败：$($x.Path)`n  $($x.Error)" }
    return $res
}

function Get-RemoteJson([string]$Url) {
    $errs = @()
    foreach ($u in (Get-SourceUrls $Url)) {
        for ($i = 1; $i -le 3; $i++) {
            try {
                $wc = New-Object System.Net.WebClient
                $wc.Headers.Add('User-Agent', 'mc-download.ps1/1.0')
                $wc.Encoding = [Text.Encoding]::UTF8
                try { return ($wc.DownloadString($u) | ConvertFrom-Json) } finally { $wc.Dispose() }
            } catch { $errs += "$u -> $($_.Exception.Message)"; Start-Sleep -Milliseconds (300 * $i) }
        }
    }
    throw "获取 $Url 失败：`n$($errs -join "`n")"
}

function Read-JsonFile([string]$Path) { [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json }

# ---------------- 规则 / 支持库 ----------------
$Arch = if ([Environment]::Is64BitOperatingSystem) { '64' } else { '32' }
$OsArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } elseif ([Environment]::Is64BitOperatingSystem) { 'x86_64' } else { 'x86' }
# 新版 natives 以独立库形式给出，用名字后缀区分架构：natives-windows(x64) / -x86 / -arm64
$NativeSuffix = switch ($OsArch) { 'arm64' { 'natives-windows-arm64' } 'x86' { 'natives-windows-x86' } default { 'natives-windows' } }

function Test-Rules($Rules) {
    if ($null -eq $Rules) { return $true }
    $allow = $false
    foreach ($r in $Rules) {
        $match = $true
        if ($r.PSObject.Properties['features']) { $match = $false }   # 依赖特性（如 demo/自定义分辨率）的规则一律不匹配
        if ($match -and $r.os) {
            if ($r.os.name -and $r.os.name -ne 'windows') { $match = $false }
            if ($r.os.arch -and $r.os.arch -ne $OsArch) { $match = $false }
        }
        if ($match) { $allow = ($r.action -eq 'allow') }
    }
    return $allow
}

function Get-MavenPath([string]$Name, [string]$Classifier) {
    $p = $Name.Split(':')
    $ext = 'jar'
    if ($p[-1] -match '@') { $tmp = $p[-1].Split('@'); $p[-1] = $tmp[0]; $ext = $tmp[1] }
    $group = $p[0].Replace('.', '/'); $artifact = $p[1]; $ver = $p[2]
    $cls = if ($Classifier) { "-$Classifier" } elseif ($p.Count -ge 4) { "-$($p[3])" } else { '' }
    return "$group/$artifact/$ver/$artifact-$ver$cls.$ext"
}

function Get-LibraryTasks($VersionJson, [string]$LibDir) {
    $tasks = @{}
    foreach ($lib in $VersionJson.libraries) {
        if (-not (Test-Rules $lib.rules)) { continue }
        if ($lib.name -match ':(natives-windows(-x86|-arm64|-x86_64)?)$' -and $Matches[1] -ne $NativeSuffix) { continue }
        $dl = $lib.downloads
        # 普通 artifact（包括新版 natives-windows 形式的独立库，规则已在上面过滤）
        if ($dl -and $dl.artifact -and $dl.artifact.url) {
            $a = $dl.artifact
            $rel = if ($a.path) { $a.path } else { Get-MavenPath $lib.name $null }
            $path = Join-Path $LibDir $rel
            $tasks[$path] = New-FileTask $a.url $path $a.sha1 ([long]$a.size) 'library'
        } elseif (-not $dl -and $lib.name -and -not $lib.natives) {
            $base = if ($lib.url) { $lib.url.TrimEnd('/') } else { 'https://libraries.minecraft.net' }
            $rel = Get-MavenPath $lib.name $null
            $path = Join-Path $LibDir $rel
            $tasks[$path] = New-FileTask "$base/$rel" $path $null 0 'library'
        }
        # 旧版 natives classifiers
        if ($lib.natives -and $lib.natives.windows) {
            $cls = $lib.natives.windows.Replace('${arch}', $Arch)
            $c = $null
            if ($dl -and $dl.classifiers) { $c = $dl.classifiers.$cls }
            if ($c) {
                $rel = if ($c.path) { $c.path } else { Get-MavenPath $lib.name $cls }
                $path = Join-Path $LibDir $rel
                $tasks[$path] = New-FileTask $c.url $path $c.sha1 ([long]$c.size) 'natives'
            } else {
                $base = if ($lib.url) { $lib.url.TrimEnd('/') } else { 'https://libraries.minecraft.net' }
                $rel = Get-MavenPath $lib.name $cls
                $path = Join-Path $LibDir $rel
                $tasks[$path] = New-FileTask "$base/$rel" $path $null 0 'natives'
            }
        }
    }
    return @($tasks.Values)
}

# ---------------- 主流程 ----------------
Write-Host "下载源：$Source（$(if ($MirrorFirst -and $Source -ne 'Official') {'BMCLAPI 镜像优先，失败回退官方'} else {'官方优先'})）"
Write-Host "正在获取版本列表…"
$manifest = Get-RemoteJson 'https://piston-meta.mojang.com/mc/game/version_manifest_v2.json'

if ($List) {
    Write-Host ("最新正式版：{0}    最新快照：{1}" -f $manifest.latest.release, $manifest.latest.snapshot) -ForegroundColor Green
    $items = $manifest.versions
    if (-not $IncludeSnapshots) { $items = $items | Where-Object { $_.type -eq 'release' } }
    $items | ForEach-Object {
        [pscustomobject]@{ 版本 = $_.id; 类型 = $_.type; 发布时间 = ([datetime]$_.releaseTime).ToString('yyyy-MM-dd') }
    } | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    Write-Host "（默认只列出正式版，加 -IncludeSnapshots 显示全部）"
    return
}

switch ($Version) {
    'latest'          { $Version = $manifest.latest.release }
    'latest-release'  { $Version = $manifest.latest.release }
    'latest-snapshot' { $Version = $manifest.latest.snapshot }
}
$entry = $manifest.versions | Where-Object { $_.id -eq $Version } | Select-Object -First 1
if (-not $entry) { throw "找不到版本 '$Version'，可用 -List 查看所有版本" }

Write-Host ("目标版本：{0}（{1}）  安装目录：{2}" -f $entry.id, $entry.type, $GameDir) -ForegroundColor Green
$swAll = [Diagnostics.Stopwatch]::StartNew()
$allResults = @()

# 1. 版本 json
$verDir = Join-Path (Join-Path $GameDir 'versions') $entry.id
$jsonPath = Join-Path $verDir "$($entry.id).json"
Write-Host "[1/6] 版本 json"
$r = Invoke-FileTask (New-FileTask $entry.url $jsonPath $entry.sha1 0 'json')
if ($r.Status -eq 'Failed') { throw "版本 json 下载失败：$($r.Error)" }
$allResults += $r
$vj = Read-JsonFile $jsonPath
if ($vj.inheritsFrom) { Write-Warning "该 json 继承自 $($vj.inheritsFrom)，本脚本只处理原版" }

$tasks = @()
# 2. 客户端 jar
$c = $vj.downloads.client
$tasks += New-FileTask $c.url (Join-Path $verDir "$($entry.id).jar") $c.sha1 ([long]$c.size) 'client'
# 3. 支持库 + natives
$libTasks = Get-LibraryTasks $vj (Join-Path $GameDir 'libraries')
$tasks += $libTasks
# 4. 资源索引
$ai = $vj.assetIndex
$indexPath = Join-Path (Join-Path (Join-Path $GameDir 'assets') 'indexes') "$($ai.id).json"
$tasks += New-FileTask $ai.url $indexPath $ai.sha1 ([long]$ai.size) 'assetIndex'
# 5. 日志配置
if ($vj.logging -and $vj.logging.client -and $vj.logging.client.file) {
    $lf = $vj.logging.client.file
    $tasks += New-FileTask $lf.url (Join-Path (Join-Path (Join-Path $GameDir 'assets') 'log_configs') $lf.id) $lf.sha1 ([long]$lf.size) 'logging'
}
Write-Host ("[2/6] 客户端 jar、支持库（{0} 个，其中 natives {1} 个）、资源索引、日志配置" -f $libTasks.Count, @($libTasks | Where-Object Kind -eq 'natives').Count)
$allResults += Invoke-Download $tasks '本体与支持库'
if (-not (Test-Path $indexPath)) { throw '资源索引下载失败，无法继续' }

# 6. 资源文件
$idx = Read-JsonFile $indexPath
$objDir = Join-Path (Join-Path $GameDir 'assets') 'objects'
$assetTasks = @{}
$copyList = @()
$isVirtual = [bool]$idx.virtual
$mapRes = [bool]$idx.map_to_resources
foreach ($p in $idx.objects.PSObject.Properties) {
    $h = $p.Value.hash
    $path = Join-Path (Join-Path $objDir $h.Substring(0, 2)) $h
    if (-not $assetTasks.ContainsKey($path)) {
        $assetTasks[$path] = New-FileTask "https://resources.download.minecraft.net/$($h.Substring(0,2))/$h" $path $h ([long]$p.Value.size) 'asset'
    }
    if ($isVirtual -or $mapRes) { $copyList += [pscustomobject]@{ Name = $p.Name; Src = $path } }
}
Write-Host ("[3/6] 资源文件（索引 {0}，{1} 个对象）" -f $ai.id, $assetTasks.Count)
$allResults += Invoke-Download @($assetTasks.Values) '资源文件'

if ($copyList.Count -gt 0) {
    $targets = @()
    if ($isVirtual) { $targets += (Join-Path (Join-Path (Join-Path $GameDir 'assets') 'virtual') $ai.id) }
    if ($mapRes) { $targets += (Join-Path $GameDir 'resources') }
    Write-Host ("[4/6] 旧版资源：复制 {0} 个文件到 {1}" -f $copyList.Count, ($targets -join '、'))
    foreach ($t in $targets) {
        foreach ($x in $copyList) {
            if (-not (Test-Path -LiteralPath $x.Src)) { continue }
            $dst = Join-Path $t ($x.Name.Replace('/', [IO.Path]::DirectorySeparatorChar))
            if ((Test-Path -LiteralPath $dst) -and (Get-Item -LiteralPath $dst).Length -eq (Get-Item -LiteralPath $x.Src).Length) { continue }
            $d = Split-Path $dst -Parent
            if (-not (Test-Path -LiteralPath $d)) { [void](New-Item -ItemType Directory -Path $d -Force) }
            Copy-Item -LiteralPath $x.Src -Destination $dst -Force
        }
    }
} else { Write-Host "[4/6] 非旧版资源格式，无需复制" }

# launcher_profiles.json
$lp = Join-Path $GameDir 'launcher_profiles.json'
if (-not (Test-Path $lp)) {
    Write-Host "[5/6] 创建 launcher_profiles.json"
    $now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $obj = [ordered]@{
        profiles = [ordered]@{
            ('mcdl-' + $entry.id) = [ordered]@{ name = $entry.id; type = 'custom'; lastVersionId = $entry.id; created = $now; lastUsed = $now; icon = 'Grass' }
        }
        settings = [ordered]@{}
        version  = 3
    }
    [IO.File]::WriteAllText($lp, ($obj | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
} else { Write-Host "[5/6] launcher_profiles.json 已存在，保持不变" }

# 汇总
$failed = @($allResults | Where-Object Status -eq 'Failed')
$down = @($allResults | Where-Object Status -eq 'Downloaded')
$skip = @($allResults | Where-Object Status -eq 'Skipped')
$mb = ($allResults | Measure-Object -Property Bytes -Sum).Sum / 1MB
Write-Host ''
Write-Host '[6/6] ========== 汇总 ==========' -ForegroundColor Cyan
Write-Host ("版本：{0}    目录：{1}" -f $entry.id, $GameDir)
Write-Host ("文件总数：{0}    新下载：{1}    已存在且校验通过：{2}    失败：{3}" -f $allResults.Count, $down.Count, $skip.Count, $failed.Count)
Write-Host ("下载量：{0:N1} MB    总用时：{1:N1} 秒" -f $mb, $swAll.Elapsed.TotalSeconds)
if ($failed.Count -gt 0) {
    Write-Host "有 $($failed.Count) 个文件失败，重新运行本脚本即可只补下缺失文件。" -ForegroundColor Yellow
    exit 1
}
Write-Host '全部文件 SHA1 校验通过！可以用 PCL / HMCL / 官方启动器选择该 .minecraft 文件夹启动（需要已安装 Java）。' -ForegroundColor Green
exit 0
