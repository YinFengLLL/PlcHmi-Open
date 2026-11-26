#!/usr/bin/env python3
"""XAML 资源键门禁（2026-10-15 标定页卡死事故的防线）。

为什么需要它
------------
`{StaticResource X}` 里写错一个键，**页面照样能加载**——只要那个引用出现在
`DataTemplate` / `ControlTemplate` 里：模板内容是"用到时才解析"的。
而 `--smoke` 是离屏遍历页面，列表行不会被实例化，于是完全碰不到这类错误。

事故就长这样：标定页把 `GapTop24`（主题里没有这个令牌）写进了"其它点"列表行的
DataTemplate —— `--smoke` 全绿、测试全过、真机一进页面就每帧抛 XamlParseException：
渲染线程被异常刷满、界面直接卡死，日志一天涨到 447MB。

所以这条门禁在**文本层**对齐键名，与是否渲染无关：
  · 收集 src/PlcHmi.App 下全部 *.xaml 的 `x:Key="..."`
  · 检查每一处 `{StaticResource ...}` / `{DynamicResource ...}` 是否在集合里
  · `StaticResource` 缺失 → 失败：运行时必抛异常
  · `DynamicResource` 缺失 → 也失败：不抛异常，但样式静默丢失（"按钮变透明"这类
    最难查的表现），同样不许进仓库

用法
----
    python tools/check_xaml_resources.py
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
XAML_ROOT = REPO_ROOT / "src" / "PlcHmi.App"
SKIP_DIRS = {"bin", "obj"}

KEY_PATTERN = re.compile(r'x:Key="([^"]+)"')
USE_PATTERN = re.compile(r"\{(Static|Dynamic)Resource\s+([A-Za-z0-9_.]+)\}")


def iter_xaml_files() -> list[Path]:
    return [
        path
        for path in sorted(XAML_ROOT.rglob("*.xaml"))
        if not any(part in SKIP_DIRS for part in path.parts)
    ]


def collect_keys(文件列表: list[Path]) -> set[str]:
    keys: set[str] = set()

    for path in 文件列表:
        for match in KEY_PATTERN.finditer(path.read_text(encoding="utf-8", errors="replace")):
            keys.add(match.group(1))

    return keys


def main() -> int:
    文件列表 = iter_xaml_files()

    if not 文件列表:
        print(f"[FAIL] 在 {XAML_ROOT} 下没扫到任何 XAML —— 路径或扫描逻辑有问题（门禁不许假绿）")
        return 1

    已定义 = collect_keys(文件列表)

    if not 已定义:
        print("[FAIL] 没扫到任何 x:Key —— 扫描逻辑有问题（门禁不许假绿）")
        return 1

    问题列表: list[tuple[str, int, str, str]] = []

    for path in 文件列表:
        for 行号, 行内容 in enumerate(
            path.read_text(encoding="utf-8", errors="replace").splitlines(), start=1
        ):
            for 类型, 键名 in USE_PATTERN.findall(行内容):
                if 键名 not in 已定义:
                    问题列表.append((path.relative_to(REPO_ROOT).as_posix(), 行号, 类型, 键名))

    print("=" * 74)
    print("XAML 资源键门禁（StaticResource 缺失 = 运行时必崩；DynamicResource 缺失 = 样式静默丢失）")
    print("=" * 74)
    print(f"扫描：{len(文件列表)} 个 XAML 文件，{len(已定义)} 个已定义键")
    print("-" * 74)

    if not 问题列表:
        print("[PASS] 全部资源引用都有定义")
        return 0

    print(f"[FAIL] {len(问题列表)} 处引用了未定义的资源：")

    for 文件, 行号, 类型, 键名 in 问题列表:
        print(f"  · {文件}:{行号}  {{{类型}Resource {键名}}} —— 主题里没有这个键")

    print()
    print("处理方式：改用主题里已有的令牌（src/PlcHmi.App/Themes 下 x:Key 为准），")
    print("或先在主题里补上该令牌再引用。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
