# -*- coding: utf-8 -*-
"""一键生成：SVG 矢量文件 + WPF XAML 几何资源 + HTML 总览预览页

    python tools/gen_all.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from iconlib import ROOT, write_svg, write_xaml, icon_key          # noqa: E402
import gen_preview                                                  # noqa: E402
import icons_nav, icons_status, icons_action, icons_device          # noqa: E402

SETS = [
    ("nav", icons_nav.ICONS),
    ("status", icons_status.ICONS),
    ("action", icons_action.ICONS),
    ("device", icons_device.ICONS),
]


def main():
    catalog = []
    print("PlcHmi 图标库 · 生成中")
    print("-" * 46)

    for cat, icons in SETS:
        n = write_svg(cat, icons)
        print("  icons/%-8s %2d 张  %s" % (cat + "/", n, " ".join(icons.keys())))
        for name, icon in icons.items():
            catalog.append((cat, name, icon))

    xaml_path = write_xaml(catalog, os.path.join(ROOT, "xaml", "PlcHmi.Icons.xaml"))
    rel_x = os.path.relpath(xaml_path, ROOT).replace("\\", "/")
    with open(xaml_path, encoding="utf-8") as f:
        ng = f.read().count("x:Key=") - 2          # 减去两个 Style 的 key
    print("  %-20s %2d 个图标 → %d 个 Geometry 资源" % (rel_x, len(catalog), ng))

    html_path = os.path.join(ROOT, "preview.html")
    with open(html_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(gen_preview.build(catalog))
    print("  preview.html        %d 个图标的总览页" % len(catalog))

    print("-" * 46)
    print("完成：%d 张图标。用浏览器打开 preview.html 查看总览。" % len(catalog))


if __name__ == "__main__":
    main()
