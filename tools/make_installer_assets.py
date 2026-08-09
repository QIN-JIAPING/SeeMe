# -*- coding: utf-8 -*-
"""
生成 SeeMe 安装向导位图（NSIS MUI2），视觉风格对齐 SeeMe 应用：
- 主题色 #6366F1（靛蓝）/ #818CF8，浅色底 #F1F5F9
- 圆角白卡片 + 微软雅黑字体 + 细分割线

产物:
  tools/installer-assets/welcome.bmp   164x314  (欢迎页/完成页左侧横幅)

用法: python make_installer_assets.py
依赖: Pillow
"""
import os
import re
from PIL import Image, ImageDraw, ImageFont

ACCENT = (99, 102, 241)      # #6366F1
ACCENT_DEEP = (79, 70, 229)  # #4F46E5
ACCENT_SOFT = (129, 140, 248)  # #818CF8
BG_LIGHT = (241, 245, 249)   # #F1F5F9
HEADING = (30, 27, 75)       # #1E1B4B
TEXT = (51, 65, 85)          # #334155
CARD_LOGO = (28, 31, 38)     # #1C1F26 (应用内欢迎卡 logo 底色)
WHITE = (255, 255, 255)

FONT_YAHEI = "C:/Windows/Fonts/msyh.ttc"
FONT_YAHEI_BOLD = "C:/Windows/Fonts/msyhbd.ttc"

# 应用图标（用户指定：使用真实 app_icon）
ICON_PNG = "D:/SeeMe/app_icon.png"

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.join(HERE, "installer-assets")


def app_version():
    """从 SeeMe.csproj 的 <Version> 读取应用版本（单一来源，避免硬编码）。"""
    try:
        with open(os.path.join(HERE, "..", "SeeMe.csproj"), encoding="utf-8") as f:
            m = re.search(r"<Version>([^<]+)</Version>", f.read())
        return m.group(1) if m else "1.0.1"
    except Exception:
        return "1.0.1"


def font(size, bold=False):
    path = FONT_YAHEI_BOLD if bold and os.path.exists(FONT_YAHEI_BOLD) else FONT_YAHEI
    return ImageFont.truetype(path, size, index=0)


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def vgradient(w, h, top, bottom):
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        c = lerp(top, bottom, y / max(1, h - 1))
        for x in range(w):
            px[x, y] = c
    return img


def fit_text(d, text, size, max_w, bold=True):
    """按最大宽度自动缩小字号，返回 (font, 实际宽度)。"""
    while size >= 8:
        f = font(size, bold=bold)
        w = d.textlength(text, font=f)
        if w <= max_w:
            return f, w
        size -= 1
    return font(8, bold=bold), d.textlength(text, font=font(8, bold=bold))


def rounded_icon(size, radius):
    """加载应用图标并按圆角蒙版裁剪。"""
    im = Image.open(ICON_PNG).convert("RGBA").resize((size, size), Image.LANCZOS)
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(im, (0, 0), mask)
    return out


def draw_welcome():
    W, H = 164, 314
    img = vgradient(W, H, ACCENT_SOFT, ACCENT_DEEP).convert("RGBA")

    # 装饰光晕（低透明度圆，叠加层合成，避免实心白圆）
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse([-56, -66, 118, 108], fill=(255, 255, 255, 26))
    gd.ellipse([98, 238, 196, 336], fill=(255, 255, 255, 20))
    img = Image.alpha_composite(img, glow)
    d = ImageDraw.Draw(img)

    # 品牌标题
    f_title = font(30, bold=True)
    d.text((W / 2, 38), "SeeMe", font=f_title, fill=WHITE, anchor="mm")

    # 细分割线
    d.line([(52, 64), (112, 64)], fill=(255, 255, 255, 150), width=2)

    # 副标题两行（自动适配宽度，避免被截断）
    f_sub, _ = fit_text(d, "Markdown · PDF · Office", 12, 124, bold=False)
    d.text((W / 2, 88), "Markdown · PDF · Office", font=f_sub, fill=WHITE, anchor="mm")
    f_sub2, _ = fit_text(d, "多格式文档查看器", 12, 124, bold=False)
    d.text((W / 2, 106), "多格式文档查看器", font=f_sub2, fill=WHITE, anchor="mm")

    # 圆角白卡片（对齐应用内欢迎卡）
    card = (22, 128, 142, 226)
    d.rounded_rectangle(card, radius=14, fill=WHITE)

    # 卡片内: 应用图标（用户指定的真实 app_icon）
    icon = rounded_icon(56, 12)
    img.paste(icon, (54, 140), icon)

    # 卡片内: 名称
    f_name = font(16, bold=True)
    d.text((W / 2, 212), "SeeMe", font=f_name, fill=HEADING, anchor="mm")

    # 特性行
    f_feat, _ = fit_text(d, "轻量 · 快速 · 离线可用", 12, 130, bold=False)
    d.text((W / 2, 262), "轻量 · 快速 · 离线可用", font=f_feat, fill=WHITE, anchor="mm")

    # 版本号（自动读取 SeeMe.csproj）
    f_ver = font(11)
    d.text((W / 2, 292), "v" + app_version(), font=f_ver, fill=WHITE, anchor="mm")

    return img.convert("RGB")


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    welcome = draw_welcome()
    welcome.save(os.path.join(OUT_DIR, "welcome.bmp"), "BMP")
    print("welcome.bmp", welcome.size)


if __name__ == "__main__":
    main()
