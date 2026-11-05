"""打发布 zip：单文件 exe + 资产 + 说明文档。

用法：
    python scripts/make_release_zip.py --version v0.12.1
"""
import argparse
import os
import shutil
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
shutil.copy2(os.path.join(SINGLE, "KeySpirit.exe"), os.path.join(STAGE, "KeySpirit.exe"))

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
