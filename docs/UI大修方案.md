# PlcHmi 工控上位机 · UI 大修方案

> 评审范围：`src/PlcHmi.App` 全部 17 个 XAML（8 个主页面 + 5 个对话框 + 2 个主题字典 + 图标库）
> 设计目标：**聚焦性 · 可读性 · 人机操作友好性**（工业生产场景：车间触屏/工控机，操作员长时间注视，误操作代价高）

---

## 一、现状评估

### 已具备的基础（保留，不动）
- 完整的 `Palette.xaml` token 体系：色板、字号、4px 栅格间距、语义按钮均已核算 WCAG AA 对比度（白字 4.51–10.0）。
- 全局焦点环（双描边，任意背景可见）、hover/pressed 罩层降明度方案、细滚动条、统一 ToolTip。
- 手动页未使能锁定、示教页空状态、机种页校验双态等细节已到位。

### 核心差距（本次大修对象）
| # | 主线问题 | 一句话描述 |
|---|---|---|
| 1 | **危险操作零防误触** | 全站"写 PLC / 删数据"类操作（下发参数、切换机种、报警复位、清除历史、恢复默认）无一有二次确认，且多数用的是 32px 高、视觉权重最低的 SmallButton |
| 2 | **触控目标不达标** | 大量关键操作 30–38px（<44px 基准）：点表行 30px、✎✕ 图标钮 30–36px、IO 状态灯 12px、报警确认 72×32 |
| 3 | **报警可读性不足** | 报警描述被省略号截断（报警信息永远不应截断）、活动报警计数 12px 不醒目、底栏报警无操作动线 |
| 4 | **信息聚焦欠佳** | IO 页卡片套卡片、机种信息三处重复、"松开即停"提示三处重复、SettingsView 单卡过长 |
| 5 | **输入无校验反馈** | 所有数值校验规则只藏在 ToolTip 里（端口 1–65535、周期 ≥100 等），非法输入静默通过 |
| 6 | **权限形同虚设** | LoginDialog 界面明文印着"初始密码 123456" |
| 7 | 规范残留 | 1 处手写颜色字面量（ManualView L119）、约 20 处手写 Margin 字面量 |

---

## 二、设计原则（工业 HMI 三维标准）

**聚焦性**
- 每屏只有一个视觉锚点（当前状态/主操作），次级信息降级为 caption 或折叠。
- 高频操作放大、低频配置收纳；同类信息合并，消除三处以上的重复。
- 状态用"色 + 图标 + 文字"三通道冗余编码，不单靠颜色（考虑色弱）。

**可读性**
- 关键读数 ≥15px、单位紧贴数值；报警文本**永不截断**。
- 数值一律等宽字体（已有 NumericFontFamily，补齐遗漏处）。
- 字面量颜色/Margin 清零，全部回归 token。

**人机操作友好性**
- 触控目标 ≥44×44px；危险操作 ≥52px 且颜色 + 位置双重隔离。
- 写 PLC / 删数据的操作必须二次确认，确认框写明**后果**而非复述按钮名。
- 破坏性按钮放在与常规按钮物理隔离的位置（右侧末位 + 间距）。

---

## 三、整改方案（按优先级）

### P0 · 安全与防误触（最高优先）

**1. 新增"危险操作确认"基础设施**
- `Themes/Controls.xaml` 新增 `DangerousActionButton` 样式（BasedOn ButtonDanger）：高度 52px、加 ⚠ 前缀图标、边框加粗一档，与普通按钮一眼可区分。
- 新建通用 `Views/ConfirmDialog.xaml`（等宽风格与现对话框一致）：标题 / 后果描述 / [取消] [确认执行] 双按钮，确认按钮颜色随操作等级（Danger=不可恢复，Warning=写 PLC）。默认焦点在"取消"，回车=取消、需显式点击确认，杜绝惯性回车误触。
- ViewModel 层提供 `IConfirmService`（Business 注册），各 VM 在 Command 中先 `await _confirm.ConfirmAsync(title, consequence)` 再执行写操作。

