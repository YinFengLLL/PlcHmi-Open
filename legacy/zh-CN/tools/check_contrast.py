# -*- coding: utf-8 -*-
"""
PlcHmi 设计系统 · WCAG 对比度自检（双主题）
解析 Themes/Palette.Light.xaml 与 Themes/Palette.Dark.xaml 中的颜色 token，
按 WCAG 2.1 计算对比度。两主题键集合一致，各自跑一组贴合该主题用法的校验。

用法：
    python tools/check_contrast.py

退出码 0 = 全部达标；1 = 存在不达标组合。
门槛：正文文本 4.5:1，大号文本(>=18.66px bold 或 >=24px) 3:1，
      非文本 UI 组件/图形边界 3:1（WCAG 1.4.11）。

深浅两主题的令牌分工不同（深色：Primary 只做填充、PrimaryLight 做文字），
因此校验组按主题分别定义 —— 改色值前先看清该主题里这个键"压在哪、干什么"。
"""
import re
import sys
import os

THEMES_DIR = os.path.join(os.path.dirname(__file__), "..", "src", "PlcHmi.App", "Themes")


def load_colors(path):
    """从调色板抽取 <Color x:Key="X">#RRGGBB</Color> 与 SolidColorBrush 别名。"""
    with open(path, encoding="utf-8") as f:
        text = f.read()
    colors = {}
    for m in re.finditer(r'<Color x:Key="([^"]+)">(#[0-9A-Fa-f]{6,8})</Color>', text):
        colors[m.group(1)] = m.group(2)
    # SolidColorBrush 引用 Color 的：把 Brush 名映射到同一个色值
    for m in re.finditer(r'<SolidColorBrush x:Key="([^"]+)"\s+Color="\{StaticResource ([^}]+)\}"', text):
        if m.group(2) in colors:
            colors[m.group(1)] = colors[m.group(2)]
    return colors


def _srgb_to_linear(c):
    return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4


def relative_luminance(hexcolor):
    h = hexcolor.lstrip("#")
    if len(h) == 8:  # ARGB -> 丢弃 alpha（本检查仅针对不透明前景/背景）
        h = h[2:]
    r, g, b = (int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))
    return 0.2126 * _srgb_to_linear(r) + 0.7152 * _srgb_to_linear(g) + 0.0722 * _srgb_to_linear(b)


def contrast_ratio(fg, bg):
    l1, l2 = relative_luminance(fg), relative_luminance(bg)
    if l1 < l2:
        l1, l2 = l2, l1
    return (l1 + 0.05) / (l2 + 0.05)


