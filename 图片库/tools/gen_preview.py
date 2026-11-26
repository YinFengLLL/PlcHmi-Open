# -*- coding: utf-8 -*-
"""HTML 总览预览页构建器"""

from iconlib import PALETTE as P, CAT_LABEL, CAT_ORDER, svg_el

CSS = """
*{box-sizing:border-box;margin:0;padding:0}
body{background:%(bg)s;color:%(text)s;font:14px/1.6 "Microsoft YaHei UI","Segoe UI",sans-serif;padding:40px 32px 64px}
.wrap{max-width:1180px;margin:0 auto}
h1{font-size:24px;font-weight:600;letter-spacing:.5px}
.sub{color:%(text2)s;font-size:13px;margin-top:6px}
.chips{display:flex;gap:8px;flex-wrap:wrap;margin-top:18px}
.chip{display:flex;align-items:center;gap:6px;background:%(surface)s;border:1px solid %(border)s;
      border-radius:999px;padding:4px 12px 4px 8px;font-size:12px;color:%(text2)s}
.sw{width:12px;height:12px;border-radius:3px;border:1px solid rgba(0,0,0,.06)}
section{margin-top:44px}
.head{display:flex;align-items:baseline;gap:10px;padding-bottom:10px;border-bottom:2px solid %(primary)s}
.head h2{font-size:16px;font-weight:600;color:%(primary)s}
.head .n{font-size:12px;color:%(text2)s}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(146px,1fr));gap:12px;margin-top:16px}
.card{background:%(surface)s;border:1px solid %(border)s;border-radius:8px;padding:16px 10px 12px;
      text-align:center;transition:.15s}
.card:hover{border-color:%(primaryLt)s;box-shadow:0 4px 12px rgba(11,61,145,.10);transform:translateY(-2px)}
.box{height:44px;display:flex;align-items:center;justify-content:center;color:%(primary)s}
.name{font-family:Consolas,"Cascadia Mono",monospace;font-size:12px;color:%(text)s;margin-top:10px;
      word-break:break-all}
.lab{font-size:11px;color:%(text2)s;margin-top:2px}
.states{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:12px;margin-top:16px}
.st{background:%(surface)s;border:1px solid %(border)s;border-radius:8px;padding:12px 14px}
.st .t{font-size:12px;color:%(text2)s;font-family:Consolas,monospace}
.st .row{display:flex;gap:16px;margin-top:8px;align-items:center}
.st .row span{display:flex;flex-direction:column;align-items:center;gap:2px}
.st .row em{font-style:normal;font-size:10px;color:%(text2)s}
.sizes{display:flex;gap:28px;align-items:flex-end;margin-top:16px;background:%(surface)s;
       border:1px solid %(border)s;border-radius:8px;padding:20px 26px}
.sizes div{text-align:center;color:%(primary)s}
.sizes em{font-style:normal;display:block;font-size:11px;color:%(text2)s;margin-top:6px}
footer{margin-top:48px;color:%(text2)s;font-size:12px;border-top:1px solid %(border)s;padding-top:16px}
""" % P


def svg_at(icon, disp):
    size = icon.get("size", 24)
    sw = icon.get("sw", 1.75)
    body = "".join(svg_el(p) for p in icon["els"])
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %s %s" width="%s" height="%s" '
            'fill="none" stroke="currentColor" stroke-width="%s" stroke-linecap="round" '
            'stroke-linejoin="round">%s</svg>') % (size, size, disp, disp, sw, body)


STATUS_COLORS = [
    ("primary", "主色"),
    ("success", "正常"),
    ("warning", "预警"),
    ("danger", "报警"),
    ("muted", "离线"),
]


def build(catalog):
    by_cat = {}
    for cat, name, icon in catalog:
        by_cat.setdefault(cat, []).append((name, icon))

    o = ['<!DOCTYPE html>', '<html lang="zh-CN">', '<head>', '<meta charset="utf-8">',
         '<title>PlcHmi 图标库 · 总览</title>', '<style>' + CSS + '</style>', '</head>', '<body>',
         '<div class="wrap">']

    total = len(catalog)
    o.append('<h1>PlcHmi 图标库</h1>')
    o.append('<div class="sub">%d 张线性描边图标 · SVG 矢量源文件 + WPF XAML 几何资源 · '
             '描边圆头圆角 · 颜色由 currentColor / 画刷绑定决定</div>' % total)

    chips = ['<div class="chips">']
    for k, label in [("primary", "主色"), ("success", "正常"), ("warning", "预警"),
                     ("danger", "报警"), ("muted", "离线"), ("soft", "浅底"), ("border", "描边")]:
        chips.append('<div class="chip"><i class="sw" style="background:%s"></i>%s %s</div>'
                     % (P[k], label, P[k].upper()))
    chips.append('</div>')
    o.append("".join(chips))

    for cat in CAT_ORDER:
        items = by_cat.get(cat, [])
        if not items:
            continue
        o.append('<section>')
        o.append('<div class="head"><h2>%s</h2><span class="n">%d 张 · icons/%s/</span></div>'
                 % (CAT_LABEL[cat], len(items), cat))
        o.append('<div class="grid">')
        for name, icon in items:
            o.append('<div class="card"><div class="box">%s</div>'
                     '<div class="name">%s.svg</div><div class="lab">%s</div></div>'
                     % (svg_at(icon, 34), name, icon["label"]))
        o.append('</div>')

        if cat == "status":
            o.append('<div class="head" style="margin-top:28px;border-bottom-width:1px">'
                     '<h2 style="font-size:14px">同一张图 · 五种状态配色</h2>'
                     '<span class="n">绑定不同画刷即可，无需多套资源</span></div>')
            o.append('<div class="states">')
            for name, icon in items:
                cells = "".join(
                    '<span style="color:%s">%s<em>%s</em></span>' % (P[k], svg_at(icon, 26), lbl)
                    for k, lbl in STATUS_COLORS)
                o.append('<div class="st"><div class="t">%s</div><div class="row">%s</div></div>'
                         % (name, cells))
            o.append('</div>')

        o.append('</section>')

    o.append('<section><div class="head"><h2>尺寸表现</h2>'
             '<span class="n">矢量绘制，放大不糊</span></div>')
    o.append('<div class="sizes">')
    demo = _size_demo(catalog)
    for d in (16, 24, 32, 48, 64):
        o.append('<div>%s<em>%dpx</em></div>' % (svg_at(demo, d), d))
    o.append('</div></section>')

    o.append('<footer>生成方式：<code>python tools/gen_all.py</code> · '
             '图标以图元列表定义，改 <code>tools/icons_*.py</code> 后重跑即可全量再生成</footer>')
    o.append('</div></body></html>')
    return "\n".join(o)


def _size_demo(catalog):
    for cat, name, icon in catalog:
        if cat == "device" and name == "motor":
            return icon
    return catalog[0][2]
