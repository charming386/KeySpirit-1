<#
键灵 发布配图脚本
用途：发布到临时目录 → 逐页出透明底原图 → 排成发布用卡片图

用法（在项目根目录）：
    powershell -ExecutionPolicy Bypass -File scripts\release_shots.ps1 -Version v0.12.0

产出：
    docs\release\<版本>\raw\*.png    原始控件图（透明底，2 倍像素）
    docs\release\<版本>\*.png        排版好的配图（可直接进 Release / README）

什么时候要跑：
    · 有新功能上线、界面有可见变化 → 必须出图，Release 正文配图说明
    · 纯 bug 修复、数值调整、性能优化且界面外观没变 → 不用出图，文字说清即可

注意：脚本会真的弹出面板几秒钟再自己关掉，属于正常现象。
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Test-Path $Dotnet)) { $Dotnet = "dotnet" }
$raw = "docs\release\$Version\raw"

Write-Output "== 1/3 出图专用发布 =="
& $Dotnet publish "src\KeySpirit\KeySpirit.csproj" -c Release -r win-x64 --self-contained false -o "dist\_shot" --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败：多半是 dist\_shot 里的 dll 被占用" }

Write-Output "== 2/3 逐页截图 =="
& "dist\_shot\KeySpirit.exe" --shot $raw
if ($LASTEXITCODE -ne 0) { throw "截图失败：见上方输出" }

Write-Output "== 3/3 排版 =="
python "scripts\make_release_shots.py" --version $Version
if ($LASTEXITCODE -ne 0) { throw "排版失败：需要 Python + Pillow" }

Write-Output ""
Write-Output "配图完成：docs\release\$Version\"
Get-ChildItem "docs\release\$Version\*.png" | ForEach-Object { Write-Output ("  " + $_.Name) }