# (说明, 前景 token, 背景 token, 要求门槛)
CHECKS_LIGHT = [
    ("正文文本 / 白卡",        "TextPrimaryColor",    "SurfaceColor",   4.5),
    ("正文文本 / 页面底",      "TextPrimaryColor",    "BackgroundColor", 4.5),
    ("次要文本 / 白卡",        "TextSecondaryColor",  "SurfaceColor",   4.5),
    ("次要文本 / 页面底",      "TextSecondaryColor",  "BackgroundColor", 4.5),
    ("次要文本 / 浅色卡",      "TextSecondaryColor",  "SurfaceAltColor", 4.5),
    ("白字 / 主蓝(顶栏/主按钮)", "TextOnPrimaryColor", "PrimaryColor",   4.5),
    ("浅蓝字(机种号小标题) / 主蓝", "OnPrimaryMutedColor", "PrimaryColor", 4.5),
    ("白字 / 浅蓝",            "TextOnPrimaryColor",  "PrimaryLightColor", 4.5),
    ("白字 / 深蓝",            "TextOnPrimaryColor",  "PrimaryDarkColor", 4.5),
    ("白字 / 成功(启动按钮)",  "TextOnPrimaryColor",  "SuccessColor",   4.5),
    ("白字 / 危险(急停)",      "TextOnPrimaryColor",  "DangerColor",    4.5),
    ("白字 / 警告深(停止按钮)", "TextOnPrimaryColor", "WarningStrongColor", 4.5),
    ("浅蓝字(次按钮) / 白",    "PrimaryLightColor",  "SurfaceColor",   4.5),
    ("主蓝字 / 白卡",          "PrimaryColor",       "SurfaceColor",   4.5),
    ("主蓝字 / 页面底",        "PrimaryColor",       "BackgroundColor", 4.5),
    ("主蓝字 / 浅蓝底(选中行)", "PrimaryColor",      "PrimarySoftColor", 4.5),
    ("浅蓝字 / 浅蓝底(导航选中项)", "PrimaryLightColor", "PrimarySoftColor", 4.5),
    ("次要文本 / 浅橙底(提示框)", "TextSecondaryColor", "WarningSoftColor", 4.5),
    ("成功色 / 白卡",          "SuccessColor",       "SurfaceColor",   3.0),
    ("危险色 / 白卡",          "DangerColor",        "SurfaceColor",   3.0),
    ("警告色 / 白卡(指示灯)",  "WarningColor",       "SurfaceColor",   3.0),
    ("次要色 / 白卡",          "MutedColor",         "SurfaceColor",   3.0),
    ("边框 / 白卡(组件边界)",  "BorderStrongColor",  "SurfaceColor",   3.0),
    ("点缀青 / 白卡(数据高亮)", "AccentColor",       "SurfaceColor",   3.0),

    # OnShell* 系列压在 PrimaryBrush 上的实测值。装饰线与强调只作图形用，
    # 门槛 3:1；拿它们写小字正文就会红 —— 这正是这条校验要拦的。
    ("深底正文 / 主蓝",        "OnShellPrimaryColor",   "PrimaryColor",  4.5),
    ("深底次要文字 / 主蓝",    "OnShellSecondaryColor", "PrimaryColor",  4.5),
    ("深底装饰线 / 主蓝",      "OnShellMutedColor",     "PrimaryColor",  3.0),
    ("深底强调色 / 主蓝",      "OnShellAccentColor",    "PrimaryColor",  3.0),
]

# 深色主题分工：Primary 只做"填充"（白字压其上），深色底上的主色文字
# 一律是 PrimaryLight。因此没有"主蓝字/页面底"，换成 PrimaryLight 的三条。
CHECKS_DARK = [
    ("正文文本 / 卡片",        "TextPrimaryColor",    "SurfaceColor",   4.5),
    ("正文文本 / 页面底",      "TextPrimaryColor",    "BackgroundColor", 4.5),
    ("次要文本 / 卡片",        "TextSecondaryColor",  "SurfaceColor",   4.5),
    ("次要文本 / 页面底",      "TextSecondaryColor",  "BackgroundColor", 4.5),
    ("次要文本 / 次级面板",    "TextSecondaryColor",  "SurfaceAltColor", 4.5),
    ("白字 / 主蓝(顶栏/主按钮)", "TextOnPrimaryColor", "PrimaryColor",   4.5),
    ("浅蓝字(机种号小标题) / 主蓝", "OnPrimaryMutedColor", "PrimaryColor", 4.5),
    ("白字 / 深蓝(按下态)",    "TextOnPrimaryColor",  "PrimaryDarkColor", 4.5),
    ("主蓝亮字(次按钮/链接) / 卡片", "PrimaryLightColor", "SurfaceColor", 4.5),
    ("主蓝亮字 / 页面底",      "PrimaryLightColor",   "BackgroundColor", 4.5),
    ("主蓝亮字 / 主蓝浅底(选中行)", "PrimaryLightColor", "PrimarySoftColor", 4.5),
    ("白字 / 成功(启动按钮)",  "TextOnPrimaryColor",  "SuccessColor",   4.5),
    ("白字 / 危险(急停)",      "TextOnPrimaryColor",  "DangerColor",    4.5),
    ("白字 / 警告深(停止按钮)", "TextOnPrimaryColor", "WarningStrongColor", 4.5),
    ("次要文本 / 警告浅底(提示框)", "TextSecondaryColor", "WarningSoftColor", 4.5),
    ("次要文本 / 危险浅底(异常横幅)", "TextSecondaryColor", "DangerSoftColor", 4.5),
    ("次要文本 / 成功浅底",    "TextSecondaryColor",  "SuccessSoftColor", 4.5),
    ("成功色 / 卡片",          "SuccessColor",       "SurfaceColor",   3.0),
    ("危险色 / 卡片",          "DangerColor",        "SurfaceColor",   3.0),
    ("警告色 / 卡片(指示灯)",  "WarningColor",       "SurfaceColor",   3.0),
    ("次要色 / 卡片",          "MutedColor",         "SurfaceColor",   3.0),
    ("边框 / 卡片(组件边界)",  "BorderStrongColor",  "SurfaceColor",   3.0),
    ("点缀青 / 卡片(数据高亮)", "AccentColor",       "SurfaceColor",   3.0),

    ("深底正文 / 主蓝",        "OnShellPrimaryColor",   "PrimaryColor",  4.5),
    ("深底次要文字 / 主蓝",    "OnShellSecondaryColor", "PrimaryColor",  4.5),
    ("深底装饰线 / 主蓝",      "OnShellMutedColor",     "PrimaryColor",  3.0),
    ("深底强调色 / 主蓝",      "OnShellAccentColor",    "PrimaryColor",  3.0),
]

