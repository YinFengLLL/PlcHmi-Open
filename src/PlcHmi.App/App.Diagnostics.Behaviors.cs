using Microsoft.Extensions.DependencyInjection;
using Serilog;
using 上位机.应用.视图模型;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.应用;

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
/// <para>自检跑在模拟器上，但读写的都是真实的 <see cref="Modbus客户端接口"/>，
/// 验证的是"界面动作 → 共享状态 → PLC 落位"这条链本身。</para>
/// </summary>
public partial class 应用
{
    /// <summary>同步交互自检 + 需要 PLC 往返的现场行为自检。</summary>
    private async Task<int> 异步验证交互() => 验证交互() + await 异步验证现场行为();

    private async Task<int> 异步验证现场行为()
    {
        if (_主机 is null)
        {
            return 0;
        }

        var 失败 = 0;

        try
        {
            // 门禁不能依赖"现场 PLC 在不在线"（开发机、CI 上都没接设备），
            // 而这几条要验证的恰恰是"界面动作有没有真的落到 PLC"——切到本地模拟从站最确定。
            // 模拟开关刻意不落盘、不跨重启记忆（见运行模式管理器），自检不会污染现场。
            var 模式 = _主机.Services.GetService<运行模式管理器接口>();

            if (模式 is null || !模式.可在运行期切换)
            {
                Log.Warning("现场行为自检跳过：当前协议不支持运行期切换运行模式");
                return 0;
            }

            await 模式.异步切换(运行模式.模拟PLC);
            await 异步等待连接();

            失败 += await 异步验证使能共享与M666();
            失败 += await 异步验证动作按钮自动跟随();
        }
        catch (Exception 异常)
        {
            失败++;
            Log.Error(异常, "现场行为自检异常");
        }

        return 失败;
    }

    /// <summary>等连接建立（切到模拟模式后连接由后台重连线程拉起）。</summary>
    private async Task 异步等待连接()
    {
        var 客户端 = _主机!.Services.GetRequiredService<Modbus客户端接口>();
        var 截止 = Environment.TickCount64 + 5000;

        while (!客户端.是否已连接 && Environment.TickCount64 < 截止)
        {
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// ① 手动使能：标定页的开关 → 共享状态（三页同步）→ PLC 的 M666 置位 / 复位。
    /// </summary>
    private async Task<int> 异步验证使能共享与M666()
    {
        var 手动 = _主机!.Services.GetRequiredService<手动控件服务接口>();
        var 客户端 = _主机.Services.GetRequiredService<Modbus客户端接口>();
        var 标定 = _主机.Services.GetRequiredService<标定视图模型>();
        var 示教 = _主机.Services.GetRequiredService<锁附示教视图模型>();

        var 问题 = 0;

        // 从"未使能"起步（事件回写是经界面派发排队的，等它跑完）
        标定.是否手动启用 = false;
        await Task.Delay(400);

        标定.是否手动启用 = true;
        await Task.Delay(600);

        if (!手动.是否启用)
        {
            问题++;
            Log.Error("现场行为自检失败：标定页打开使能后，共享服务状态仍为关闭");
        }

        if (!示教.是否手动启用)
        {
            问题++;
            Log.Error("现场行为自检失败：标定页打开使能后锁附示教页未同步——使能没有共享");
        }

        var 置位后 = await 客户端.异步读取线圈(手动使能寄存器.线圈, 1);

        if (!置位后[0])
        {
            问题++;
            Log.Error("现场行为自检失败：使能打开后 PLC 的 M{Coil} 未置位", 手动使能寄存器.线圈);
        }

        标定.是否手动启用 = false;
        await Task.Delay(600);

        var 复位后 = await 客户端.异步读取线圈(手动使能寄存器.线圈, 1);

        if (手动.是否启用 || 示教.是否手动启用 || 复位后[0])
        {
            问题++;
            Log.Error("现场行为自检失败：关闭使能后未复位（服务={Shared} 示教页={Teach} M{Coil}={Coil}）",
                手动.是否启用, 示教.是否手动启用, 手动使能寄存器.线圈, 复位后[0]);
        }

        if (问题 == 0)
        {
            Log.Information("现场行为自检通过：标定页使能 → 三页共享 + PLC M{Coil} 置位/复位",
                手动使能寄存器.线圈);
        }

        return 问题;
    }

    /// <summary>
    /// ② 动作按钮自动跟随：上位机置位 → PLC 复位后，界面<b>不离开页面</b>就能跟上
    /// （由主视图模型对当前页的周期回读完成，见 <see cref="动作按钮页接口"/>）。
    /// </summary>
    private async Task<int> 异步验证动作按钮自动跟随()
    {
        var 主 = _主机!.Services.GetRequiredService<主视图模型>();
        var 客户端 = _主机.Services.GetRequiredService<Modbus客户端接口>();
        var 解析器 = _主机.Services.GetRequiredService<地址解析器接口>();
        var 示教 = _主机.Services.GetRequiredService<锁附示教视图模型>();

        var 条目 = 主.导航条目集合.FirstOrDefault(项 => 项.标题 == "锁附示教");

        if (条目 is null)
        {
            Log.Warning("现场行为自检跳过：导航里没有锁附示教页");
            return 0;
        }

        // 周期回读只作用于"当前页面"，所以先切到有动作按钮的示教页
        主.选中导航条目 = 条目;
        await Task.Delay(400);

        if (示教.动作按钮集合.Count == 0)
        {
            Log.Warning("现场行为自检跳过：锁附示教页没有动作按钮");
            return 0;
        }

        var 按钮 = 示教.动作按钮集合[0];

        if (!解析器.尝试解析(按钮.定义.地址, out var 地址值) || 地址值 is null)
        {
            Log.Error("现场行为自检失败：动作按钮 {Name} 的地址「{Address}」无法解析",
                按钮.名称, 按钮.定义.地址);
            return 1;
        }

        var 问题 = 0;

        // 上位机置位 → 回读应反映为已激活
        await 客户端.异步写入单个线圈(地址值.Modbus地址, true);
        await 示教.异步刷新动作按钮状态();
        await Task.Delay(200);

        if (!按钮.已激活)
        {
            问题++;
            Log.Error("现场行为自检失败：PLC 侧线圈置位后，动作按钮 {Name} 未显示已激活", 按钮.名称);
        }

        // PLC 侧复位：界面不做任何操作，只等周期回读自己跟上
        await 客户端.异步写入单个线圈(地址值.Modbus地址, false);
        await Task.Delay(3000);

        if (按钮.已激活)
        {
            问题++;
            Log.Error("现场行为自检失败：PLC 复位后动作按钮 {Name} 仍显示已激活——周期回读没起作用",
                按钮.名称);
        }
        else
        {
            Log.Information("现场行为自检通过：动作按钮 {Name} 在 PLC 复位后自动跟上（无需离开页面）",
                按钮.名称);
        }

        return 问题;
    }
}
