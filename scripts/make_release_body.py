"""把仓库内的更新说明转成 GitHub Release 正文（图片改绝对 raw 链接）。

用法：
    python scripts/make_release_body.py --version v0.12.1
"""
import argparse
import io
import re

ROOT = r"D:\project\TaskbarIdle"
OUT = r"D:\project\TaskbarIdle\release\release_body.md"
RAW = "https://raw.githubusercontent.com/charming386/KeySpirit-1/main/docs/release/"

ap = argparse.ArgumentParser()
ap.add_argument("--version", default="v0.12.1", help="版本号，如 v0.12.1")
a = ap.parse_args()
VER = a.version
SRC = ROOT + r"\更新说明\%s.md" % VER

s = io.open(SRC, encoding="utf-8").read()

# 相对路径 ../docs/release/vX.Y.Z/xx.png → 绝对 raw 链接（Release 页解析不到相对路径）
s = s.replace("](../docs/release/", "](" + RAW)

# 标题下加一行下载提示
s = s.replace(
    "---\n\n## 一、概述",
    "> 下载 **Assets** 里的 `KeySpirit-%s-win-x64.zip`，解压后双击 `KeySpirit.exe` 即可，"
    "无需安装、无需 .NET。\n\n---\n\n## 一、概述" % VER,
    1,
)

io.open(OUT, "w", encoding="utf-8").write(s)
print("正文字数:", len(s))
print("图片链接:", len(re.findall(r"raw\.githubusercontent\.com", s)))
