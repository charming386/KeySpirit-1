"""把 `KeySpirit.exe --shot` 出的原图排版成发布配图。

用法（项目根目录）：
    KeySpirit.exe --shot docs/release/v0.12.1/raw
    python scripts/make_release_shots.py --version v0.12.1

产出（docs/release/<版本>/*.png）：
    01-battle.png   战斗详情（怪物种类 + 图标 + 固定红血条）
    02-claim.png    每日敲击与两档每日礼包
    03-shop.png     商店「单次数量」一档控制
    04-hp.png       玩家血条三档配色对比
    05-bars.png     （可选）主面板整体

为什么单独做这一步：--shot 出的是透明底原始控件图，尺寸随面板高度变，
直接扔进 README 会出现大块空白和大小不一。这里统一成同宽卡片，
标题/说明文字进图，正文里就只需要一行 ![](...)。
"""
import argparse
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FONT_DIR = r"C:\Windows\Fonts"
W = 1200                 # 卡片宽度固定，README 里视觉一致
PAD = 44
BG = (11, 15, 20)
BG2 = (16, 23, 31)
FG = (232, 238, 243)
DIM = (141, 154, 166)
ACC = (79, 214, 224)


def font(size, bold=False):
    name = "msyhbd.ttc" if bold else "msyh.ttc"
    return ImageFont.truetype(os.path.join(FONT_DIR, name), size)


def canvas(h):
    """深色底 + 右上角一团极淡青光，避免整张纯黑发死。"""
    im = Image.new("RGB", (W, h), BG)
    glow = Image.new("L", (W, h), 0)
    d = ImageDraw.Draw(glow)
    d.ellipse([W - 520, -380, W + 260, 360], fill=44)
    glow = glow.filter(ImageFilter.GaussianBlur(150))
    im.paste(Image.new("RGB", (W, h), BG2), (0, 0), glow)
    return im


def load(path):
    im = Image.open(path).convert("RGBA")
    box = im.split()[3].getbbox()
    if box:
        im = im.crop(box)
    return im


def card(src, out, title, note, version):
    shot = load(src)
    maxw = W - PAD * 2
    if shot.width > maxw:
        shot = shot.resize((maxw, round(shot.height * maxw / shot.width)), Image.LANCZOS)

    top, bottom = 104, 92
    im = canvas(top + shot.height + bottom)
    d = ImageDraw.Draw(im)

    d.text((PAD, 40), title, font=font(30, True), fill=FG)
    tag = "键灵 " + version
    tw = d.textlength(tag, font=font(16))
    d.text((W - PAD - tw, 50), tag, font=font(16), fill=ACC)

    im.paste(shot, ((W - shot.width) // 2, top), shot)
    d.text((PAD, top + shot.height + 30), note, font=font(17), fill=DIM)

    im.save(out, optimize=True)
    print("card:", os.path.basename(out), f"{im.width}x{im.height}")


def hp_compare(raw, out, version):
    """血条三档：只截「生命 + 血条」那一条，横排三栏对照。"""
    rows = [("满血", "hp-full", (62, 193, 143)),
            ("45%", "hp-mid", (242, 166, 59)),
            ("12%", "hp-low", (216, 69, 60))]

    strips = []
    for label, name, color in rows:
        im = load(os.path.join(raw, name + ".png"))
        im = im.crop((0, 0, im.width, 222))       # 等级 / 经验 / 生命 / 层数 / 金币 / 暂停，正好一个完整头块
        strips.append((label, color, im))

    col_w = (W - PAD * 2 - 24 * (len(strips) - 1)) // len(strips)
    scaled = [(l, c, i.resize((col_w, round(i.height * col_w / i.width)), Image.LANCZOS))
              for l, c, i in strips]

    top, bottom, label_h = 104, 128, 40
    im = canvas(top + label_h + scaled[0][2].height + bottom)
    d = ImageDraw.Draw(im)

    d.text((PAD, 40), "血条配色：按血量比例分档", font=font(30, True), fill=FG)
    tag = "键灵 " + version
    tw = d.textlength(tag, font=font(16))
    d.text((W - PAD - tw, 50), tag, font=font(16), fill=ACC)

    total = col_w * len(scaled) + 24 * (len(scaled) - 1)
    x = (W - total) // 2
    for label, color, s in scaled:
        d.text((x + 6, top + 8), label, font=font(20, True), fill=color)
        im.paste(s, (x, top + label_h), s)
        x += col_w + 24

    y = top + label_h + scaled[0][2].height + 26
    d.text((PAD, y), "玩家：满血绿 → 低于 60% 琥珀 → 低于 20% 红；休整中置灰",
           font=font(17), fill=DIM)
    d.text((PAD, y + 30), "怪物血条固定红色，不随百分比变色，一眼区分「谁在掉血」",
           font=font(17), fill=DIM)

    im.save(out, optimize=True)
    print("card:", os.path.basename(out), f"{im.width}x{im.height}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="v0.12.1")
    ap.add_argument("--raw", default=None)
    ap.add_argument("--out", default=None)
    a = ap.parse_args()

    raw = a.raw or os.path.join(ROOT, "docs", "release", a.version, "raw")
    out = a.out or os.path.join(ROOT, "docs", "release", a.version)
    os.makedirs(out, exist_ok=True)

    card(os.path.join(raw, "02-battle.png"), os.path.join(out, "01-battle.png"),
         "战斗详情：打的哪只怪、第几只、还剩多少血", "小怪 10 种 / 精英 5 种 / 守层者 3 种，"
         "三围各有高低；类型前挂档位图标", a.version)

    card(os.path.join(raw, "03-claim.png"), os.path.join(out, "02-claim.png"),
         "每日敲击与每日礼包：跨零点自动重置", "今日敲击按当日累计、上限 20,000（40 袋 × 500 敲），"
         "到顶后只留数字并继续计数但不再掉袋；礼包固定两档、各限领一次", a.version)

    card(os.path.join(raw, "04-shop.png"), os.path.join(out, "03-shop.png"),
         "商店：单次数量一档控制", "1 / 10 / 100 / 1K / 1M / 1B / max 七档切换，"
         "买墨晶与开墨匣共用同一档位", a.version)

    hp_compare(raw, os.path.join(out, "04-hp.png"), a.version)

    card(os.path.join(raw, "07-note.png"), os.path.join(out, "05-note.png"),
         "悬停 1.5 秒出注释：说不清的按钮直接讲明白",
         "鼠标停在行上停满 1.5 秒才弹，划过不弹；设置、本命器、转生三处的按钮都带解释，"
         "置灰的按钮照样能看（层数不够时更要说明代价）", a.version)


if __name__ == "__main__":
    main()
