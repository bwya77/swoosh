"""Assemble the exported tutorial PNG frame sequences into looping README GIFs.

Each frame is composited onto a dark rounded "card" (matching the tutorial surface) with
padding, then the sequence is written as an optimized, infinitely-looping GIF.

Usage:
  python tools/make_tutorial_gifs.py <frames_dir> <out_dir>
"""
import sys
import os
from PIL import Image, ImageDraw

SURFACE = (26, 27, 32)      # tutorial dark surface (#1A1B20)
PAD = 26                    # padding around the demo content
RADIUS = 22                 # rounded card corners
TARGET_W = 560              # final GIF width (frames are rendered at 2x)
FPS = 25


def rounded_card(w, h, radius, color):
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, w - 1, h - 1], radius=radius, fill=color + (255,))
    return img


def build_gif(frames_dir, out_path):
    files = sorted(f for f in os.listdir(frames_dir) if f.lower().endswith(".png"))
    if not files:
        return False

    composed = []
    card = None
    for name in files:
        fr = Image.open(os.path.join(frames_dir, name)).convert("RGBA")
        if card is None:
            cw, ch = fr.width + PAD * 2, fr.height + PAD * 2
            card = rounded_card(cw, ch, RADIUS, SURFACE)
        base = card.copy()
        base.alpha_composite(fr, (PAD, PAD))
        # Flatten onto the surface color (GIF has no partial alpha).
        flat = Image.new("RGB", base.size, SURFACE)
        flat.paste(base, mask=base.split()[3])
        composed.append(flat)

    # Downscale for a reasonable README size.
    scale = TARGET_W / composed[0].width
    if scale < 1:
        size = (TARGET_W, round(composed[0].height * scale))
        composed = [im.resize(size, Image.LANCZOS) for im in composed]

    # Quantize each frame to a shared-ish adaptive palette for crisp gradients.
    pal_frames = [im.convert("P", palette=Image.ADAPTIVE, colors=128) for im in composed]

    delay = round(1000 / FPS)
    pal_frames[0].save(
        out_path,
        save_all=True,
        append_images=pal_frames[1:],
        duration=delay,
        loop=0,
        disposal=2,
        optimize=True,
    )
    kb = os.path.getsize(out_path) / 1024
    print(f"{os.path.basename(out_path)}: {len(pal_frames)} frames, {kb:.0f} KB")
    return True


def main():
    frames_dir = sys.argv[1]
    out_dir = sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)
    for name in sorted(os.listdir(frames_dir)):
        sub = os.path.join(frames_dir, name)
        if os.path.isdir(sub):
            build_gif(sub, os.path.join(out_dir, f"{name}.gif"))


if __name__ == "__main__":
    main()
