"""Draws the WhereEQ icon (a gold map pin on a dark rounded square) and writes src/WhereEQ/Assets/WhereEQ.ico.

    python tools/make_icon.py

Drawn at 1024 px and scaled down, so every size (16..256) is smooth. Needs Pillow.
"""
import os
from PIL import Image, ImageDraw

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Background: dark blue-slate rounded square with a subtle lighter rim.
pad, radius = 40, 210
d.rounded_rectangle([pad, pad, S - pad, S - pad], radius=radius, fill=(27, 42, 65, 255))
d.rounded_rectangle([pad, pad, S - pad, S - pad], radius=radius, outline=(70, 96, 130, 255), width=22)

# Faint map grid lines.
for i in range(1, 4):
    x = pad + i * (S - 2 * pad) // 4
    d.line([x, pad + 60, x, S - pad - 60], fill=(48, 68, 96, 255), width=10)
    d.line([pad + 60, x, S - pad - 60, x], fill=(48, 68, 96, 255), width=10)

# Map pin: circle head + point, gold, with a dark hole.
cx, cy, r = S // 2, 420, 230
tip_y = 870
gold, gold_dark = (242, 190, 60, 255), (196, 140, 28, 255)
d.polygon([(cx - r * 0.86, cy + r * 0.5), (cx + r * 0.86, cy + r * 0.5), (cx, tip_y)], fill=gold)
d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=gold)
# Shade the right half of the point a little for depth.
d.polygon([(cx, cy + r * 0.6), (cx + r * 0.86, cy + r * 0.5), (cx, tip_y)], fill=gold_dark)
hole = 95
d.ellipse([cx - hole, cy - hole, cx + hole, cy + hole], fill=(27, 42, 65, 255))

out = os.path.join(os.path.dirname(__file__), "..", "src", "WhereEQ", "Assets")
os.makedirs(out, exist_ok=True)
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
img.save(os.path.join(out, "WhereEQ.ico"), sizes=[(s, s) for s in sizes])
img.resize((256, 256), Image.LANCZOS).save(os.path.join(out, "WhereEQ-256.png"))
print("wrote", os.path.normpath(os.path.join(out, "WhereEQ.ico")))