**2. 逐页接入（写 PLC / 删数据操作全部覆盖）**

| 页面 | 操作 | 确认等级 | 附加整改 |
|---|---|---|---|
| ParamView | 下发到 PLC / 恢复默认 | Warning / Danger | "恢复默认"从参数区移出、改为独立 DangerousActionButton 且与读取/保存拉开间距 |
| RecipeView | 切换机种 / 下发本机数据 | Warning / Warning | "切换"由 80×32 升为 ≥44px；"下发到 PLC"从 SmallButton 升为 PrimaryButton+确认 |
| AlarmView | 报警复位 / 清除历史 / 全部确认 | Warning / Danger / Low | "清除历史"移入历史区右端、DangerousActionButton，与普通按钮物理隔离 |
| ManualView | 气缸伸出（机械动作） | 仅"伸出"方向加确认，缩回不加（保住点动效率） | 切换钮 80×38 → ≥52px |
| TeachView | 下发本面点表 / 清空当前点 | Warning / Low | 三个底部按钮按权重分级：下发=Primary+确认，清空=Small，避免视觉等权 |
| SettingsView | 保存并生效（会断连重连） | Warning | 确认文案写明"确认后将断开与 PLC 的连接约 X 秒" |

**3. 权限兜底（LoginDialog）**
- 删除界面上"初始密码 123456"明文；改为首次登录强制改密（对话框提示后跳设置），或至少仅在管理员首次登录时显示一次性提示。

### P1 · 触控目标统一（44px 基准）

| 位置 | 现状 | 目标 |
|---|---|---|
| TeachView 点表行 + CoordBox | 行高 30px | 行高 44px，输入框高度随之放大 |
| ParamView / AlarmView / ManualView 的 ✎ ✕ 图标钮 | 30–38px | 统一 44×44，图标 16px 居中 |
| ParamView 置位/复位 | 46×32 | 52px 高；颜色语义修正：置位=Primary、复位=Warning（绿色=置位易被误读为"安全无操作"） |
| AlarmView 行内确认 | 72×32 | ≥44 高，保持行密度用紧凑宽度 |
| IoView 状态灯 | 12px | 16px 灯 + ON/OFF 文字放大至 12px→13px |
| LoginDialog 输入框/按钮 | 36px | 44px |
| 新增 token | — | `Palette.xaml` 增加 `Icon button` 规范：`IconButtonStyle`（44×44，圆角 ControlCornerRadius，hover 走 PrimarySoft） |

### P2 · 可读性

1. **报警文本不截断**：AlarmView L45 报警描述改 `TextWrapping="Wrap"`（行高自适应），行内确认钮 `VerticalAlignment="Center"`；全文仍保留 ToolTip。
2. **活动报警计数放大**：12px → 16px SemiBold，维持 DangerBrush；底栏报警条（MainWindow）加"查看 →"可点击区域直达报警页（聚焦报警动线闭环）。
3. **关键读数字号纠偏**：ManualView 轴位置 11px → 15px + NumericFontFamily（当前用 Mini 字号读数，位置读数是示教的核心信息）。
4. **长说明分层**：TeachView L321、RecipeView L227 的 12px 长段说明改为"一行摘要 + 展开箭头"或缩短为 ≤2 行要点；SettingsView"快组周期"等术语补一句 caption 释义（或统一为"快速组/慢速组/事件组"）。
5. **IO 筛选框**：加占位提示"按编号或含义筛选"（水印），不再只藏 ToolTip。

### P3 · 聚焦性与信息架构

