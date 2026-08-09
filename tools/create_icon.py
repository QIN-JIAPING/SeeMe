"""create_icon.py — 从源图生成 SeeMe 应用图标

用法:
    python create_icon.py <源图路径>

源图应为白色「W」在黑色背景上的截图。
脚本自动检测白色区域边界，裁剪并居中缩放至约 72% 占比，
输出 app_icon.ico（多尺寸: 16/32/48/64/128/256）和 app_icon.png（256×256）。

依赖: Pillow (pip install Pillow)
"""

import sys
import os
from PIL import Image

def detect_white_bbox(img, threshold=200):
    """检测白色像素的边界框"""
    gray = img.convert("L")
    w, h = gray.size
    pixels = gray.load()
    rows, cols = [], []
    for y in range(h):
        for x in range(w):
            if pixels[x, y] >= threshold:
                rows.append(y)
                cols.append(x)
    if not rows or not cols:
        return (0, 0, w, h)
    return (min(cols), min(rows), max(cols) + 1, max(rows) + 1)

def make_icon(source_path, output_dir):
    img = Image.open(source_path).convert("RGBA")
    w, h = img.size

    # 检测白色边界
    bbox = detect_white_bbox(img)
    left, top, right, bottom = bbox
    fw, fh = right - left, bottom - top

    # 正方形画布边长 = max, 留边距让 W 约占 72%
    target_ratio = 0.72
    canvas = int(max(fw, fh) / target_ratio)
    canvas = max(canvas, 16)

    # 裁剪内容区域
    content = img.crop(bbox)

    # 在正方形画布上居中
    final = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 255))
    ox = (canvas - fw) // 2
    oy = (canvas - fh) // 2
    final.paste(content, (ox, oy), content)

    # 输出多尺寸 ICO
    icon_sizes = [16, 32, 48, 64, 128, 256]
    ico_path = os.path.join(output_dir, "app_icon.ico")
    final_resized = [final.resize((s, s), Image.LANCZOS) for s in icon_sizes]
    final_resized[0].save(
        ico_path,
        format="ICO",
        sizes=[(s, s) for s in icon_sizes],
        append_images=final_resized[1:],
    )
    print(f"  -> {ico_path}")

    # 输出 256×256 PNG
    png_path = os.path.join(output_dir, "app_icon.png")
    final.resize((256, 256), Image.LANCZOS).save(png_path, format="PNG")
    print(f"  -> {png_path}")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("用法: python create_icon.py <源图路径>")
        sys.exit(1)

    src = sys.argv[1]
    if not os.path.isfile(src):
        print(f"错误: 找不到源图: {src}")
        sys.exit(1)

    out_dir = os.path.dirname(os.path.abspath(__file__))  # tools/
    proj_dir = os.path.dirname(out_dir)                   # 项目根目录

    print(f"源图: {src}")
    print(f"输出目录: {proj_dir}")
    make_icon(src, proj_dir)
    print("完成")
