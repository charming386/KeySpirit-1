"""键灵 asset generator.

Single source of truth: 32x32 pixel grids (Python lists) -> both
  * SVG (one rect per horizontal run, shape-rendering=crispEdges)
  * multi-size .ico (nearest-neighbour upscale, no PNG intermediate files)
Tutorial illustrations are emitted as plain (non-pixel-grid) SVGs.

Run:  python scripts/gen_assets.py
Out:  assets/svg/*.svg, assets/ico/*.ico
"""
from __future__ import annotations

import os
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SVG_DIR = os.path.join(ROOT, "assets", "svg")
ICO_DIR = os.path.join(ROOT, "assets", "ico")

N = 32

# ---- palette -------------------------------------------------------------
INK = (10, 14, 19, 255)
INK2 = (20, 27, 38, 255)
INK3 = (30, 40, 55, 255)
RIM = (34, 66, 84, 255)
CYAN = (79, 214, 224, 255)
CYAN_D = (36, 120, 130, 255)
CYAN_L = (160, 240, 245, 255)
AMB = (242, 166, 59, 255)
GOLD = (255, 209, 102, 255)
GOLD_D = (176, 132, 44, 255)
CRIM = (194, 69, 63, 255)
CRIM_D = (110, 34, 32, 255)
GREY = (108, 116, 126, 255)
GREY_D = (62, 68, 76, 255)
RUST = (150, 96, 60, 255)


def blank():
    return [[None] * N for _ in range(N)]


def put(g, x, y, c):
    if 0 <= x < N and 0 <= y < N:
        g[y][x] = c


def rect(g, x0, y0, x1, y1, c):
    for y in range(y0, y1 + 1):
        for x in range(x0, x1 + 1):
            put(g, x, y, c)


def hline(g, x0, x1, y, c):
    rect(g, x0, y, x1, y, c)


# ---- ink spirit body -----------------------------------------------------
# half-width profile for rows 6..26 (droplet silhouette)
PROFILE = [1, 2, 2, 3, 3, 4, 4, 5, 6, 6, 7, 7, 7, 7, 7, 6, 6, 5, 4, 3, 2]


def draw_body(g, y0=6, body=INK3, shade=INK2, rim=(56, 104, 128, 255), flat=0):
    for i, hw in enumerate(PROFILE):
        y = y0 + i + flat
        rect(g, 16 - hw, y, 16 + hw - 1, y, body)
        put(g, 16 - hw, y, rim)              # left rim light
        put(g, 16 + hw - 1, y, shade)        # right shade


def draw_eyes(g, y=16, color=CYAN, style="solid"):
    if style == "solid":
        rect(g, 12, y, 13, y + 2, color)
        rect(g, 18, y, 19, y + 2, color)
        put(g, 12, y, CYAN_L)
        put(g, 18, y, CYAN_L)
    elif style == "closed":
        hline(g, 11, 14, y + 1, CYAN_D)
        hline(g, 17, 20, y + 1, CYAN_D)
    elif style == "hollow":
        for (x0, x1) in ((12, 13), (18, 19)):
            hline(g, x0, x1, y, color)
            hline(g, x0, x1, y + 2, color)
            put(g, x0, y + 1, color)
            put(g, x1, y + 1, color)


def sprite_base():
    g = blank()
    draw_body(g)
    draw_eyes(g)
    # bottom cyan rim so the droplet reads on a dark tray
    for i, hw in enumerate(PROFILE):
        if i > 12:
            put(g, 16 - hw, 6 + i, CYAN_D)
    # drip tail
    rect(g, 15, 27, 16, 28, INK3)
    put(g, 15, 28, (56, 104, 128, 255))
    return g


def sprite_reward():
    g = sprite_base()
    # amber pouch, right side
    rect(g, 22, 15, 28, 22, AMB)
    rect(g, 22, 15, 28, 15, GOLD)
    rect(g, 22, 22, 28, 22, GOLD_D)
    rect(g, 23, 13, 27, 14, AMB)          # neck
    hline(g, 23, 27, 12, GOLD)            # tie
    put(g, 25, 17, GOLD)
    put(g, 26, 19, GOLD)
    put(g, 24, 20, GOLD_D)
    # pending badge
    rect(g, 25, 2, 29, 6, (255, 138, 40, 255))
    put(g, 25, 2, (255, 190, 120, 255))
    return g


