from __future__ import annotations

import argparse
from collections import deque
from pathlib import Path

from PIL import Image, ImageFilter


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Remove only chroma-key pixels connected to the canvas border."
    )
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--tolerance", type=int, default=180)
    parser.add_argument("--feather", type=float, default=0.45)
    parser.add_argument("--remove-isolated-key", action="store_true")
    return parser.parse_args()


def is_background(red: int, green: int, blue: int, tolerance: int) -> bool:
    key_distance = max(255 - red, 255 - green, blue)
    yellow_dominance = min(red, green) - blue
    red_green_delta = abs(red - green)
    return key_distance <= tolerance and yellow_dominance >= 24 and red_green_delta <= 92


def is_strong_isolated_key(red: int, green: int, blue: int) -> bool:
    yellow_floor = min(red, green)
    return (
        yellow_floor >= 48
        and blue <= yellow_floor * 0.58
        and abs(red - green) <= 96
    )


def main() -> None:
    args = parse_args()
    source_path = Path(args.input)
    output_path = Path(args.output)

    image = Image.open(source_path).convert("RGBA")
    width, height = image.size
    pixels = image.load()
    background = bytearray(width * height)
    queue: deque[tuple[int, int]] = deque()

    def enqueue(x: int, y: int) -> None:
        index = y * width + x
        if background[index]:
            return
        red, green, blue, _ = pixels[x, y]
        if not is_background(red, green, blue, args.tolerance):
            return
        background[index] = 1
        queue.append((x, y))

    for x in range(width):
        enqueue(x, 0)
        enqueue(x, height - 1)
    for y in range(height):
        enqueue(0, y)
        enqueue(width - 1, y)

    while queue:
        x, y = queue.popleft()
        if x > 0:
            enqueue(x - 1, y)
        if x + 1 < width:
            enqueue(x + 1, y)
        if y > 0:
            enqueue(x, y - 1)
        if y + 1 < height:
            enqueue(x, y + 1)

    if args.remove_isolated_key:
        for y in range(height):
            row = y * width
            for x in range(width):
                red, green, blue, _ = pixels[x, y]
                if is_strong_isolated_key(red, green, blue):
                    background[row + x] = 1

    alpha = Image.new("L", image.size, 255)
    alpha_pixels = alpha.load()
    for y in range(height):
        row = y * width
        for x in range(width):
            if background[row + x]:
                alpha_pixels[x, y] = 0

    if args.feather > 0:
        alpha = alpha.filter(ImageFilter.GaussianBlur(radius=args.feather))

    image.putalpha(alpha)
    output_pixels = image.load()
    for y in range(height):
        row = y * width
        for x in range(width):
            if background[row + x]:
                output_pixels[x, y] = (0, 0, 0, alpha_pixels[x, y])
    output_path.parent.mkdir(parents=True, exist_ok=True)
    image.save(output_path, "PNG")
    print(f"Wrote {output_path}")


if __name__ == "__main__":
    main()
