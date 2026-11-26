using Microsoft.Extensions.DependencyInjection;
using Serilog;
using PlcHmi.App.ViewModels;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.App;

/// <summary>
/// <c>--smoke</c> 的「现场行为」自检（partial）：验证"界面动作真的通到了 PLC 与其它页面"，
/// 而不只是"页面画出来了"。
///
/// <para><b>为什么要单独一段：</b>这两条都是 2026-10-15 现场反馈的缺陷，且都属于
/// "看代码看不出、必须跑一遍"的类型：</para>
/// <list type="number">
///   <item>标定页的手动使能原先没接共享服务——点了开关，别的页看不到、PLC 的 M666 也不置位；</item>
///   <item>动作按钮的状态原先只在<b>进入页面</b>时回读一次——PLC 侧复位后必须离开再回来才看得到。</item>
/// </list>
///
/// <para>自检跑在模拟器上，但读写的都是真实的 <see cref="IModbusClient"/>，
/// 验证的是"界面动作 → 共享状态 → PLC 落位"这条链本身。</para>
/// </summary>
public partial class App
{
    /// <summary>同步交互自检 + 需要 PLC 往返的现场行为自检。</summary>
    private async Task<int> VerifyCommitMutualAsync() => VerifyCommitMutual() + await VerifyFieldRowAsAsync();

    private async Task<int> VerifyFieldRowAsAsync()
    {
        if (_host is null)
        {
            return 0;
        }

        var Failed = 0;

        try
        {
            // 门禁不能依赖"现场 PLC 在不在线"（开发机、CI 上都没接设备），
            // 而这几条要验证的恰恰是"界面动作有没有真的落到 PLC"——切到本地模拟从站最确定。
            // 模拟开关刻意不落盘、不跨重启记忆（见运行模式管理器），自检不会污染现场。
            var mode = _host.Services.GetService<IRunModeManager>();

            if (mode is null || !mode.CanAtRuntimeSwitch)
            {
                Log.Warning("现场行为自检跳过：当前协议不支持运行期切换运行模式");
                return 0;
            }

            await mode.SwitchAsync(RunMode.SimulationPlc);
            await WaitConnectAsync();

            Failed += await VerifyEnableTotalShareAndM666Async();
            Failed += await VerifyActionButtonAutoFollowAsync();
        }
        catch (Exception exception)
        {
            Failed++;
            Log.Error(exception, "现场行为自检异常");
        }

        return Failed;
    }

    /// <summary>等连接建立（切到模拟模式后连接由后台重连线程拉起）。</summary>
    private async Task WaitConnectAsync()
    {
        var Client = _host!.Services.GetRequiredService<IModbusClient>();
        var truncateStop = Environment.TickCount64 + 5000;

        while (!Client.IsConnected && Environment.TickCount64 < truncateStop)
        {
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// 等条件成立（超时返回 false）。
    ///
    /// <para><b>为什么不能用"固定睡 N 秒"做断言依据：</b>动作按钮的周期回读是 2 秒一次，
    /// 原来这里睡 3 秒——只剩半个周期的余量。CI（以及现场低配机）上采集线程被挤占、
    /// 或 UI 派发排队时，一次周期就被错过，自检报"周期回读没起作用"的假缺陷。
    /// 改成"盯住目标状态、最多等 N 秒"，既不会误判，也不会让正常情况变慢。</para>
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;

        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            await Task.Delay(100);
        }

        return true;
    }

    /// <summary>同上，但条件本身要读写 PLC（链路未就绪时由调用方用 IsConnected 短路）。</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int timeoutMilliseconds = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;

