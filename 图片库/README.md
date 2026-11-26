# PlcHmi 图标库

工控上位机常用图形元素，**41 张线性描边图标**，专为 `src/PlcHmi.App` 的「深蓝工业风 · 白色基底 + 深蓝基调」配套设计。

- 描边统一 **1.75px 圆头圆角**（设备类 32px 网格用 1.9px），视觉重量一致
- 颜色不写死：用 `currentColor` / 画刷绑定，**一套图顶多套用**
- 矢量绘制，16px 到 96px 放大不糊
- 配色已对齐 `Themes/Palette.*.xaml` 令牌体系，无额外色值

---

## 目录结构

```
图片库/
├─ preview.html                 ← 浏览器打开可看 41 张图总览 + 五种状态配色演示
├─ icons/                       ← SVG 矢量源文件（设计交付物）
│  ├─ nav/                      导航与功能   11 张
│  ├─ status/                   状态与报警灯 10 张
│  ├─ action/                   操作按钮     10 张
│  └─ device/                   设备工艺图形 10 张
├─ xaml/
│  └─ PlcHmi.Icons.xaml         ← WPF 可直接引用的 Geometry 资源字典
└─ tools/                       ← 生成器：改图后重跑即可全量再生成
   ├─ iconlib.py                图元 → SVG / XAML 渲染
   ├─ icons_nav.py              四类图标的数据定义
   ├─ icons_status.py
   ├─ icons_action.py
   ├─ icons_device.py
   ├─ gen_preview.py            HTML 总览页构建
   └─ gen_all.py                一键生成入口
```

---

## 一、接入 WPF

### 1. 拷贝并合并资源字典

把 `xaml/PlcHmi.Icons.xaml` 拷到工程里（建议 `src/PlcHmi.App/Images/`），在 `App.xaml` 合并：

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="Themes/Palette.Base.xaml" />
      <ResourceDictionary Source="Themes/Palette.Dark.xaml" />
      <ResourceDictionary Source="Themes/Controls.xaml" />
      <ResourceDictionary Source="Images/PlcHmi.Icons.xaml" />
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

### 2. 画一个图标

资源键规则：`Icon.<类别>.<文件名驼峰>`，例如 `icons/nav/run.svg` → `Icon.Nav.Run`。

```xml
<!-- 导航按钮：图标自动跟随按钮前景色 -->
<Button Style="{StaticResource NavButton}" Foreground="{StaticResource PrimaryBrush}">
  <StackPanel Orientation="Horizontal">
    <Path Data="{StaticResource Icon.Nav.Run}"
          Style="{StaticResource IconPath}"
          Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}" />
    <TextBlock Text="运行" Margin="8,0,0,0" VerticalAlignment="Center" />
  </StackPanel>
</Button>
```

`IconPath` / `IconPathLarge` 样式已设好尺寸（24 / 32）、描边粗细与圆头圆角 —— **只需再给 `Stroke` 即可**，不用重复写 `StrokeThickness`、`Stretch`。

### 3. 状态灯：一张图换五种颜色

状态类图标是单色的，运行态按实时状态绑画刷就行：

```xml
<Grid>
  <!-- 轮廓 -->
  <Path Data="{StaticResource Icon.Status.Run}"
        Style="{StaticResource IconPath}"
        Stroke="{Binding RunState, Converter={StaticResource StateToBrush}}" />
  <!-- 灯芯（实心部分，仅带 .Fill 的资源需要） -->
  <Path Data="{StaticResource Icon.Status.Run.Fill}"
        Fill="{Binding RunState, Converter={StaticResource StateToBrush}}" />
</Grid>
```

状态 → 画刷对照（取自 `Palette.xaml`）：

| 状态 | 画刷 | 色值 |
|---|---|---|
| 运行 / 正常 | `SuccessBrush` | `#1B8A4B` |
| 预警 / 待机 | `WarningBrush` | `#E8890C` |
| 报警 / 故障 | `DangerBrush` | `#C62828` |
| 停止 / 未使能 | `OffBrush` | `#D8E0EA` |
| 离线 / 禁用 | `MutedBrush` | `#8A95A5` |
| 常规图标 | `PrimaryBrush` | `#0B3D91` |

> 设备类图标用 `Style="{StaticResource IconPathLarge}"`。

---

## 二、图标清单

### 导航与功能 `icons/nav/`

| 文件 | 资源键 | 含义 | 对应页面 |
|---|---|---|---|
| `run.svg` | `Icon.Nav.Run` | 仪表盘 | RunView 运行首页 |
| `param.svg` | `Icon.Nav.Param` | 滑块 | ParamView 参数设置 |
| `manual.svg` | `Icon.Nav.Manual` | 手 | ManualView 手动操作 |
| `io.svg` | `Icon.Nav.Io` | 芯片 | IoView IO 点位监控 |
| `alarm.svg` | `Icon.Nav.Alarm` | 铃铛 | AlarmView 报警记录 |
| `history.svg` | `Icon.Nav.History` | 记录表（带时刻标记） | HistoryView 历史记录 |
| `recipe.svg` | `Icon.Nav.Recipe` | 烧瓶 | RecipeView 配方管理 |
| `teach.svg` | `Icon.Nav.Teach` | 十字准星 | TeachView 示教点位 |
| `settings.svg` | `Icon.Nav.Settings` | 齿轮 | SettingsView 系统设置 |
| `user.svg` | `Icon.Nav.User` | 人像 | 用户 |
| `lock.svg` | `Icon.Nav.Lock` | 挂锁 | LoginDialog 登录 / 权限 |

