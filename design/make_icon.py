import sys
from PIL import Image, ImageDraw

src, out_dir = sys.argv[1], sys.argv[2]
SIZE = 1024
SAFE = 0.66  # Android adaptive icon safe zone (66 of 108 dp)

img = Image.open(src).convert("RGBA")
w, h = img.size
print("source", img.size, "corner", img.getpixel((0, 0)), "edge-mid", img.getpixel((2, h // 2)))

# Make the area outside the rounded square transparent by flood-filling
# near-white / transparent pixels from the four corners.
corner = img.getpixel((0, 0))
if corner[3] > 0 and sum(corner[:3]) > 600:  # opaque, near white
    for pt in [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)]:
        ImageDraw.floodfill(img, pt, (0, 0, 0, 0), thresh=60)

# Trim transparent border so the artwork fills its own box.
bbox = img.getbbox()
art = img.crop(bbox)
print("artwork box", bbox, "size", art.size)

# Background blue sampled a little inside the artwork's left edge.
aw, ah = art.size
bg = art.getpixel((int(aw * 0.03), ah // 2))[:3]
print("bg color #%02X%02X%02X" % bg)

# Square the artwork (it should already be square).
side = max(aw, ah)
sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
sq.paste(art, ((side - aw) // 2, (side - ah) // 2), art)

# 1) Foreground layer: artwork scaled to the safe zone, centered on a transparent canvas.
fg = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
s = int(SIZE * SAFE)
scaled = sq.resize((s, s), Image.LANCZOS)
fg.paste(scaled, ((SIZE - s) // 2, (SIZE - s) // 2), scaled)
fg.save(f"{out_dir}/appiconfg.png")

# 2) Background layer: solid blue.
Image.new("RGBA", (SIZE, SIZE), bg + (255,)).save(f"{out_dir}/appicon.png")

# 3) Splash artwork: the rounded square itself on transparent, used on a blue splash.
sq.resize((SIZE, SIZE), Image.LANCZOS).save(f"{out_dir}/splash.png")

# 4) Preview of what a circle-masked launcher shows (center 72/108 crop, circular mask).
comp = Image.new("RGBA", (SIZE, SIZE), bg + (255,))
comp.paste(fg, (0, 0), fg)
crop = int(SIZE * 72 / 108)
off = (SIZE - crop) // 2
view = comp.crop((off, off, off + crop, off + crop))
mask = Image.new("L", view.size, 0)
ImageDraw.Draw(mask).ellipse((0, 0, crop - 1, crop - 1), fill=255)
prev = Image.new("RGBA", view.size, (255, 255, 255, 255))
prev.paste(view, (0, 0), mask)
prev.save(f"{out_dir}/preview_circle.png")
print("done", "#%02X%02X%02X" % bg)
