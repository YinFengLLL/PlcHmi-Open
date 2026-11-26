#!/usr/bin/env bash
# ============================================================================
# PlcHmi 提交前自检门禁
# 用法：bash tools/verify.sh
# 任何一步失败即中止（set -e），红灯不许提交。
# 配套文档：docs/04-代码审查标准与流程.md §5
# ============================================================================

set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$(pwd)"
echo "项目根目录：$ROOT"
echo

# 沙箱/CI 环境会剥离 Windows 环境变量，未打补丁时 NuGet 会报
#   Value cannot be null. (Parameter 'path1')
if [ -f "$HOME/.workbuddy/dotnet-env.sh" ]; then
  # shellcheck disable=SC1091
  source "$HOME/.workbuddy/dotnet-env.sh"
fi

step() { echo; echo "==> $1"; }

# ------------------------------------------------------------------ 1 构建
step "1/3 构建（要求 0 警告 0 错误）"
# --no-incremental 是必须的：增量编译会跳过未改动的项目，
# 于是"上游引入的警告"在热树上根本不重新报告 —— 门禁会假绿。
# 这个盲区真实发生过：同一棵树，带增量报 0 警告，强制重编报 CS0067。
#
# WarningsNotAsErrors=NU1900：NuGet 取漏洞数据失败是网络问题，不是代码问题，
# 不该以 "error" 名义把构建判红（CI 网络抖动时会误报）。
dotnet build PlcHmi.slnx -warnaserror --no-incremental -p:WarningsNotAsErrors=NU1900

# ------------------------------------------------------------------ 2 测试
step "2/3 单元 + 集成测试"
dotnet test PlcHmi.slnx --no-build --verbosity minimal

# ------------------------------------------------------- 3 界面自检（可选）
step "3/3 界面自检（8 页 + 3 对话框）"
SMOKE=""
for cand in \
  "src/PlcHmi.App/bin/Debug/net10.0-windows/PlcHmi.App.exe" \
  "dist/PlcHmi.App.exe" \
  "publish/PlcHmi.App.exe"
do
  if [ -f "$cand" ]; then SMOKE="$cand"; break; fi
done

if [ -z "$SMOKE" ]; then
  echo "   ⚠️  未找到 PlcHmi.App.exe，跳过界面自检"
  echo "      请先执行：dotnet build PlcHmi.slnx"
else
  echo "   使用：$SMOKE"
  # --smoke 失败会返回非 0，触发 set -e 中止
  "$SMOKE" --smoke
fi

# ------------------------------------------------------- 4 静态门禁
if command -v python >/dev/null 2>&1; then
  for check in tools/check_contrast.py tools/check_thresholds.py; do
    [ -f "$check" ] || continue
    step "4/4 $check"
    python "$check"
  done
else
  echo "   ⚠️  未找到 python，跳过对比度与规模门禁"
fi

echo
echo "✅ 全部门禁通过，可以提交"
