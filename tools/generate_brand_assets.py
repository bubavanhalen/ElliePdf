from __future__ import annotations

import struct
import sys
import zlib
from collections import deque
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Assets" / "Brand" / "elliepdf-logo-master.png"
ASSETS = ROOT / "Assets"
ICO = ASSETS / "AppIcon.ico"
RECOVERY = Path(r"C:\Users\phili\.cursor\projects\e-source-bubavanhalen-ElliePdf\assets\elliepdf-logo-master.png")


def is_peach(r: int, g: int, b: int) -> bool:
    return 195 <= r <= 238 and 150 <= g <= 198 and 120 <= b <= 178 and (r - b) >= 35


def is_removable(r: int, g: int, b: int, a: int) -> bool:
    if a <= 10 or is_peach(r, g, b):
        return False
    bright_neutral = r >= 235 and g >= 232 and b >= 225
    cream_tile = r >= 225 and g >= 215 and b >= 200 and (r - b) <= 45 and (r - g) <= 30
    return bright_neutral or cream_tile


def similar(r1: int, g1: int, b1: int, r2: int, g2: int, b2: int, tolerance: int = 24) -> bool:
    return abs(r1 - r2) <= tolerance and abs(g1 - g2) <= tolerance and abs(b1 - b2) <= tolerance


def remove_background(image: Image.Image) -> Image.Image:
    rgba = image.convert("RGBA")
    width, height = rgba.size
    pixels = rgba.load()
    removed = bytearray(width * height)
    queue: deque[tuple[int, int]] = deque()

    def enqueue(x: int, y: int) -> None:
        if x < 0 or y < 0 or x >= width or y >= height:
            return
        index = y * width + x
        if removed[index]:
            return
        r, g, b, a = pixels[x, y]
        if not is_removable(r, g, b, a):
            return
        removed[index] = 1
        queue.append((x, y))

    for x in range(width):
        enqueue(x, 0)
        enqueue(x, height - 1)
    for y in range(height):
        enqueue(0, y)
        enqueue(width - 1, y)

    while queue:
        x, y = queue.popleft()
        seed_r, seed_g, seed_b, _ = pixels[x, y]
        for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if nx < 0 or ny < 0 or nx >= width or ny >= height:
                continue
            index = ny * width + nx
            if removed[index]:
                continue
            r, g, b, a = pixels[nx, ny]
            if not is_removable(r, g, b, a) or not similar(seed_r, seed_g, seed_b, r, g, b):
                continue
            removed[index] = 1
            queue.append((nx, ny))

    for y in range(height):
        for x in range(width):
            if removed[y * width + x]:
                pixels[x, y] = (0, 0, 0, 0)

    return rgba


def crop_to_content(image: Image.Image) -> Image.Image:
    rgba = image.convert("RGBA")
    bbox = rgba.getbbox()
    return rgba.crop(bbox) if bbox else rgba


def render_square(source: Image.Image, size: int, padding_ratio: float = 0.03) -> Image.Image:
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    padding = int(size * padding_ratio)
    draw_size = size - (2 * padding)
    resized = source.resize((draw_size, draw_size), Image.Resampling.LANCZOS)
    canvas.paste(resized, (padding, padding), resized)
    return canvas


def render_wide(source: Image.Image, width: int, height: int, padding_ratio: float = 0.05) -> Image.Image:
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    draw_size = int(min(width, height) * (1 - (2 * padding_ratio)))
    resized = source.resize((draw_size, draw_size), Image.Resampling.LANCZOS)
    offset_x = (width - draw_size) // 2
    offset_y = (height - draw_size) // 2
    canvas.paste(resized, (offset_x, offset_y), resized)
    return canvas


def save_ico(path: Path, source: Image.Image, sizes: list[int]) -> None:
    images = [
        render_square(source, size, padding_ratio=0.01 if size <= 48 else 0.02)
        for size in sizes
    ]
    entries = []
    offset = 6 + 16 * len(images)

    for image in images:
        width, height = image.size
        rgba = image.tobytes("raw", "BGRA")
        row_bytes = width * 4
        and_mask = ((width + 31) // 32) * 4
        bmp = bytearray()
        for y in range(height - 1, -1, -1):
            start = y * row_bytes
            bmp.extend(rgba[start : start + row_bytes])
            bmp.extend(b"\x00" * and_mask)

        dib = struct.pack(
            "<IIIHHIIIIII",
            40,
            width,
            height * 2,
            1,
            32,
            0,
            len(bmp),
            0,
            0,
            0,
            0,
        ) + bmp
        entries.append((width, height, dib, offset))
        offset += len(dib)

    ico = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    for width, height, dib, image_offset in entries:
        ico.extend(
            struct.pack(
                "<BBBBHHII",
                0 if width >= 256 else width,
                0 if height >= 256 else height,
                0,
                0,
                1,
                32,
                len(dib),
                image_offset,
            )
        )
    for _, _, dib, _ in entries:
        ico.extend(dib)
    path.write_bytes(bytes(ico))


def main() -> int:
    if SOURCE.exists():
        source_path = SOURCE
        image = Image.open(source_path).convert("RGBA")
    elif RECOVERY.exists():
        source_path = RECOVERY
        image = remove_background(Image.open(source_path))
    else:
        raise FileNotFoundError("No logo source image found.")

    SOURCE.parent.mkdir(parents=True, exist_ok=True)
    image.save(SOURCE, format="PNG")
    logo = crop_to_content(image)

    render_square(logo, 50, padding_ratio=0.02).save(ASSETS / "StoreLogo.png", format="PNG")
    render_square(logo, 300, padding_ratio=0.04).save(ASSETS / "Square150x150Logo.scale-200.png", format="PNG")
    render_square(logo, 88, padding_ratio=0.02).save(ASSETS / "Square44x44Logo.scale-200.png", format="PNG")
    render_square(logo, 24, padding_ratio=0.01).save(
        ASSETS / "Square44x44Logo.targetsize-24_altform-unplated.png", format="PNG"
    )
    render_square(logo, 48, padding_ratio=0.01).save(
        ASSETS / "Square44x44Logo.targetsize-48_altform-lightunplated.png", format="PNG"
    )
    render_square(logo, 96, padding_ratio=0.02).save(ASSETS / "LockScreenLogo.scale-200.png", format="PNG")
    render_wide(logo, 620, 300).save(ASSETS / "Wide310x150Logo.scale-200.png", format="PNG")
    render_wide(logo, 620, 300).save(ASSETS / "SplashScreen.scale-200.png", format="PNG")
    save_ico(ICO, logo, [16, 24, 32, 48, 64, 128, 256])
    print(f"Updated transparent brand assets from {source_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