def sprite_rest():
    g = blank()
    # wide melted puddle
    prof = [3, 4, 6, 7, 8, 9, 10, 10, 9, 8, 6]
    for i, hw in enumerate(prof):
        y = 20 + i
        rect(g, 16 - hw, y, 16 + hw - 1, y, INK3)
        put(g, 16 - hw, y, (56, 104, 128, 255))
        put(g, 16 + hw - 1, y, INK2)
    # closed eyes as two short lines
    hline(g, 11, 14, 24, GREY)
    hline(g, 17, 20, 24, GREY)
    # sweat drop
    rect(g, 24, 15, 26, 17, GREY)
    put(g, 25, 18, GREY_D)
    return g


def sprite_pause():
    g = blank()
    # outline-only body
    for i, hw in enumerate(PROFILE):
        y = 8 + i
        put(g, 16 - hw, y, CYAN_D)
        put(g, 16 + hw - 1, y, CYAN_D)
        if 0 < i < len(PROFILE) - 1:
            prev, nxt = PROFILE[i - 1], PROFILE[i + 1]
            if nxt > hw:
                hline(g, 16 - nxt, 16 - hw - 1, y, CYAN_D)
                hline(g, 16 + hw, 16 + nxt - 1, y, CYAN_D)
            if prev > hw:
                hline(g, 16 - prev, 16 - hw - 1, y, CYAN_D)
                hline(g, 16 + hw, 16 + prev - 1, y, CYAN_D)
    draw_eyes(g, y=18, color=CYAN, style="hollow")
    # pause bars
    rect(g, 13, 2, 14, 6, CYAN)
    rect(g, 17, 2, 18, 6, CYAN)
    return g


# ---- panel / menu icons --------------------------------------------------
def icon_crystal():
    g = blank()
    rect(g, 14, 8, 17, 10, CYAN_D)
    rect(g, 12, 11, 19, 13, (28, 84, 96, 255))
    rect(g, 13, 14, 19, 19, (24, 70, 82, 255))
    rect(g, 15, 20, 18, 23, (20, 58, 70, 255))
    rect(g, 14, 24, 17, 25, (16, 44, 54, 255))
    put(g, 15, 9, CYAN_L)
    put(g, 14, 12, CYAN)
    put(g, 13, 13, CYAN)
    hline(g, 13, 14, 15, CYAN)
    return g


def icon_blade():
    g = blank()
    # vertical sword blade (wide enough to read at 16px)
    for y in range(3, 21):
        rect(g, 14, y, 17, y, INK3)
        put(g, 14, y, CYAN)
        put(g, 17, y, INK2)
    rect(g, 15, 3, 16, 5, INK3)               # tip taper
    put(g, 15, 2, CYAN_D)
    rect(g, 15, 6, 16, 6, CYAN_L)             # glint
    rect(g, 9, 21, 22, 22, (56, 104, 128, 255))   # guard
    hline(g, 9, 22, 21, (30, 40, 55, 255))
    rect(g, 14, 23, 17, 26, INK2)             # grip
    rect(g, 13, 27, 18, 28, GOLD)             # pommel
    put(g, 13, 28, GOLD_D)
    return g


