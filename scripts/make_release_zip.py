"""打发布 zip：单文件 exe + 资产 + 说明文档。

用法：
    python scripts/make_release_zip.py --version v0.12.1
"""
import argparse
import os
import shutil
import subprocess
import sys
import time
import zipfile

ROOT = r"D:\project\TaskbarIdle"
SINGLE = os.path.join(ROOT, "release", "_single")
DIST = os.path.join(ROOT, "dist", "KeySpirit")
PKG = os.path.join(ROOT, "release", "pkg")

ap = argparse.ArgumentParser()
ap.add_argument("--version", default="v0.12.1", help="版本号，如 v0.12.1")
a = ap.parse_args()
VER = a.version

NAME = f"KeySpirit-{VER}-win-x64"
STAGE = os.path.join(PKG, NAME)
OUT = os.path.join(ROOT, "release", NAME + ".zip")

if os.path.isdir(PKG):
    shutil.rmtree(PKG)
os.makedirs(STAGE)

# 主程序：自包含单文件
#
# ⚠ 必须每次**现发布**，不能复用 release/_single 里上一次的 exe。
# v0.12.3 就是踩了这个坑：脚本只 shutil.copy2，而 _single 还是 v0.12.2 构建的产物，
# 于是 GitHub 上的 v0.12.3 包解开是 v0.12.2 的程序 —— 说明文档是新的、行为是旧的。
DOTNET = os.path.join(os.environ.get("USERPROFILE", ""), ".dotnet", "dotnet.exe")
if not os.path.exists(DOTNET):
    DOTNET = "dotnet"

print("发布自包含单文件 exe …")
r = subprocess.run(
    [DOTNET, "publish", os.path.join(ROOT, "src", "KeySpirit", "KeySpirit.csproj"),
     "-c", "Release", "-r", "win-x64", "--self-contained", "true",
     "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
     "-p:EnableCompressionInSingleFile=true",
     "-o", SINGLE, "--nologo"],
    capture_output=True, text=True)
if r.returncode != 0:
    print(r.stdout[-2000:])
    print(r.stderr[-2000:])
    sys.exit("发布失败：dotnet publish 返回 %d（游戏在运行会锁 dll）" % r.returncode)

SINGLE_EXE = os.path.join(SINGLE, "KeySpirit.exe")
# 兜底校验：exe 必须是刚刚生成的，否则说明 publish 静默失败了
if time.time() - os.path.getmtime(SINGLE_EXE) > 300:
    sys.exit("发布失败：%s 不是本次生成的，拒绝打包" % SINGLE_EXE)

shutil.copy2(SINGLE_EXE, os.path.join(STAGE, "KeySpirit.exe"))

# 资产（ico 必须在 exe 旁边，单文件不打包它们；tutorial.html 与 svg 一并带上）
shutil.copytree(os.path.join(DIST, "Assets"), os.path.join(STAGE, "Assets"))

# 文档
shutil.copy2(os.path.join(DIST, "README.txt"), os.path.join(STAGE, "README.txt"))
shutil.copy2(os.path.join(DIST, "构建信息.txt"), os.path.join(STAGE, "构建信息.txt"))
shutil.copy2(r"D:\project\KeySpirit-dist\使用说明.txt", os.path.join(STAGE, "使用说明.txt"))

os.makedirs(os.path.join(STAGE, "更新说明"))
notice = f"{VER}.md"
shutil.copy2(os.path.join(ROOT, "更新说明", notice),
             os.path.join(STAGE, "更新说明", notice))

# 打包
if os.path.exists(OUT):
    os.remove(OUT)
total = 0
with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for base, _dirs, files in os.walk(STAGE):
        for f in files:
            full = os.path.join(base, f)
            rel = os.path.relpath(full, STAGE)
            z.write(full, rel.replace("\\", "/"))
            total += 1

size = os.path.getsize(OUT)
print("文件数:", total)
print("zip:", OUT)
print("大小: %.2f MB" % (size / 1048576))
print("exe :", os.path.getsize(os.path.join(SINGLE, "KeySpirit.exe")))
