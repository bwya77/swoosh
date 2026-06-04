"""Render several Swoosh icon variants and a labeled comparison sheet."""
import os
from PIL import Image, ImageDraw, ImageChops, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "tools", "variants")
os.makedirs(OUT, exist_ok=True)
WS = r"C:\Users\bradleywyatt\OneDrive - Microsoft\Documents\Clawpilot"

M = 512
BLUE = (10, 132, 255, 255)
BLUE_LT = (42, 147, 255, 255)
BLUE_DK = (10, 111, 224, 255)
DARK = (27, 30, 38, 255)
EDGE = (242, 244, 248, 235)
LIGHT_BODY = (238, 241, 245, 255)

def rmask(box, radius, size):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=radius, fill=255)
    return m

def screen_box(size):
    mx, my = int(size * 0.117), int(size * 0.185)
    return [mx, my, size - mx, size - my], int(size * 0.108)

def base_screen(size, body, edge):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    box, r = screen_box(size)
    sm = rmask(box, r, size)
    img.paste(Image.new("RGBA", (size, size), body), (0, 0), sm)
    return img, box, r, sm

def add_edge(img, box, r, size, color=EDGE):
    ew = max(1, int(size * 0.018))
    ImageDraw.Draw(img).rounded_rectangle(box, radius=r, outline=color, width=ew)

def zone(img, box, r, size, sm, rect, color=BLUE):
    z = Image.new("L", (size, size), 0)
    ImageDraw.Draw(z).rectangle(rect, fill=255)
    zm = ImageChops.multiply(sm, z)
    img.paste(Image.new("RGBA", (size, size), color), (0, 0), zm)

def v_left(size=M):
    img, box, r, sm = base_screen(size, DARK, EDGE)
    midx = (box[0] + box[2]) // 2
    zone(img, box, r, size, sm, [box[0], box[1], midx, box[3]])
    d = ImageDraw.Draw(img)
    d.line([(midx, box[1]+int(size*0.02)), (midx, box[3]-int(size*0.02))],
           fill=(255,255,255,60), width=max(1,int(size*0.006)))
    add_edge(img, box, r, size)
    return img

def v_quarter(size=M):
    img, box, r, sm = base_screen(size, DARK, EDGE)
    midx = (box[0] + box[2]) // 2
    midy = (box[1] + box[3]) // 2
    zone(img, box, r, size, sm, [box[0], box[1], midx, midy])
    add_edge(img, box, r, size)
    return img

def v_motion(size=M):
    img, box, r, sm = base_screen(size, DARK, EDGE)
    midx = (box[0] + box[2]) // 2
    zone(img, box, r, size, sm, [box[0], box[1], midx, box[3]])
    d = ImageDraw.Draw(img)
    # trailing chevrons in the dark half pointing left into the zone
    cy = (box[1] + box[3]) // 2
    h = int(size * 0.11)
    w = max(2, int(size * 0.028))
    for i, alpha in enumerate([230, 150, 80]):
        cx = midx + int(size * 0.10) + i * int(size * 0.085)
        col = (120, 190, 255, alpha)
        d.line([(cx, cy - h), (cx - int(size*0.05), cy)], fill=col, width=w)
        d.line([(cx - int(size*0.05), cy), (cx, cy + h)], fill=col, width=w)
    add_edge(img, box, r, size)
    return img

def v_tile(size=M):
    # Blue rounded app tile with a white screen + bright/faint split.
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    tm = int(size * 0.055)
    tbox = [tm, tm, size - tm, size - tm]
    tr = int(size * 0.20)
    # vertical gradient blue
    grad = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    for y in range(size):
        t = y / size
        c = tuple(int(BLUE_LT[k] + (BLUE_DK[k] - BLUE_LT[k]) * t) for k in range(3)) + (255,)
        gd.line([(0, y), (size, y)], fill=c)
    img.paste(grad, (0, 0), rmask(tbox, tr, size))
    # inner screen
    smx, smy = int(size * 0.255), int(size * 0.31)
    sbox = [smx, smy, size - smx, size - smy]
    sr = int(size * 0.07)
    sm = rmask(sbox, sr, size)
    midx = (sbox[0] + sbox[2]) // 2
    # right half faint white
    zone_rect_r = [midx, sbox[1], sbox[2], sbox[3]]
    z = Image.new("L", (size, size), 0); ImageDraw.Draw(z).rectangle(zone_rect_r, fill=255)
    img.paste(Image.new("RGBA",(size,size),(255,255,255,70)), (0,0), ImageChops.multiply(sm,z))
    # left half bright white
    zone_rect_l = [sbox[0], sbox[1], midx, sbox[3]]
    z2 = Image.new("L", (size, size), 0); ImageDraw.Draw(z2).rectangle(zone_rect_l, fill=255)
    img.paste(Image.new("RGBA",(size,size),(255,255,255,255)), (0,0), ImageChops.multiply(sm,z2))
    ImageDraw.Draw(img).rounded_rectangle(sbox, radius=sr, outline=(255,255,255,235),
                                          width=max(1,int(size*0.016)))
    return img

def v_light(size=M):
    img, box, r, sm = base_screen(size, LIGHT_BODY, DARK)
    midx = (box[0] + box[2]) // 2
    zone(img, box, r, size, sm, [box[0], box[1], midx, box[3]])
    add_edge(img, box, r, size, color=(27,30,38,235))
    return img

variants = [("A  Left-half", v_left), ("B  Quarter", v_quarter),
            ("C  Motion", v_motion), ("D  App tile", v_tile),
            ("E  Light screen", v_light)]

masters = {}
for name, fn in variants:
    im = fn()
    key = name.split()[0]
    masters[key] = im
    im.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, f"variant-{key}.png"))

# ---- comparison sheet ----
try:
    font = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 26)
    fonts = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 18)
except Exception:
    font = ImageFont.load_default(); fonts = font

cell_w, cell_h = 250, 300
cols = 3
rows = (len(variants) + cols - 1) // cols
W, H = cols * cell_w, rows * cell_h + 10
sheet = Image.new("RGBA", (W, H), (245, 246, 248, 255))
dr = ImageDraw.Draw(sheet)

for i, (name, fn) in enumerate(variants):
    key = name.split()[0]
    cx = (i % cols) * cell_w
    cy = (i // cols) * cell_h
    m = masters[key]
    hero = m.resize((150, 150), Image.LANCZOS)
    # checker behind hero so transparency is visible
    for yy in range(0, 150, 15):
        for xx in range(0, 150, 15):
            if (xx//15 + yy//15) % 2 == 0:
                dr.rectangle([cx+50+xx, cy+18+yy, cx+50+xx+15, cy+18+yy+15], fill=(220,224,230,255))
    sheet.alpha_composite(hero, (cx + 50, cy + 18))
    dr.text((cx + 50, cy + 175), name, font=font, fill=(20, 22, 28, 255))
    # mini sizes row on dark + light
    bx = cx + 30
    for j, bgc in enumerate([(40,40,46,255), (232,234,238,255)]):
        row = Image.new("RGBA", (190, 34), bgc)
        x = 8
        for s in [16, 24, 32]:
            row.alpha_composite(m.resize((s, s), Image.LANCZOS), (x, (34 - s)//2))
            x += s + 12
        sheet.alpha_composite(row, (bx, cy + 210 + j*38))

sheet.convert("RGB").save(os.path.join(WS, "swoosh-icon-variants.png"))
print("Wrote variants sheet")