def icon_robe():
    g = blank()
    # hood
    rect(g, 12, 4, 19, 5, INK3)
    rect(g, 11, 6, 20, 9, INK3)
    rect(g, 14, 7, 17, 9, INK)                # hood opening
    put(g, 15, 8, CYAN_D)
    put(g, 16, 8, CYAN_D)
    # shoulders
    rect(g, 9, 10, 22, 12, INK3)
    # body widening downward
    for y in range(13, 27):
        hw = 7 + min(4, (y - 13) // 4)
        rect(g, 16 - hw, y, 16 + hw - 1, y, INK3)
        put(g, 16 - hw, y, (56, 104, 128, 255))
    # center seam + cyan hem
    for y in range(12, 26):
        put(g, 16, y, INK2)
    hline(g, 10, 21, 26, CYAN_D)
    hline(g, 12, 19, 11, CYAN)
    return g


def icon_eye():
    g = blank()
    # almond eye: symmetric lens
    lens = [2, 4, 6, 7, 8, 8, 8, 7, 6, 4, 2]
    for i, hw in enumerate(lens):
        y = 9 + i
        rect(g, 16 - hw, y, 16 + hw - 1, y, (26, 44, 56, 255))
        put(g, 16 - hw, y, (56, 104, 128, 255))
    # iris
    rect(g, 12, 12, 19, 17, (18, 84, 96, 255))
    rect(g, 13, 13, 18, 16, CYAN)
    rect(g, 15, 13, 16, 14, CYAN_L)
    # drip
    put(g, 15, 20, CYAN_D)
    put(g, 15, 21, CYAN_D)
    put(g, 16, 22, CYAN_D)
    return g


def icon_seal():
    g = blank()
    rect(g, 7, 7, 24, 24, INK2)
    for x in range(7, 25):                    # bevel
        put(g, x, 7, INK3)
        put(g, 7, x, INK3)
    # spiral sigil
    pts = [(16, 11), (19, 11), (21, 13), (21, 17), (19, 20), (15, 20),
           (12, 18), (12, 14), (14, 12), (17, 12), (18, 14), (18, 17),
           (16, 17)]
    for (x, y) in pts:
        put(g, x, y, CYAN)
    put(g, 16, 15, CYAN_L)
    return g


def icon_pouch():
    g = blank()
    # round bag body
    prof = [3, 5, 6, 7, 7, 7, 7, 6, 5, 3]
    for i, hw in enumerate(prof):
        y = 13 + i
        rect(g, 16 - hw, y, 16 + hw - 1, y, (58, 48, 40, 255))
        put(g, 16 - hw, y, (92, 76, 60, 255))
        put(g, 16 + hw - 1, y, (36, 30, 26, 255))
    # neck + tie
    rect(g, 13, 10, 18, 12, (58, 48, 40, 255))
    hline(g, 12, 19, 12, GOLD_D)
    hline(g, 12, 19, 11, GOLD)
    # coin peeking
    rect(g, 15, 8, 18, 10, GOLD)
    put(g, 15, 8, (255, 240, 190, 255))
    # coins showing through
    put(g, 13, 17, GOLD)
    put(g, 18, 18, GOLD)
    put(g, 15, 19, GOLD_D)
    put(g, 19, 20, GOLD)
    return g


def icon_box():
    g = blank()
    rect(g, 8, 14, 23, 25, RUST)              # chest body
    rect(g, 8, 14, 23, 17, (176, 116, 72, 255))  # lid
    hline(g, 8, 23, 18, (96, 62, 40, 255))
    rect(g, 14, 18, 17, 21, GOLD_D)           # latch
    put(g, 15, 19, GOLD)
    # ink tendrils
    for (x, y) in [(9, 12), (10, 11), (21, 12), (22, 11), (15, 26), (16, 27)]:
        put(g, x, y, INK)
    # question-shaped ink drop above
    rect(g, 15, 4, 17, 5, CYAN)
    put(g, 18, 6, CYAN)
    put(g, 17, 7, CYAN)
    put(g, 16, 8, CYAN)
    put(g, 16, 10, CYAN)
    return g


ICONS = {
    "icon-base": sprite_base,
    "icon-reward": sprite_reward,
    "icon-rest": sprite_rest,
    "icon-pause": sprite_pause,
    "cur-inkcrystal": icon_crystal,
    "ear-blade": icon_blade,
    "ear-robe": icon_robe,
    "ear-eye": icon_eye,
    "ear-seal": icon_seal,
    "bag-gold": icon_pouch,
    "box-ink": icon_box,
}


# ---- writers -------------------------------------------------------------
def to_svg(g):
    out = ['<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" '
           'viewBox="0 0 32 32" shape-rendering="crispEdges">']
    for y in range(N):
        x = 0
        while x < N:
            c = g[y][x]
            if c is None:
                x += 1
                continue
            run = 1
            while x + run < N and g[y][x + run] == c:
                run += 1
            out.append(f'<rect x="{x}" y="{y}" width="{run}" height="1" '
                       f'fill="#{c[0]:02X}{c[1]:02X}{c[2]:02X}" '
                       f'fill-opacity="{c[3] / 255:.2f}"/>')
            x += run
    out.append("</svg>")
    return "\n".join(out)


def to_image(g, size=256):
    img = Image.new("RGBA", (N, N), (0, 0, 0, 0))
    px = img.load()
    for y in range(N):
        for x in range(N):
            if g[y][x] is not None:
                px[x, y] = g[y][x]
    return img.resize((size, size), Image.NEAREST)


def main():
    os.makedirs(SVG_DIR, exist_ok=True)
    os.makedirs(ICO_DIR, exist_ok=True)
    for name, fn in ICONS.items():
        g = fn()
        with open(os.path.join(SVG_DIR, name + ".svg"), "w", encoding="utf-8") as f:
            f.write(to_svg(g))
        img = to_image(g, 256)
        img.save(os.path.join(ICO_DIR, name + ".ico"),
                 sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64),
                        (128, 128), (256, 256)])
    print("assets:", len(ICONS), "icons ->", SVG_DIR, ICO_DIR)


if __name__ == "__main__":
    main()
