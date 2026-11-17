"""把 `KeySpirit.exe --shot` 出的原图排版成发布配图。

用法（项目根目录）：
    KeySpirit.exe --shot docs/release/v0.12.1/raw
    python scripts/make_release_shots.py --version v0.12.1

产出（docs/release/<版本>/*.png）：
    01-battle.png      战斗详情（怪物种类 + 图标 + 固定红血条 + 本回合两行入口）
    02-fight-out.png   战斗详情 › 我对怪（三级页，回合 / 基础 / 暴击 / 总伤）
    03-fight-in.png    战斗详情 › 怪对我（三级页，回合 / 怪攻 / 减伤 / 掉血）
    04-claim.png       每日敲击与两档每日礼包（v0.24.0 起按「折合几级强化」定标）
    09-hp.png          玩家血条三档配色对比（界面配色有改动时才要）

为什么单独做这一步：--shot 出的是透明底原始控件图，尺寸随面板高度变，
直接扔进 README 会出现大块空白和大小不一。这里统一成同宽卡片，
标题/说明文字进图，正文里就只需要一行 ![](…)。
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


def wrap_text(text, f, maxw):
    """按像素宽度折行。中文没有空格不能按词断，只能逐字量（getlength 是精确宽度）。"""
    lines, cur = [], ""
    for ch in text:
        if not cur or f.getlength(cur + ch) <= maxw:
            cur += ch
        else:
            lines.append(cur)
            cur = ch
    if cur:
        lines.append(cur)
    return lines


def card(src, out, title, note, version):
    shot = load(src)
    maxw = W - PAD * 2
    if shot.width > maxw:
        shot = shot.resize((maxw, round(shot.height * maxw / shot.width)), Image.LANCZOS)

    # 说明文字按画布宽折行：写长了直接被裁掉（v0.26.0 的礼包那张就溢出去了），
    # 靠人肉控字数迟早再犯，索性交给排版。
    f_note = font(17)
    lines = wrap_text(note, f_note, maxw)
    lh = 26
    top = 104
    bottom = 30 + len(lines) * lh + 26
    im = canvas(top + shot.height + bottom)
    d = ImageDraw.Draw(im)

    d.text((PAD, 40), title, font=font(30, True), fill=FG)
    tag = "键灵 " + version
    tw = d.textlength(tag, font=font(16))
    d.text((W - PAD - tw, 50), tag, font=font(16), fill=ACC)

    im.paste(shot, ((W - shot.width) // 2, top), shot)
    for i, ln in enumerate(lines):
        d.text((PAD, top + shot.height + 30 + i * lh), ln, font=f_note, fill=DIM)

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
         "战斗详情：这一只打到哪儿了，最后两行点进去看流水",
         "前六段只读（怪物 / 血条 / 层内序号 / 生命 / 攻防 / 我方）；"
         "「本回合」两行是唯一可点的，右侧给最新一回合的净结果", a.version)

    card(os.path.join(raw, "11-fight-out.png"), os.path.join(out, "02-fight-out.png"),
         "战斗详情 › 我对怪：基础与暴击分开列",
         "只给总伤时，\"这一刀打了 8 倍\"分不清是暴击还是这只怪防御低 —— "
         "前者运气、后者配装。橙色那截就是暴击附加", a.version)

    card(os.path.join(raw, "12-fight-in.png"), os.path.join(out, "03-fight-in.png"),
         "战斗详情 › 怪对我：怪攻与被减掉多少",
         "只记掉血的话，高防御时看到\"1.0K\"会以为是怪太弱，看不出是自己护甲在干活；"
         "打死那一回合整行写「打死」，因为那刀没有反击", a.version)

    card(os.path.join(raw, "03-claim.png"), os.path.join(out, "04-claim.png"),
         "每日敲击与每日礼包：按「折合几级强化」定标",
         "5,000 档折合 14 级强化（发金币）、20,000 档折合 20 级（发墨晶）；"
         "折级不随层数漂移 —— 旧的线性公式在 480 层就永久冻在 150 颗，深处等于空包", a.version)


if __name__ == "__main__":
    main()
