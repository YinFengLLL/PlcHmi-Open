#!/usr/bin/env python3
"""代码规模门禁（棘轮式）。

为什么是"棘轮"而不是"一刀切"
--------------------------------
`docs/04` §3.3 规定单文件 400 行、单函数 50 行。但项目里已经有 7 个文件越线，
而标准自己也写了"不要求立刻重构（能跑就别乱动）"。

于是一刀切的门禁只有两个结局：要么永远红着没人看，要么被加一堆
`# pragma` / 白名单绕过 —— 两种都比没有门禁更糟。

棘轮的做法：**把现状固化成基线，只禁止它变坏。**
  · 新文件超过 400 行        → 失败
  · 基线内的文件继续变大      → 失败
  · 基线内的文件变小或达标    → 通过，并提示把基线收紧

基线只应缩小，不应变大。要加新条目，必须在 PR 里写明理由。

用法
----
    python tools/check_thresholds.py            # 检查
    python tools/check_thresholds.py --baseline # 重新生成基线（仅在确有必要时）
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SRC_ROOT = REPO_ROOT / "src"

FILE_LINE_LIMIT = 400

# 已知超标文件 → 当前行数。**只允许调小。**
#
# 口径：总行数（含空行与注释），与 wc -l / 编辑器显示一致。
# 注意早期文档里的"7 个文件超 400 行"是用 PowerShell `Measure-Object -Line` 数的，
# 那个口径**不数空行**，所以数字偏小（例如 App.xaml.cs 记 504，实际 722）。
# 这里统一到总行数口径，基线也按此重算。
FILE_BASELINE: dict[str, int] = {
    # 2026-09-17 审查整改：OPC UA 分支把 6 项指标推过了基线（TeachViewModel +44、
    # AxisAddressService +38、PollingEngine +16、InovanceAddressResolver 新超标 404、
    # RecipeTeachIntegrationTests 新超标 439、catch (Exception) 83→85）。
    # 按"基线只允许调小"的棘轮规则，本次不是放宽基线，而是把职责拆出去：
    #   · TeachViewModel        → 行视图模型移入 TeachRowViewModel.cs
    #   · AxisAddressService    → 校验规则移入 AxisAddressValidator.cs
    #   · PollingEngine         → 值发布与范围诊断移入 PollingEngine.Publish.cs
    #   · InovanceAddressResolver → 基址表移入 InovanceElementSpecs.cs
    #   · RecipeTeachIntegrationTests → 拆为 partial 的两个文件
    # 拆分后基线整体下调（数值为 2026-09-17 实测总行数）。
    # 2026-09-29 审查整改（算法优化 + 缺陷修复）按棘轮规则整体收紧：
    #   · RecipeService  633 → 506：CSV 导入导出移入 RecipeService.Csv.cs
    #   · TeachService   611 → 559：面启用移入 TeachService.Face.cs
    #   · PollingEngine  475 → 435：IO 点位采集移入 PollingEngine.IO.cs
    #   · ParamTableService 423 → 达标（校验移入 ParamTableService.Validate.cs），从基线移除
    # 2026-10-12 项目模板实例化（6 轴点胶锁付机）：按棘轮规则整体收紧——
    #   · TeachViewModel / TeachService 随旧示教子系统删除，从基线移除
    #   · RecipeService 506 → 497（示教耦合摘除后减少 9 行）
    #   新增的点表 / 标定 / 其它点代码一律按 400 行上限拆分，未新增基线条目。
    # 2026-10-12 机种模型调整（机种 = D2500–D3500 快照）：
    #   · RecipeService 497 → 491：机种区段 IO 移入 RecipeService.Snapshot.cs
    #   · RecipeService 491 → 426：启动同步（区段外 D 区以 PLC 为准）移入 RecipeService.Sync.cs
    #   · RecipeCsvCodec 445 → 达标（旧三段式 CSV 改为机种区段单段导出），从基线移除
    # 2026-11-19 棘轮收紧：本轮未改该文件，实测总行数已由 610 降到 588
    # （历史重构的收益），按"基线只允许调小"同步下调，防止以后反弹。
    "src/PlcHmi.App/ViewModels/ParamViewModel.cs": 588,
    # 2026-10-21：机种号改"开机读一次"后，读机种号方法移入 RecipeService.PlcNo.cs，
    # RecipeService.cs 降到 400 行以内 → 从基线移除（棘轮收紧）。
    # 2026-10-12 异常提示整改：575 → 546（全局异常处理器移入 CrashGuard.cs，
    # 横幅刷新移入 MainViewModel.Banners.cs），基线随之收紧。
    # 2026-10-15 算法优化与缺陷修复：退出生命周期移入 App.Lifecycle.cs，546 → 506，基线随之收紧。
    "src/PlcHmi.App/App.xaml.cs": 506,
    # 2026-10-12 v1.1：轴项拆到 ManualAxisViewModel.cs，529 → 518，基线随之收紧
    # 2026-10-15：对话框提示/错误统一切 UI 线程等改动未新增行数，518 → 511，基线随之收紧。
    "src/PlcHmi.App/ViewModels/ManualViewModel.cs": 511,
    # 2026-10-15：读取计划编译移入 PollingEngine.Plans.cs，435 → 达标，从基线移除。
    # 2026-10-15：内存初始化移入 VirtualPlcDevice.Memory.cs（403 条基线注释里
    # "下次动该文件时顺手拆分"的约定本次兑现），达标后从基线移除。
    # 已达标（≤400 行），从基线移除：
    #   PollingEngine（原 435）、VirtualPlcDevice（原 403）、
    #   AxisAddressService（原 424）、RecipeModels（原 418）、ParamTableService（原 423）
}

# 宽泛捕获的现状基线（docs/04 §1 曾记 74 处，2026-09-13 实测 83 处，2026-09-17 一度升到 85）。
# CA1031 刻意保持 suggestion 级别（后台服务顶层"记录+继续"是正确设计），
# 但总数不应继续增长。
#
# 2026-09-17：把"try → await 服务调用 → catch → 记日志 + 弹错误框"这段逐字重复的样板
# 抽到 界面命令.异步执行（同类代码出现第 3 次就该抽），5 处各减一个 catch，
# 净减 3 → 82，基线随之收紧。
#
# 2026-09-26：「Modbus 运行模式管理」新增 3 处 —— 都在"必须兜住、不能让一次异常掀翻连接"的位置：
#   · ModbusSlaveProtocol.处理请求：单条请求解析失败的兜底（回了异常响应而不是断连）
#   · 虚拟PLC从站.异步服务客户端：调试客户端断开属常态，按类型过滤后记 Debug
#   · 虚拟PLC从站.异步停止：等待接受循环退出，取消防抖后仅留痕
# 同时按"重复样板抽公共方法"的既定做法，把运行模式切换命令的 try/catch 改为复用
# 界面命令.异步执行（少 2 处）。工作区既有代码另有 5 处新增（历史记录 / 产量统计，
# 非本次改动），故基线由 82 上调到 90 —— 这是"冻结现状"，不是放宽对新增代码的要求。
#
# 2026-09-29：审查整改（H1 脉冲复位无条件执行的 catch 属必要兜底，+1）；
# 同时把两处"已按 when 过滤、但仍占用宽泛捕获额度"的捕获改为具体类型
# （AtomicFile 备份恢复、ConfigPaths 可写探查，语义完全等价），净 -1 → 89。
#
# 2026-10-12：项目模板实例化（点表/标定/其它点）新增的界面命令统一走
# 界面命令.异步执行（既有公共入口），同时摘除了随旧示教子系统删除的捕获，
# 净 -9 → 80。基线随之收紧。
#
# 2026-10-13：可配置弹窗（弹窗监控服务）+3 —— 装载配置失败停用弹窗、
# 轮询循环兜底（轮询线程绝不能带崩进程）、单轮读取失败跳过。三处都是长驻循环 /
# 后台线程的必要兜底，不能上抛。80 → 83。
#
# 2026-10-13 晚：切换型动作按钮的状态回读 +2（示教页与标定页各一处）——
# 逐按钮回读 PLC 线圈，某个地址读不到只跳过该按钮，否则一处通信抖动会把整页初始化打断。
# 83 → 85。
#
# 2026-10-13 晚：页面上下文同步（D10 页码 / 点码）+1 —— 写入失败只记警告，
# 绝不能因为 PLC 抖动把切页与点选卡住。85 → 86。
#
# 2026-10-15：手动使能保持服务（M666）常驻循环 +2 ——
#   · 异步保持循环：单次写失败不能打断循环，一次网络抖动不该让"手动使能"信号此后再也不写
#   · StopAsync：循环若因 bug 卡住/异常，不能让程序退出挂在这里（与报警扫描服务同款结构）
# 两处都是长驻后台循环的必要兜底，不能上抛。86 → 88。
# 2026-10-15：--smoke 现场行为自检 +1 —— 自检要驱动界面动作并等 PLC 往返，
# 兜底捕获必须留在自检内部：抛出去会走到全局异常处理器，
# 退出码可能仍是 0（门禁假绿），那正是这条自检要防的事。88 → 89。
#
# 2026-10-15 算法优化与缺陷修复：标签采集按读取计划隔离失败 +1 ——
# 曾经裸 foreach，任一计划抛异常（如某段地址 PLC 回异常码 03）会中断整组本轮采集，
# 同组后续全部点位本周期不刷新。与 IO 路径（PollingEngine.IO.cs 尝试读取IO块）
# 同款"单块失败只跳过自己"结构，属必要兜底。89 → 90。
# 同期另外三处修复（历史记录握手、OPC UA 连接收口、动作按钮回读）均通过
# try/finally 与 异步安全调用 复用既有入口实现，未新增宽泛捕获。
#
# 2026-10-16：示教页固定状态灯 +1 —— 灯每秒回读一路 M 线圈（PointTableTeachViewModel.Lamp.cs），
# 地址写错 / 未连接时必须只把灯标成"未读到"并留痕，不能让一盏辅助灯把整页初始化与
# 周期回读打断（与"动作按钮逐按钮回读"同款单项容错，见 2026-10-13 条目）。
# 同期报警横幅与底栏放大只改 XAML/VM 展示逻辑，未新增捕获。90 → 91。
# 2026-10-17：IO 通信诊断 +1 —— 诊断的职责就是"把失败也变成一行读数"：
# 两个功能码读不通时必须照样返回一行（读不到 + 异常摘要），抛出去界面就只剩一句
# "诊断失败"，而现场要看的恰恰是"哪个功能码读不到、报什么错"。
# 与报警扫描、状态灯回读的单项容错同属必要兜底。91 → 92。
# 2026-10-17：参数页回读 +2（延时一条、按钮一条）—— 回读是"让界面跟上 PLC"的附加动作，
# 单条地址配错 / 无应答只该跳过这一条；抛出去会把整页操作打断（用户改延时值时页面弹错）。
# 与弹窗轮询、状态灯回读的单项容错同款。92 → 94。
# 2026-11-19 页面回读合并改造：新加的容错一律改用显式异常类型
# （catch (IOException) / catch (SocketException) / catch (InvalidOperationException) …
# 而非 catch (Exception) when (...)），顺便把逐按钮循环的宽泛捕获收掉，
# 宽泛捕获总数 94 → 91。按棘轮规则把基线同步收紧，防止以后回退。
CATCH_LIMIT = 91

SKIP_DIRS = {"bin", "obj"}


def iter_cs_files():
    for path in SRC_ROOT.rglob("*.cs"):
        if any(part in SKIP_DIRS for part in path.parts):
            continue
        yield path


def count_lines(path: Path) -> int:
    return len(path.read_text(encoding="utf-8", errors="replace").splitlines())


def rel(path: Path) -> str:
    return path.relative_to(REPO_ROOT).as_posix()


def collect() -> tuple[dict[str, int], int]:
    sizes: dict[str, int] = {}
    catch_total = 0
    pattern = re.compile(r"catch\s*\(\s*Exception")

    for path in iter_cs_files():
        text = path.read_text(encoding="utf-8", errors="replace")
        lines = len(text.splitlines())

        if lines > FILE_LINE_LIMIT:
            sizes[rel(path)] = lines

        catch_total += len(pattern.findall(text))

    return sizes, catch_total


def main() -> int:
    parser = argparse.ArgumentParser(description="代码规模棘轮门禁")
    parser.add_argument("--baseline", action="store_true", help="重新生成基线并打印")
    args = parser.parse_args()

    sizes, catch_total = collect()

    if args.baseline:
        print("FILE_BASELINE: dict[str, int] = {")
        for name, lines in sorted(sizes.items(), key=lambda kv: -kv[1]):
            print(f'    "{name}": {lines},')
        print("}")
        print(f"CATCH_LIMIT = {catch_total}")
        return 0

    failures: list[str] = []
    improvements: list[str] = []

    print("=" * 74)
    print(f"代码规模门禁（单文件上限 {FILE_LINE_LIMIT} 行，棘轮式）")
    print("=" * 74)

    # ── 文件行数 ────────────────────────────────────────────────────────
    for name, lines in sorted(sizes.items(), key=lambda kv: -kv[1]):
        allowed = FILE_BASELINE.get(name)

        if allowed is None:
            failures.append(
                f"{name}：{lines} 行，超出 {FILE_LINE_LIMIT} 行上限且不在基线内"
            )
            print(f"[FAIL] {name:<58} {lines:>5} 行（新超标）")
        elif lines > allowed:
            failures.append(
                f"{name}：{lines} 行，超过基线 {allowed} 行（本次 +{lines - allowed}）"
            )
            print(f"[FAIL] {name:<58} {lines:>5} 行（基线 {allowed}，变大）")
        else:
            note = f"基线 {allowed}"
            if lines < allowed:
                note += f"，已减少 {allowed - lines} → 请把基线收紧到 {lines}"
                improvements.append(f"{name} 由 {allowed} 降到 {lines}")
            print(f"[ ok ] {name:<58} {lines:>5} 行（{note}）")

    # ── 已达标但仍在基线里的条目 ────────────────────────────────────────
    for name in FILE_BASELINE:
        if name not in sizes:
            improvements.append(f"{name} 已降到 {FILE_LINE_LIMIT} 行以内，可从基线移除")

    # ── catch (Exception) 总数 ─────────────────────────────────────────
    print("-" * 74)
    if catch_total > CATCH_LIMIT:
        failures.append(
            f"catch (Exception) 由 {CATCH_LIMIT} 增至 {catch_total} 处"
        )
        print(f"[FAIL] catch (Exception) 总数 {catch_total}（基线 {CATCH_LIMIT}）")
    else:
        note = f"基线 {CATCH_LIMIT}"
        if catch_total < CATCH_LIMIT:
            note += "，已减少 → 请把基线收紧"
            improvements.append(f"catch (Exception) 由 {CATCH_LIMIT} 降到 {catch_total}")
        print(f"[ ok ] catch (Exception) 总数 {catch_total}（{note}）")

    # ── 结论 ────────────────────────────────────────────────────────────
    print("-" * 74)

    if improvements:
        print("可以收紧基线：")
        for item in improvements:
            print(f"  · {item}")
        print()

    if failures:
        print(f"[FAIL] {len(failures)} 项规模门禁未通过：")
        for item in failures:
            print(f"  · {item}")
        print()
        print("处理方式见 docs/04 §3.3：新代码不要再往超标文件里塞；")
        print("改动超标文件时顺手把职责拆出去。")
        return 1

    print("[PASS] 代码规模未变坏")
    return 0


if __name__ == "__main__":
    sys.exit(main())