1. **去卡片套卡片（IoView）**：X/Y 大容器降级为 `SectionTitleText` 标题 + 分组线，内部保留每组 CardBorder；只留一层卡片边界。
2. **合并重复状态卡（RunView）**：底部"设备状态总览"胶囊卡与顶部四张指标卡存在信息重叠（设备状态出现两次）——总览卡中的"设备"项删除，保留通信/报警两项，指标卡状态色即唯一来源。
3. **消除三重提示（ManualView）**："松开即停"保留警告条一处（最显眼处），删 ToolTip 与 caption 两处。
4. **机种信息收敛（RecipeView）**：大卡保留；列表首行高亮保留（定位需要）；名称卡与大卡合并为一张卡（机种号 + 名称 + 切换钮同卡，操作与状态就近）。
5. **SettingsView 拆卡**：通信配置卡拆为"当前配置（只读摘要）"与"管理员编辑（默认折叠）"两卡，页面首屏聚焦状态而非表单。
6. **空状态补齐**：RecipeView 机种列表、AlarmView 历史列表、IoView 筛选无结果，复用手动页已有的空状态模式（图标 + 一句话 + 引导动作）。

### P4 · 规范清理

- `ManualView L119` `#CCFFFFFF` → `OnPrimaryFadedTextBrush`。
- 约 20 处手写 Margin：高频的 `0,10,6,10`（表格单元）提炼为 `TableCellPadding` token 后全量替换；其余逐处映射到 Gap* token。
- TeachView 四张轴位置卡提炼为 `AxisPositionCard`（DataTemplate 或自定义控件），消除四份复制粘贴。

---

## 四、实施顺序与验收标准

**建议顺序**：P0 基础设施（ConfirmDialog + IConfirmService + DangerousActionButton）→ P0 各页接入 → P1 触控 → P2 可读性 → P3 聚焦 → P4 清理。P0/P1 均只动 XAML + VM Command 前置确认，不碰通信层，风险低。

**验收清单**：
- [x] 全部写 PLC / 删数据操作 100% 有二次确认，确认默认焦点在"取消"（`ConfirmDialog`，三级分级）
- [x] 全部可点击元素 ≥44×44px（含顶栏、三个编辑对话框、IO 状态灯、点表行 44px）
- [x] 报警文本零截断（换行显示）；活动报警计数 16px
- [x] 手写颜色字面量 = 0（仅 Palette/样式库定义处允许）；页面 Margin/Padding 字面量已归零（组件库内部像素定义除外）
- [x] 卡片嵌套 = 0 层以上（IoView 已去外层卡）
- [x] 每屏视觉锚点唯一：主操作 Primary 级按钮一屏一个，危险操作独立 Dangerous 样式
- [x] WCAG AA 对比度保持（新增色全部复用已核算 token）

---

## 五、实施结果（2026-09-12 完成）

**新增**
- `Views/ConfirmDialog.xaml(.cs)`：品牌化分级确认框（Neutral/Warning/Danger），默认焦点在取消、回车=取消、ESC=取消
- `Controls.xaml`：`DangerousActionButton`（52px 加重）、`IconButton`（44×44 图标钮）、`IconButtonGlyph`
- `Palette.xaml`：`DangerousButtonHeight`/`IconButtonSize` 及 `NavHeaderMargin`、`ChipPadding`、`NoticePadding` 等间距 token

**修复的既有缺陷**
- IO 页筛选框与「仅显示已定义」为死绑定（属性无生效逻辑）→ `IO视图模型` 实现全量索引 + 过滤分组，并补筛选无结果空状态与「清除筛选」出口

**验证**
- `dotnet build` 0 错误 0 警告
- `--smoke` 自检：8 页全部通过 + 4 个对话框（含新确认框）实例化通过 + 交互自检通过
- 单元测试 293/293 通过

**发布**
- 命令：`dotnet publish src/PlcHmi.App -c Release -r win-x64 --self-contained true -o publish`
- 产物：`publish/PlcHmi.App.exe`（自包含运行时，工控机免装 .NET），`config/` 9 个现场配置随包输出，共约 142 MB
- 发布产物已跑 `--smoke` 自检：失败 0 项
