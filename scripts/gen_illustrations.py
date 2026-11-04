"""Tutorial illustration SVGs (dark, pixel-flavoured, static)."""
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets", "svg")
os.makedirs(OUT, exist_ok=True)

BG = "#0d1117"
INK = "#161d26"
INK2 = "#1e2836"
CYAN = "#4fd6e0"
CYAN_D = "#2a8c96"
CRIM = "#c2453f"
CRIM_D = "#6e2220"
GOLD = "#ffd166"
AMB = "#f2a63b"


def head(w, h, extra=""):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" '
            f'viewBox="0 0 {w} {h}" font-family="Segoe UI, Microsoft YaHei UI, sans-serif">'
            f'<defs>{extra}</defs>'
            f'<rect width="{w}" height="{h}" fill="{BG}"/>')


def hero():
    s = [head(800, 400, f'''
    <linearGradient id="g1" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#0d1117"/><stop offset="0.55" stop-color="#101821"/>
      <stop offset="1" stop-color="#2a1414"/>
    </linearGradient>
    <radialGradient id="g2" cx="0.5" cy="1" r="0.8">
      <stop offset="0" stop-color="{CRIM}" stop-opacity="0.55"/>
      <stop offset="1" stop-color="{CRIM}" stop-opacity="0"/>
    </radialGradient>'''),
         '<rect width="800" height="400" fill="url(#g1)"/>',
         '<rect y="250" width="800" height="150" fill="url(#g2)"/>']

    # abyss tower: stacked blocks going down, narrowing
    cx = 400
    y = 60
    widths = [150, 140, 132, 122, 112, 104, 96, 88, 80, 72, 64, 56, 48, 40, 32]
    for i, w in enumerate(widths):
        h = 16
        s.append(f'<rect x="{cx - w // 2}" y="{y}" width="{w}" height="{h}" fill="{INK}" stroke="#243040"/>')
        # inner steps
        s.append(f'<rect x="{cx - w // 2}" y="{y + h - 4}" width="{w}" height="4" fill="{INK2}"/>')
        # drips
        if i % 3 == 1:
            s.append(f'<rect x="{cx - w // 2 + 6}" y="{y + h}" width="3" height="{8 + i}" fill="{INK2}"/>')
            s.append(f'<rect x="{cx + w // 2 - 9}" y="{y + h}" width="3" height="{5 + i}" fill="{INK2}"/>')
        y += h + 2

    # crimson glow deep inside
    s.append(f'<ellipse cx="400" cy="345" rx="70" ry="14" fill="{CRIM}" opacity="0.35"/>')
    s.append(f'<ellipse cx="400" cy="345" rx="34" ry="7" fill="{CRIM}" opacity="0.75"/>')

    # spirit on top of the tower
    s.append(f'<g transform="translate(400,46)">')
    for i, hw in enumerate([2, 4, 5, 7, 8, 9, 10, 10, 10, 9, 8]):
        yy = -18 + i * 3
        s.append(f'<rect x="{-hw}" y="{yy}" width="{hw * 2}" height="3" fill="{INK2}" stroke="#2b4a58" stroke-width="1"/>')
    s.append(f'<rect x="-6" y="-6" width="4" height="5" fill="{CYAN}"/>')
    s.append(f'<rect x="3" y="-6" width="4" height="5" fill="{CYAN}"/>')
    s.append('</g>')
    s.append(f'<ellipse cx="400" cy="34" rx="46" ry="16" fill="{CYAN}" opacity="0.10"/>')

    # labels (english only)
    s.append(f'<text x="24" y="34" fill="{CYAN}" font-size="12" letter-spacing="3">MOYUAN TOWER</text>')
    s.append(f'<text x="24" y="54" fill="#7d8b96" font-size="11">10,000 layers · the deeper, the thicker</text>')
    s.append(f'<text x="{cx + 90}" y="70" fill="#7d8b96" font-size="11">you start here</text>')
    s.append(f'<line x1="{cx + 84}" y1="66" x2="{cx + 30}" y2="58" stroke="#3c4a56"/>')
    s.append(f'<text x="{cx + 90}" y="352" fill="{CRIM}" font-size="11">layer 10,000 · boss</text>')
    s.append(f'<line x1="{cx + 84}" y1="348" x2="{cx + 46}" y2="344" stroke="#5c2a28"/>')
    s.append('</svg>')
    return "\n".join(s)