        while (true)
        {
            if (await condition().ConfigureAwait(true))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            await Task.Delay(100);
        }
    }

    /// <summary>
    /// ① 手动使能：标定页的开关 → 共享状态（三页同步）→ PLC 的 M666 置位 / 复位。
    /// </summary>
    private async Task<int> VerifyEnableTotalShareAndM666Async()
    {
        var Manual = _host!.Services.GetRequiredService<IManualWidgetService>();
        var Client = _host.Services.GetRequiredService<IModbusClient>();
        var Calibration = _host.Services.GetRequiredService<CalibrationViewModel>();
        var Teach = _host.Services.GetRequiredService<ScrewTeachViewModel>();

        var Issue = 0;

        // 从"未使能"起步（事件回写是经界面派发排队的，等它跑完）
        Calibration.IsManualEnabled = false;
        await WaitUntilAsync(() => !Manual.IsEnabled, 5000);

        Calibration.IsManualEnabled = true;
        await WaitUntilAsync(() => Manual.IsEnabled && Teach.IsManualEnabled);

        if (!Manual.IsEnabled)
        {
            Issue++;
            Log.Error("现场行为自检失败：标定页打开使能后，共享服务状态仍为关闭");
        }

        if (!Teach.IsManualEnabled)
        {
            Issue++;
            Log.Error("现场行为自检失败：标定页打开使能后锁附示教页未同步——使能没有共享");
        }

        // 刚切到模拟从站时，头几条读写可能落空（链路还在建），盯住目标状态重试到超时
        var set = await WaitUntilAsync(
            async () => Client.IsConnected && (await Client.ReadCoilAsync(ManualEnableRegister.Coil, 1))[0]);

        if (!set)
        {
            Issue++;
            Log.Error("现场行为自检失败：使能打开后 PLC 的 M{Coil} 未置位", ManualEnableRegister.Coil);
        }

        Calibration.IsManualEnabled = false;
        await WaitUntilAsync(() => !Manual.IsEnabled && !Teach.IsManualEnabled, 5000);

        var reset = await WaitUntilAsync(
            async () => Client.IsConnected && !(await Client.ReadCoilAsync(ManualEnableRegister.Coil, 1))[0]);

        if (Manual.IsEnabled || Teach.IsManualEnabled || !reset)
        {
            Issue++;
            Log.Error("现场行为自检失败：关闭使能后未复位（服务={Shared} 示教页={Teach} M{Coil} 仍为 1）",
                Manual.IsEnabled, Teach.IsManualEnabled, ManualEnableRegister.Coil);
        }

        if (Issue == 0)
        {
            Log.Information("现场行为自检通过：标定页使能 → 三页共享 + PLC M{Coil} 置位/复位",
                ManualEnableRegister.Coil);
        }

        return Issue;
    }

    /// <summary>
    /// ② 动作按钮自动跟随：上位机置位 → PLC 复位后，界面<b>不离开页面</b>就能跟上
    /// （由主视图模型对当前页的周期回读完成，见 <see cref="IActionButtonPage"/>）。
    /// </summary>
    private async Task<int> VerifyActionButtonAutoFollowAsync()
    {
        var master = _host!.Services.GetRequiredService<MasterViewModel>();
        var Client = _host.Services.GetRequiredService<IModbusClient>();
        var Parser = _host.Services.GetRequiredService<IAddressParser>();
        var Teach = _host.Services.GetRequiredService<ScrewTeachViewModel>();

        var Entry = master.NavigationEntryCollection.FirstOrDefault(Item => Item.Title == "锁附示教");

        if (Entry is null)
        {
            Log.Warning("现场行为自检跳过：导航里没有锁附示教页");
            return 0;
        }

        // 周期回读只作用于"当前页面"，所以先切到有动作按钮的示教页
        master.SelectedNavigationEntry = Entry;
        await Task.Delay(400);

        if (Teach.ActionButtonCollection.Count == 0)
        {
            Log.Warning("现场行为自检跳过：锁附示教页没有动作按钮");
            return 0;
        }

        var Button = Teach.ActionButtonCollection[0];

        if (!Parser.TryParse(Button.Definition.Address, out var addressValue) || addressValue is null)
        {
            Log.Error("现场行为自检失败：动作按钮 {Name} 的地址「{Address}」无法解析",
                Button.Name, Button.Definition.Address);
            return 1;
        }

        var Issue = 0;

        // 上位机置位 → 回读应反映为已激活
        await Client.WriteSingleCoilAsync(addressValue.ModbusAddress, true);
        await Teach.RefreshActionButtonStateAsync();
        await WaitUntilAsync(() => Button.Activated, 3000);

        if (!Button.Activated)
        {
            Issue++;
            Log.Error("现场行为自检失败：PLC 侧线圈置位后，动作按钮 {Name} 未显示已激活", Button.Name);
        }

        // PLC 侧复位：界面不做任何操作，只等周期回读自己跟上（回读间隔 2 秒，给足 15 秒）
        await Client.WriteSingleCoilAsync(addressValue.ModbusAddress, false);
        await WaitUntilAsync(() => !Button.Activated);

        if (Button.Activated)
        {
            Issue++;
            Log.Error("现场行为自检失败：PLC 复位后动作按钮 {Name} 仍显示已激活——周期回读没起作用",
                Button.Name);
        }
        else
        {
            Log.Information("现场行为自检通过：动作按钮 {Name} 在 PLC 复位后自动跟上（无需离开页面）",
                Button.Name);
        }

        return Issue;
    }
}
