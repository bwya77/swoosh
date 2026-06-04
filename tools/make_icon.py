"""Generate the Swoosh app icon: a soft light-blue screen with a deeper-blue
window floating over the left half (rounded-left, slight right curve, drop
shadow). Renders a multi-resolution .ico, the PNG set, and a preview sheet."""
import os
from PIL import Image, ImageDraw, ImageChops, ImageFilter, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(REPO, "Assets")
WS = r"C:\Users\bradleywyatt\OneDrive - Microsoft\Documents\Clawpilot"
os.makedirs(ASSETS, exist_ok=True)

S = 1024
BODY_T = (222, 236, 250, 255)   # light screen, top
BODY_B = (196, 218, 242, 255)   # light screen, bottom
BLUE_LT = (90, 178, 255, 255)   # floating window, lit
BLUE_DK = (10, 132, 255, 255)   # floating window, deep
NAVY = (8, 44, 100)             # floating-window shadow tint

def rmask(box, radius, size=S):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=radius, fill=255)
    return m

def left_rounded_mask(box, radius, size=S):
    """Rounded-left, straight-right mask (right corners squared by cropping a
    rounded rect that overhangs the right edge)."""
    L, T, R, B = box
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([L, T, R + radius * 2, B], radius=radius, fill=255)
    crop = Image.new("L", (size, size), 0)
    ImageDraw.Draw(crop).rectangle([L, T, R, B], fill=255)
    return ImageChops.multiply(m, crop)

def vgrad(size, top, bot):
    g = Image.new("RGBA", (size, size), (0, 0, 0, 0)); d = ImageDraw.Draw(g)
    for y in range(size):
        t = y / max(1, size - 1)
        d.line([(0, y), (size, y)], fill=tuple(int(top[k] + (bot[k] - top[k]) * t) for k in range(4)))
    return g

def dgrad(size, tl, br):
    g = Image.new("RGBA", (size, size), (0, 0, 0, 0)); px = g.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = tuple(int(tl[k] + (br[k] - tl[k]) * t) for k in range(4))
    return g

def clip(layer, mask):
    out = Image.new("RGBA", layer.size, (0, 0, 0, 0)); out.paste(layer, (0, 0), mask); return out

def colored_shadow(mask, off, blur, alpha, tint, size=S):
    a = mask.point(lambda v: int(v * alpha / 255))
    sh = Image.new("RGBA", (size, size), tint + (0,)); sh.putalpha(a)
    return ImageChops.offset(sh, off[0], off[1]).filter(ImageFilter.GaussianBlur(blur))

def render(size=S):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    # Larger: tighter outer margins so the screen fills more of the frame.
    mx, my = int(size * 0.082), int(size * 0.108)
    box = [mx, my, size - mx, size - my]; r = int(size * 0.165)
    mask = rmask(box, r, size)
    # outer drop shadow for whole icon
    img.alpha_composite(colored_shadow(mask, (0, int(size * 0.03)), int(size * 0.042), 105, (24, 66, 120), size))
    img.alpha_composite(clip(vgrad(size, BODY_T, BODY_B), mask))
    sheen = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(sheen).rounded_rectangle([box[0], box[1], box[2], box[1] + int(size * 0.30)], radius=r, fill=(255, 255, 255, 40))
    img.alpha_composite(clip(sheen, mask))

    inset = int(size * 0.055)
    midx = (box[0] + box[2]) // 2
    cbox = [box[0] + inset, box[1] + inset, midx, box[3] - inset]
    cr = int(size * 0.085)   # larger left radius
    sr = int(size * 0.024)   # slight right curve
    cardmask = ImageChops.multiply(left_rounded_mask(cbox, cr, size), rmask(cbox, sr, size))
    # Layered shadow (Fluent-style elevation): a soft ambient halo plus a tight
    # contact shadow, both clipped to the screen so they fall on the light body.
    for offx, offy, blur, alpha, tint in [
        (0.012, 0.030, 0.060, 120, NAVY),       # ambient
        (0.006, 0.010, 0.012, 205, (6, 34, 80)),  # contact
    ]:
        sh = colored_shadow(cardmask, (int(size * offx), int(size * offy)), int(size * blur), alpha, tint, size)
        img.alpha_composite(clip(sh, mask))
    img.alpha_composite(clip(dgrad(size, BLUE_LT, BLUE_DK), cardmask))
    return img

master = render()
master.save(os.path.join(ASSETS, "swoosh-1024.png"))
sizes = [256, 128, 64, 48, 32, 16]
for s in sizes:
    master.resize((s, s), Image.LANCZOS).save(os.path.join(ASSETS, f"swoosh-{s}.png"))
master.save(os.path.join(ASSETS, "swoosh.ico"), sizes=[(s, s) for s in sizes])
print("Wrote", os.path.join(ASSETS, "swoosh.ico"))

# preview sheet for review
def checker(w, h, c=16):
    bg = Image.new("RGBA", (w, h), (255, 255, 255, 255)); dr = ImageDraw.Draw(bg)
    for y in range(0, h, c):
        for x in range(0, w, c):
            if (x // c + y // c) % 2 == 0:
                dr.rectangle([x, y, x + c, y + c], fill=(216, 220, 226, 255))
    return bg
try:
    sheet = checker(760, 360)
    sheet.alpha_composite(master.resize((256, 256), Image.LANCZOS), (28, 52))
    def strip(bgc, y0):
        s = Image.new("RGBA", (380, 70), bgc); x = 16
        for sz in [16, 24, 32, 48]:
            s.alpha_composite(master.resize((sz, sz), Image.LANCZOS), (x, (70 - sz) // 2)); x += sz + 18
        sheet.alpha_composite(s, (320, y0))
    strip((38, 38, 44, 255), 80)
    strip((234, 236, 240, 255), 170)
    sheet.convert("RGB").save(os.path.join(WS, "swoosh-icon-final.png"))
    print("Wrote preview")
except Exception as e:
    print("preview skipped:", e)