def boss():
    s = [head(800, 400, f'''
    <radialGradient id="core" cx="0.5" cy="0.5" r="0.5">
      <stop offset="0" stop-color="#ff8a72"/><stop offset="0.4" stop-color="{CRIM}"/>
      <stop offset="1" stop-color="{CRIM_D}" stop-opacity="0"/>
    </radialGradient>'''),
         '<rect width="800" height="400" fill="#0b0e13"/>']

    cx, cy = 400, 190
    # vortex rings
    for i, r in enumerate(range(40, 300, 26)):
        op = 0.55 - i * 0.03
        if op <= 0.06:
            break
        col = CRIM_D if i % 2 else "#38262a"
        s.append(f'<ellipse cx="{cx}" cy="{cy}" rx="{r + 30}" ry="{int((r + 30) * 0.42)}" '
                 f'fill="none" stroke="{col}" stroke-width="2" opacity="{op:.2f}"/>')

    # tendrils
    import math
    for i in range(14):
        a = math.radians(i * 360 / 14 + 8)
        x2 = cx + int(math.cos(a) * 320)
        y2 = cy + int(math.sin(a) * 150)
        mx = cx + int(math.cos(a) * 140)
        my = cy + int(math.sin(a) * 60)
        s.append(f'<path d="M{cx},{cy} Q{mx},{my} {x2},{y2}" fill="none" stroke="{INK2}" stroke-width="7" opacity="0.85"/>')
        s.append(f'<path d="M{cx},{cy} Q{mx},{my} {x2},{y2}" fill="none" stroke="#2b3a49" stroke-width="2" opacity="0.5"/>')

    # core
    s.append(f'<ellipse cx="{cx}" cy="{cy}" rx="120" ry="100" fill="url(#core)"/>')
    s.append(f'<ellipse cx="{cx}" cy="{cy}" rx="34" ry="30" fill="#ff6b5a" opacity="0.9"/>')
    s.append(f'<ellipse cx="{cx}" cy="{cy}" rx="14" ry="26" fill="#2b0f0e"/>')

    # tiny spirit silhouette bottom
    s.append(f'<g transform="translate(400,330)">')
    for i, hw in enumerate([1, 2, 3, 4, 4, 5, 5, 5, 4, 3]):
        s.append(f'<rect x="{-hw}" y="{-16 + i * 3}" width="{hw * 2}" height="3" fill="#0f141a"/>')
    s.append(f'<rect x="-3" y="-7" width="2" height="2" fill="{CYAN}"/>')
    s.append(f'<rect x="2" y="-7" width="2" height="2" fill="{CYAN}"/>')
    s.append('</g>')

    s.append(f'<text x="24" y="34" fill="{CRIM}" font-size="12" letter-spacing="3">MO YUAN</text>')
    s.append(f'<text x="24" y="54" fill="#7d8b96" font-size="11">HP 1.8e+303 · ATK 1.8e+303 · DEF 1.8e+300</text>')
    s.append(f'<text x="620" y="378" fill="#7d8b96" font-size="11">you, for scale</text>')
    s.append('</svg>')
    return "\n".join(s)


def timeline():
    s = [head(800, 220), '<rect width="800" height="220" fill="#0f1318"/>']
    steps = [
        ("keys", "ten keys", "100 taps → 1 bag"),
        ("bag", "a pouch drops", "claim it by right-click"),
        ("shop", "buy ink", "25 gold per shard"),
        ("blade", "feed the blade", "+1 level, +6% atk"),
    ]
    x0 = 76
    step = 190
    for i, (kind, t1, t2) in enumerate(steps):
        cx = x0 + i * step
        s.append(f'<rect x="{cx - 62}" y="58" width="124" height="92" rx="8" fill="{INK}" stroke="#243040"/>')
        if kind == "keys":
            for k in range(3):
                s.append(f'<rect x="{cx - 42 + k * 28}" y="{88}" width="22" height="16" rx="3" fill="{INK2}" stroke="#3a4a58"/>')
            s.append(f'<rect x="{cx - 42}" y="116" width="78" height="14" rx="3" fill="#141a22" stroke="#3a4a58"/>')
            s.append(f'<text x="{cx}" y="126" fill="{CYAN}" font-size="9" text-anchor="middle">tap tap</text>')
        elif kind == "bag":
            s.append(f'<path d="M{cx - 16},{86} h32 l6,10 v22 q0,6 -6,6 h-32 q-6,0 -6,-6 v-22 z" fill="#3a3028" stroke="{AMB}"/>')
            s.append(f'<rect x="{cx - 8}" y="96" width="16" height="10" fill="{GOLD}"/>')
            s.append(f'<rect x="{cx - 10}" y="82" width="20" height="5" fill="{AMB}"/>')
        elif kind == "shop":
            s.append(f'<path d="M{cx},{86} l12,8 v10 l-12,10 -12,-10 v-10 z" fill="{CYAN_D}" stroke="{CYAN}"/>')
            s.append(f'<rect x="{cx - 1}" y="92" width="3" height="16" fill="{CYAN}"/>')
            s.append(f'<text x="{cx}" y="{124}" fill="#7d8b96" font-size="9" text-anchor="middle">25 g</text>')
        else:
            s.append(f'<rect x="{cx - 3}" y="80" width="6" height="34" fill="{INK2}" stroke="{CYAN}"/>')
            s.append(f'<rect x="{cx - 16}" y="114" width="32" height="5" fill="#3a4a58"/>')
            s.append(f'<rect x="{cx - 4}" y="119" width="8" height="14" fill="{INK2}"/>')
            s.append(f'<rect x="{cx - 7}" y="133" width="14" height="4" fill="{GOLD}"/>')
        s.append(f'<text x="{cx}" y="172" fill="{CYAN}" font-size="12" text-anchor="middle">{t1}</text>')
        s.append(f'<text x="{cx}" y="190" fill="#7d8b96" font-size="10" text-anchor="middle">{t2}</text>')
        if i < 3:
            ax = cx + 66
            s.append(f'<path d="M{ax},104 h44" stroke="#3c4a56" stroke-width="2"/>')
            s.append(f'<path d="M{ax + 44},104 l-8,-4 v8 z" fill="#3c4a56"/>')
    s.append('</svg>')
    return "\n".join(s)


