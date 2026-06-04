"""Generate the Swoosh app icon from the HUD motif: a rounded 'screen' with the
left half snapped/filled in the signature blue, a dark screen body, and a light
edge. Renders a multi-resolution .ico plus PNGs, and a preview sheet."""
import os
from PIL import Image, ImageDraw, ImageChops

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(REPO, "Assets")
os.makedirs(ASSETS, exist_ok=True)

S = 1024
BLUE = (10, 132, 255, 255)      # #0A84FF  signature accent
DARK = (27, 30, 38, 255)        # #1B1E26  screen body (HUD ScreenBg, opaque)
EDGE = (242, 244, 248, 235)     # near-white edge
DIVIDER = (255, 255, 255, 60)   # faint zone boundary

def rounded_mask(box, radius, size=S):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle(box, radius=radius, fill=255)
    return m

def render(size=S):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    # Screen rect: monitor-ish aspect, generous margins so nothing clips at 16px.
    mx, my = int(size * 0.117), int(size * 0.185)
    box = [mx, my, size - mx, size - my]
    radius = int(size * 0.108)
    midx = (box[0] + box[2]) // 2

    screen = rounded_mask(box, radius, size)

    # Dark screen body
    body = Image.new("RGBA", (size, size), DARK)
    img.paste(body, (0, 0), screen)

    # Left-half snapped zone in blue: screen shape intersected with left rectangle.
    left = Image.new("L", (size, size), 0)
    ImageDraw.Draw(left).rectangle([box[0], box[1], midx, box[3]], fill=255)
    leftzone = ImageChops.multiply(screen, left)
    blue = Image.new("RGBA", (size, size), BLUE)
    img.paste(blue, (0, 0), leftzone)

    d = ImageDraw.Draw(img)
    # Faint divider echoing the HUD zone boundary
    dw = max(1, int(size * 0.006))
    d.line([(midx, box[1] + int(size*0.02)), (midx, box[3] - int(size*0.02))],
           fill=DIVIDER, width=dw)
    # Light edge around the whole screen
    ew = max(1, int(size * 0.018))
    d.rounded_rectangle(box, radius=radius, outline=EDGE, width=ew)
    return img

master = render()
master.save(os.path.join(ASSETS, "swoosh-1024.png"))

sizes = [256, 128, 64, 48, 32, 16]
for s in sizes:
    master.resize((s, s), Image.LANCZOS).save(os.path.join(ASSETS, f"swoosh-{s}.png"))

# Multi-resolution ICO
master.save(os.path.join(ASSETS, "swoosh.ico"), sizes=[(s, s) for s in sizes])
print("Wrote", os.path.join(ASSETS, "swoosh.ico"))

# ---- Preview sheet into the Clawpilot workspace so it renders inline ----
WS = r"C:\Users\bradleywyatt\OneDrive - Microsoft\Documents\Clawpilot"
def checker(w, h, c=16):
    bg = Image.new("RGBA", (w, h), (255, 255, 255, 255))
    dr = ImageDraw.Draw(bg)
    for y in range(0, h, c):
        for x in range(0, w, c):
            if (x // c + y // c) % 2 == 0:
                dr.rectangle([x, y, x + c, y + c], fill=(214, 218, 224, 255))
    return bg

pw, ph = 760, 360
sheet = checker(pw, ph)
sheet.alpha_composite(master.resize((256, 256), Image.LANCZOS), (24, 52))

def strip(bg_color, y0):
    strip_img = Image.new("RGBA", (380, 64), bg_color)
    x = 16
    for s in [16, 24, 32, 48]:
        ic = master.resize((s, s), Image.LANCZOS)
        strip_img.alpha_composite(ic, (x, (64 - s) // 2))
        x += s + 18
    sheet.alpha_composite(strip_img, (336, y0))

strip((32, 32, 36, 255), 52)      # dark taskbar
strip((236, 238, 242, 255), 140)  # light taskbar
sheet.convert("RGB").save(os.path.join(WS, "swoosh-icon-preview.png"))
print("Wrote preview")
