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
# 用绝对路径：exe 的工作目录不一定是 $root，传相对路径会写到别处（曾因此排版找不到 raw）
$raw = Join-Path $root "docs\release\$Version\raw"

Write-Output "== 1/3 出图专用发布 =="
& $Dotnet publish "src\KeySpirit\KeySpirit.csproj" -c Release -r win-x64 --self-contained false -o "dist\_shot" --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败：多半是 dist\_shot 里的 dll 被占用" }

Write-Output "== 2/3 逐页截图 =="
# 刚 publish 完时 exe 偶发启动失败（无输出、无图），重试一次即可；仍失败才抛错
for ($try = 1; $try -le 2; $try++) {
    & "dist\_shot\KeySpirit.exe" --shot $raw
    $made = @(Get-ChildItem -Path $raw -Filter *.png -ErrorAction SilentlyContinue).Count
    if ($made -ge 1) { break }
    Write-Output "  截图未产出（第 $try 次），重试…"
    Start-Sleep -Milliseconds 800
}
if (@(Get-ChildItem -Path $raw -Filter *.png -ErrorAction SilentlyContinue).Count -lt 1) {
    throw "截图失败：$raw 下没有 png"
}

Write-Output "== 3/3 排版 =="
python "scripts\make_release_shots.py" --version $Version
if ($LASTEXITCODE -ne 0) { throw "排版失败：需要 Python + Pillow" }

Write-Output ""
Write-Output "配图完成：docs\release\$Version\"
Get-ChildItem "docs\release\$Version\*.png" | ForEach-Object { Write-Output ("  " + $_.Name) }
