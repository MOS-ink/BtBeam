# -*- coding: utf-8 -*-
"""
BtBeam 图标生成（v2）

墨溟语言：墨底圆角方块 · 纸白设备 · 朱砂一抹束光
关键：小尺寸（≤24px）自动切换为极简版，只留「两点 + 一道弧」，
      否则在 16×16 下会糊成一团黑。
"""
import os
from PIL import Image, ImageDraw

OUT = r"D:\Projects\dsh_orb\lanbridge\BtBeam.ico"
PREVIEW = r"D:\Projects\dsh_orb\lanbridge\icon_preview.png"

INK   = (23, 23, 23, 255)
PAPER = (245, 242, 235, 255)
SEAL  = (170, 52, 41, 255)


def bezier_pts(p0, p1, p2, n=64):
    out = []
    for i in range(n + 1):
        u = i / n
        x = (1-u)**2*p0[0] + 2*(1-u)*u*p1[0] + u**2*p2[0]
        y = (1-u)**2*p0[1] + 2*(1-u)*u*p1[1] + u**2*p2[1]
        out.append((x, y))
    return out


def draw_icon(size):
    """按 size 直接绘制（不缩放），保证小尺寸锐利"""
    k = size / 1024.0
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 底：墨色圆角方块（圆角 22%）
    d.rounded_rectangle([0, 0, size, size], radius=int(size * 0.225), fill=INK)

    cx, cy = size / 2, size / 2
    simple = size <= 26          # 小尺寸走极简

    if simple:
        # ---- 极简：两个朱砂点 + 一道纸白弧 ----
        # 弧
        ax, bx = size * 0.26, size * 0.74
        top = cy - size * 0.19
        pts = bezier_pts((ax, cy + size * 0.10), (cx, top), (bx, cy + size * 0.10), 32)
        d.line(pts, fill=PAPER, width=max(2, int(size * 0.145)), joint="curve")

        # 两点
        rr = size * 0.098
        for px in (ax, bx):
            py = cy + size * 0.10
            d.ellipse([px - rr, py - rr, px + rr, py + rr], fill=SEAL)
    else:
        # ---- 完整：两台设备 + 朱砂拱弧 + 弧顶文件点 ----
        dev_w = size * 0.185
        dev_h = size * 0.360
        top_y = cy - dev_h / 2
        lx = size * 0.135
        rx = size * 0.680

        for x0 in (lx, rx):
            d.rounded_rectangle([x0, top_y, x0 + dev_w, top_y + dev_h],
                                radius=size * 0.030, fill=PAPER)
            # 两道刻线
            for i, fr in enumerate((0.26, 0.42)):
                lw = (dev_w - size * 0.062) * (1.0 if i == 0 else 0.66)
                ly = top_y + dev_h * fr
                d.rounded_rectangle(
                    [x0 + size * 0.031, ly, x0 + size * 0.031 + lw, ly + max(1, size * 0.021)],
                    radius=size * 0.010, fill=(23, 23, 23, 80))

        # 朱砂拱弧
        ax = lx + dev_w + size * 0.012
        bx = rx - size * 0.012
        arc_pts = bezier_pts((ax, cy), (cx, cy - size * 0.105), (bx, cy), 48)
        d.line(arc_pts, fill=SEAL, width=max(3, int(size * 0.052)), joint="curve")

        # 弧顶那颗"正在传的文件"
        mid = arc_pts[len(arc_pts) // 2]
        pr = size * 0.042
        d.ellipse([mid[0] - pr, mid[1] - pr, mid[0] + pr, mid[1] + pr], fill=PAPER)

    return img


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [draw_icon(s) for s in sizes]

    frames[-1].save(OUT, format="ICO", sizes=[(s, s) for s in sizes])
    print("ico ->", OUT, os.path.getsize(OUT), "bytes")

    # 预览：各尺寸并排 + 放大后的 16/32 单独一行
    pad = 22
    row_w = sum(s + pad for s in sizes) + pad
    prev = Image.new("RGBA", (row_w, 256 + pad * 3 + 120), (238, 236, 230, 255))
    x = pad
    for f in frames:
        prev.paste(f, (x, pad), f)
        x += f.width + pad

    # 下面一行：16 / 32 / 48 放大 6 倍，检查小尺寸
    y2 = 256 + pad * 2
    x = pad
    for s in (16, 32, 48):
        f = draw_icon(s).resize((s * 5, s * 5), Image.NEAREST)
        prev.paste(f, (x, y2), f)
        x += s * 5 + pad

    prev.save(PREVIEW)
    print("preview ->", PREVIEW, prev.size)


if __name__ == "__main__":
    main()