def rebirth():
    s = [head(800, 400, f'''
    <linearGradient id="fade" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#1e2836" stop-opacity="1"/>
      <stop offset="1" stop-color="#1e2836" stop-opacity="0"/>
    </linearGradient>'''),
         '<rect width="800" height="400" fill="#0f1318"/>',
         '<line x1="400" y1="0" x2="400" y2="400" stroke="#1d2530"/>']

    # left: dissolving spirit
    s.append('<g transform="translate(220,150)">')
    for i, hw in enumerate([4, 7, 9, 12, 14, 15, 15, 15, 14, 12, 9]):
        s.append(f'<rect x="{-hw}" y="{-30 + i * 9}" width="{hw * 2}" height="9" fill="url(#fade)" stroke="#2b4a58"/>')
    s.append(f'<rect x="-8" y="-4" width="6" height="7" fill="{CYAN}"/>')
    s.append(f'<rect x="3" y="-4" width="6" height="7" fill="{CYAN}"/>')
    s.append('</g>')
    for i in range(9):
        x = 190 + i * 12 + (i % 3) * 4
        y = 250 + (i % 4) * 24
        s.append(f'<rect x="{x}" y="{y}" width="5" height="7" fill="{CYAN_D}" opacity="{0.75 - i * 0.06:.2f}"/>')

    # right: roots / sprouts
    s.append('<g transform="translate(580,180)">')
    s.append(f'<rect x="-30" y="40" width="60" height="4" fill="#243040"/>')
    for i in range(5):
        x = -24 + i * 12
        s.append(f'<path d="M{x},40 v-24" stroke="{CYAN_D}" stroke-width="2"/>')
        s.append(f'<ellipse cx="{x}" cy="14" rx="5" ry="7" fill="{CYAN}" opacity="0.9"/>')
    for i in range(4):
        s.append(f'<path d="M{-20 + i * 13},44 q-6,20 4,34" stroke="#2b3a49" stroke-width="2" fill="none"/>')
    s.append('</g>')

    s.append(f'<text x="220" y="330" fill="#7d8b96" font-size="11" text-anchor="middle">this cycle resets</text>')
    s.append(f'<text x="580" y="330" fill="{CYAN}" font-size="11" text-anchor="middle">roots stay forever</text>')
    s.append(f'<text x="400" y="52" fill="{CYAN}" font-size="12" letter-spacing="3" text-anchor="middle">REBIRTH</text>')
    s.append('</svg>')
    return "\n".join(s)


def stats():
    s = [head(800, 400), '<rect width="800" height="400" fill="#0f1318"/>']
    s.append(f'<rect x="90" y="60" width="620" height="280" rx="8" fill="#141a22" stroke="#243040"/>')
    s.append(f'<rect x="90" y="60" width="620" height="34" rx="8" fill="#1a232e"/>')
    s.append(f'<text x="112" y="83" fill="{CYAN}" font-size="12" letter-spacing="2">STATISTICS</text>')
    vals = [0.18, 0.42, 0.33, 0.66, 0.55, 0.82, 0.74, 0.95]
    for i, v in enumerate(vals):
        x = 130 + i * 66
        h = int(v * 150)
        s.append(f'<rect x="{x}" y="{290 - h}" width="34" height="{h}" fill="{CYAN_D}"/>')
        s.append(f'<rect x="{x}" y="{290 - h}" width="34" height="4" fill="{CYAN}"/>')
    s.append(f'<line x1="120" y1="292" x2="680" y2="292" stroke="#2b3a49"/>')
    s.append('</svg>')
    return "\n".join(s)


FILES = {
    "tut-hero": hero,
    "tut-boss": boss,
    "tut-timeline": timeline,
    "opt-rebirth": rebirth,
    "opt-stats": stats,
}

for name, fn in FILES.items():
    with open(os.path.join(OUT, name + ".svg"), "w", encoding="utf-8") as f:
        f.write(fn())
print("illustrations:", len(FILES), "->", OUT)
