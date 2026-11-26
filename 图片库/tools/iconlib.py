# -*- coding: utf-8 -*-
"""工控上位机图标库 · 公共渲染模块

图标以「图元列表」描述，一份数据同时产出三种格式：
  icons/<cat>/<name>.svg     矢量源文件（stroke/fill 用 currentColor）
  xaml/PlcHmi.Icons.xaml     WPF 可直接引用的 Geometry 资源
  preview.html               HTML 总览预览页

配色严格对齐 PlcHmi.App/Themes/Palette.Base|Light|Dark.xaml 令牌体系。
"""
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir))

PALETTE = {
    "primary":   "#0B3D91",
    "primaryDk": "#082C6B",
    "primaryLt": "#1565C0",
    "soft":      "#EEF3FA",
    "success":   "#1B8A4B",
    "danger":    "#C62828",
    "warning":   "#E8890C",
    "muted":     "#8A95A5",
    "off":       "#D8E0EA",
    "text":      "#1A2433",
    "text2":     "#5A6B82",
    "bg":        "#F4F7FB",
    "surface":   "#FFFFFF",
    "border":    "#E1E8F0",
}

CAT_LABEL = {
    "nav":    "导航与功能",
    "status": "状态与报警灯",
    "action": "操作按钮",
    "device": "设备工艺图形",
}
CAT_ORDER = ["nav", "status", "action", "device"]

FILL_KINDS = ("dot", "frect", "fpath")


# ── 图元 → SVG ────────────────────────────────────────────────
def svg_el(p):
    k = p[0]
    if k == "circle":
        return '<circle cx="%s" cy="%s" r="%s"/>' % p[1:4]
    if k == "dot":
        return '<circle cx="%s" cy="%s" r="%s" fill="currentColor" stroke="none"/>' % p[1:4]
    if k == "line":
        return '<line x1="%s" y1="%s" x2="%s" y2="%s"/>' % p[1:5]
    if k == "rect":
        return '<rect x="%s" y="%s" width="%s" height="%s" rx="%s"/>' % p[1:6]
    if k == "frect":
        return '<rect x="%s" y="%s" width="%s" height="%s" rx="%s" fill="currentColor" stroke="none"/>' % p[1:6]
    if k == "path":
        return '<path d="%s"/>' % p[1]
    if k == "fpath":
        return '<path d="%s" fill="currentColor" stroke="none"/>' % p[1]
    raise ValueError("unknown primitive: %r" % (k,))


# ── 图元 → XAML Geometry ──────────────────────────────────────
def xaml_el(p):
    k = p[0]
    if k in ("circle", "dot"):
        cx, cy, r = p[1:4]
        return '<EllipseGeometry Center="%s,%s" RadiusX="%s" RadiusY="%s"/>' % (cx, cy, r, r)
    if k == "line":
        return '<LineGeometry StartPoint="%s,%s" EndPoint="%s,%s"/>' % p[1:5]
    if k in ("rect", "frect"):
        x, y, w, h, rx = p[1:6]
        return '<RectangleGeometry Rect="%s,%s,%s,%s" RadiusX="%s" RadiusY="%s"/>' % (x, y, w, h, rx, rx)
    if k in ("path", "fpath"):
        return '<PathGeometry Figures="%s"/>' % p[1]
    raise ValueError("unknown primitive: %r" % (k,))


def is_fill(p):
    return p[0] in FILL_KINDS


def icon_key(cat, name):
    """nav + comm-ok → Icon.Nav.CommOk"""
    camel = "".join(w[:1].upper() + w[1:] for w in name.split("-"))
    return "Icon.%s.%s" % (cat.capitalize(), camel)


# ── 渲染 ──────────────────────────────────────────────────────
def render_svg(icon):
    size = icon.get("size", 24)
    sw = icon.get("sw", 1.75)
    body = "\n".join("  " + svg_el(p) for p in icon["els"])
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {s} {s}" width="{s}" height="{s}" '
        'fill="none" stroke="currentColor" stroke-width="{w}" stroke-linecap="round" '
        'stroke-linejoin="round">\n{body}\n</svg>\n'
    ).format(s=size, w=sw, body=body)


def _group(key, els, tag="GeometryGroup"):
    lines = ['        <%s x:Key="%s">' % (tag, key)]
    lines += ["          " + xaml_el(p) for p in els]
    lines.append("        </%s>" % tag)
    return "\n".join(lines)


def render_xaml(cat, name, icon):
    """输出该图标在 XAML ResourceDictionary 中的片段。"""
    size = icon.get("size", 24)
    strokes = [p for p in icon["els"] if not is_fill(p)]
    fills = [p for p in icon["els"] if is_fill(p)]
    key = icon_key(cat, name)
    out = ['  <!-- %s · %s · %sx%s -->' % (cat, icon.get("label", name), size, size)]
    if strokes:
        out.append(_group(key, strokes))
    if fills:
        out.append(_group(key + ".Fill", fills))
    return "\n".join(out)


# ── 落盘 ──────────────────────────────────────────────────────
def write_svg(cat, icons):
    d = os.path.join(ROOT, "icons", cat)
    os.makedirs(d, exist_ok=True)
    n = 0
    for name, icon in icons.items():
        with open(os.path.join(d, name + ".svg"), "w", encoding="utf-8", newline="\n") as f:
            f.write(render_svg(icon))
        n += 1
    return n


XAML_HEAD = '''<!-- ════════════════════════════════════════════════════════════════
     PlcHmi 图标库 · WPF Geometry 资源
     由 tools/gen_all.py 自动生成，请勿手工修改。
     用法（Path 描边，颜色随绑定）：
       <Path Data="{StaticResource Icon.Nav.Run}"
             Style="{StaticResource IconPath}"
             Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"/>
     用法（实心部件，仅状态类图标有 .Fill）：
       <Path Data="{StaticResource Icon.Status.Run.Fill}" Fill="{DynamicResource SuccessBrush}"/>
     ════════════════════════════════════════════════════════════════ -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <!-- ── 基础样式：1.75px 圆头圆角描边，24px 网格内原尺寸绘制 ── -->
  <Style x:Key="IconPath" TargetType="Path">
    <Setter Property="Width" Value="24" />
    <Setter Property="Height" Value="24" />
    <Setter Property="Stretch" Value="None" />
    <Setter Property="StrokeThickness" Value="1.75" />
    <Setter Property="StrokeStartLineCap" Value="Round" />
    <Setter Property="StrokeEndLineCap" Value="Round" />
    <Setter Property="StrokeLineJoin" Value="Round" />
    <Setter Property="HorizontalAlignment" Value="Center" />
    <Setter Property="VerticalAlignment" Value="Center" />
  </Style>

  <!-- ── 大尺寸变体：设备工艺图形用 32px 网格，描边略加粗 ── -->
  <Style x:Key="IconPathLarge" TargetType="Path" BasedOn="{StaticResource IconPath}">
    <Setter Property="Width" Value="32" />
    <Setter Property="Height" Value="32" />
    <Setter Property="StrokeThickness" Value="1.9" />
  </Style>
'''


def write_xaml(items, path):
    """items: [(cat, name, icon), ...]"""
    parts = [XAML_HEAD]
    cur = None
    for cat, name, icon in items:
        if cat != cur:
            if cur is not None:
                parts.append("")
            parts.append("  <!-- ══ %s ══ -->" % CAT_LABEL.get(cat, cat))
            cur = cat
        parts.append(render_xaml(cat, name, icon))
    parts.append("\n</ResourceDictionary>\n")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(parts))
    return path
