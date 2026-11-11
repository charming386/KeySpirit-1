<#
键灵 发布脚本
用途：编译 → 发布到 dist\KeySpirit → 更新构建信息时间 → 刷新桌面/开始菜单快捷方式 → 清图标缓存

用法（在项目根目录）：
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

注意：游戏正在运行时 KeySpirit.dll 会被占用，publish 会失败。先退出游戏（面板里「退出」）再跑。
#>
param(
    [string]$OutDir = "dist\KeySpirit",
    [string]$Dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 必须先转绝对路径：下面用了 [IO.File]::ReadAllLines / WriteAllText，
# 这两个 .NET API 走的是**进程工作目录**，不是 PowerShell 的 Set-Location。
# 从别处（比如 IDE 的任务目录）调用本脚本时，两者不是同一个地方，
# 相对路径会在第 2 步直接炸「未能找到路径」（v0.15.0 发版时踩过一次）。
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $root $OutDir }

if (-not (Test-Path $Dotnet)) { $Dotnet = "dotnet" }

Write-Output "== 1/4 发布 =="
& $Dotnet publish "src\KeySpirit\KeySpirit.csproj" -c Release -r win-x64 --self-contained false -o $OutDir --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败：多半是游戏还在运行，KeySpirit.dll 被占用" }

Write-Output "== 2/4 构建信息 =="
$info = Join-Path $OutDir "构建信息.txt"
if (Test-Path $info) {
    $stamp = "构建时间：" + (Get-Date -Format "yyyy-MM-dd HH:mm")
    $lines = [IO.File]::ReadAllLines($info, [Text.Encoding]::UTF8)
    $lines = $lines -replace '^构建时间：.*$', $stamp
    [IO.File]::WriteAllText($info, ($lines -join "`r`n"), (New-Object Text.UTF8Encoding $false))
    Write-Output "  $stamp"
}

Write-Output "== 3/4 快捷方式 =="
$target = (Resolve-Path (Join-Path $OutDir "KeySpirit.exe")).Path
$lnks = @(
    "$env:USERPROFILE\Desktop\KeySpirit.lnk",
    "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\KeySpirit.lnk"
)
foreach ($lnk in $lnks) {
    if (-not (Test-Path $lnk)) { Write-Warning "  缺少快捷方式：$lnk"; continue }
    # .lnk 里的目标路径既有 ANSI 段也有 UTF-16 段，两处都查，避免误判
    $bytes = [IO.File]::ReadAllBytes($lnk)
    $hit = [Text.Encoding]::ASCII.GetString($bytes).Contains($target) -or
           [Text.Encoding]::Unicode.GetString($bytes).Contains($target)
    if (-not $hit) {
        Write-Warning "  指向的不是本次发布，请手工改：$lnk"
    }
    (Get-Item $lnk).LastWriteTime = Get-Date   # 时间戳前进，资源管理器重新读取
    Write-Output "  已刷新：$lnk"
}

Write-Output "== 4/4 图标缓存 =="
Start-Process "ie4uinit.exe" -ArgumentList "-show" -WindowStyle Hidden

Write-Output ""
Write-Output "发布完成：$target"
Write-Output "自测：KeySpirit.exe --selftest（结果在 %TEMP%\keyspirit_selftest.txt）"