### 状态与报警灯 `icons/status/`（单色，随状态换色）

| 文件 | 资源键 | 含义 | 含实心部件 |
|---|---|---|---|
| `run.svg` | `Icon.Status.Run` | 运行中 | ✓ |
| `stop.svg` | `Icon.Status.Stop` | 已停止 | ✓ |
| `idle.svg` | `Icon.Status.Idle` | 待机 / 暂停 | |
| `alarm.svg` | `Icon.Status.Alarm` | 报警 | ✓ |
| `fault.svg` | `Icon.Status.Fault` | 故障 | |
| `estop.svg` | `Icon.Status.Estop` | 急停 | ✓ |
| `maintenance.svg` | `Icon.Status.Maintenance` | 维护 / 检修 | |
| `comm-ok.svg` | `Icon.Status.CommOk` | 通讯正常 | ✓ |
| `comm-lost.svg` | `Icon.Status.CommLost` | 通讯断开 | ✓ |
| `offline.svg` | `Icon.Status.Offline` | 离线 / 停机 | |

### 操作按钮 `icons/action/`

| 文件 | 资源键 | 含义 | 含实心部件 |
|---|---|---|---|
| `start.svg` | `Icon.Action.Start` | 启动 | |
| `stop.svg` | `Icon.Action.Stop` | 停止 | |
| `reset.svg` | `Icon.Action.Reset` | 复位 | |
| `pause.svg` | `Icon.Action.Pause` | 暂停 | ✓ |
| `estop.svg` | `Icon.Action.Estop` | 急停 | ✓ |
| `auto.svg` | `Icon.Action.Auto` | 自动模式 | |
| `manual.svg` | `Icon.Action.Manual` | 手动模式 | |
| `forward.svg` | `Icon.Action.Forward` | 正转 / 前进 | |
| `reverse.svg` | `Icon.Action.Reverse` | 反转 / 后退 | |
| `home.svg` | `Icon.Action.Home` | 回原点 | ✓ |

### 设备工艺图形 `icons/device/`（32px 网格，配 `IconPathLarge`）

| 文件 | 资源键 | 含义 | 含实心部件 |
|---|---|---|---|
| `motor.svg` | `Icon.Device.Motor` | 电机 | |
| `cylinder.svg` | `Icon.Device.Cylinder` | 气缸 | |
| `valve.svg` | `Icon.Device.Valve` | 阀门 | |
| `pump.svg` | `Icon.Device.Pump` | 泵 | |
| `fan.svg` | `Icon.Device.Fan` | 风机 | ✓ |
| `conveyor.svg` | `Icon.Device.Conveyor` | 传送带 | |
| `sensor.svg` | `Icon.Device.Sensor` | 光电传感器 | |
| `heater.svg` | `Icon.Device.Heater` | 加热元件 | |
| `tank.svg` | `Icon.Device.Tank` | 料罐 / 储罐 | |
| `robot.svg` | `Icon.Device.Robot` | 机械手 / 取放机构 | |

---

## 三、改图 / 加图

图标全部由 `tools/icons_*.py` 里的**图元列表**定义，SVG、XAML、预览页三种产物同源。改完重跑：

```bash
python tools/gen_all.py
```

图元语法（坐标用图标自定义网格，导航/状态/按钮是 24×24，设备是 32×32）：

| 图元 | 写法 | 说明 |
|---|---|---|
| 圆 | `("circle", cx, cy, r)` | 描边圆 |
| 实心圆 | `("dot", cx, cy, r)` | 填充，作为灯芯 |
| 直线 | `("line", x1, y1, x2, y2)` | |
| 圆角矩形 | `("rect", x, y, w, h, rx)` | 描边 |
| 实心矩形 | `("frect", x, y, w, h, rx)` | 填充 |
| 路径 | `("path", "M… a… l… z")` | 标准 SVG path，多条子路径写成 `M…M…` |

新增一张图只要在对应字典里加一条，`gen_all.py` 会自动产出 `.svg`、XAML 资源键和预览页卡片。

---

## 四、后续可扩展方向

- **设备类补充**：三通阀、变频器、伺服轴、扫码枪、安全光栅、AGV
- **导航类补充**：生产报表、趋势曲线、用户权限、数据导出
- **动画态**：运行灯呼吸、报警灯闪烁（用 `Storyboard` 驱动 `Opacity` / 画刷即可）
- **打包**：如需 PNG 位图（触摸屏大尺寸展示），可从 SVG 批量导出 96 / 128 / 256px