THEMES = [
    ("浅色 Light", os.path.join(THEMES_DIR, "Palette.Light.xaml"), CHECKS_LIGHT),
    ("深色 Dark",  os.path.join(THEMES_DIR, "Palette.Dark.xaml"),  CHECKS_DARK),
]


def run_theme(name, path, checks):
    colors = load_colors(path)
    fails = []
    missing = []
    print(f"\n● {name}（{os.path.basename(path)}）")
    print("-" * 74)
    print(f"{'组合':<30}{'比值':>9}   {'门槛':>5}  结果")
    print("-" * 74)
    for label, fg_key, bg_key, threshold in checks:
        fg, bg = colors.get(fg_key), colors.get(bg_key)
        if not fg or not bg:
            missing.append((label, fg_key, bg_key))
            print(f"{label:<30}{'—':>9}   {'—':>5}  ⚠ token 缺失 ({fg_key} / {bg_key})")
            continue
        ratio = contrast_ratio(fg, bg)
        passed = ratio >= threshold
        if not passed:
            fails.append((label, ratio, threshold))
        print(f"{label:<30}{ratio:>8.2f}:1   {threshold:>5.1f}  {'PASS' if passed else 'FAIL'}")
    return fails, missing


def main():
    print("PlcHmi 设计系统 · WCAG 对比度自检（双主题）")
    print("=" * 74)

    all_fails = []
    all_missing = []
    for name, path, checks in THEMES:
        fails, missing = run_theme(name, path, checks)
        all_fails += [(name, *f) for f in fails]
        all_missing += [(name, *m) for m in missing]

    print("=" * 74)
    if all_missing:
        print(f"\n[FAIL] {len(all_missing)} 组 token 缺失（两主题键集合必须一致）：")
        for theme, label, fg_key, bg_key in all_missing:
            print(f"  - [{theme}] {label}: {fg_key} / {bg_key}")
    if all_fails:
        print(f"\n[FAIL] {len(all_fails)} 项未达标：")
        for theme, label, ratio, threshold in all_fails:
            print(f"  - [{theme}] {label}: {ratio:.2f}:1 < {threshold}")
    if all_missing or all_fails:
        return 1
    print("\n[PASS] 双主题全部组合达到 WCAG AA 门槛。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
