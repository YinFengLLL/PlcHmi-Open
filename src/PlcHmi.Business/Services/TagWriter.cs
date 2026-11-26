using Microsoft.Extensions.Logging;
using 上位机.通信.Modbus;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.异常;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 点位写入器。把“按名称写值”统一转换为地址解析 + 编码 + Modbus 写操作。
/// <para>所有写命令都应经过这里，以便统一做权限校验、范围校验与串行化。</para>
/// </summary>
public sealed class 标签写入器
{
    private readonly Modbus客户端接口 _客户端;
    private readonly 标签仓储接口 _标签;
    private readonly 地址解析器接口 _解析器;
    private readonly ILogger<标签写入器> _日志;

    /// <summary>
    /// D 字内位「读-改-写」的进程内串行锁。
    /// <para>Modbus 没有写字内位的功能码，只能 读字 → 改位 → 写回；
    /// 两次上位机写命令并发交错会把彼此的位改回去，必须串行化。
    /// 注意这只能防上位机自身并发，与 PLC 程序并发写同一 D 字仍有竞态（见文档）。</para>
    /// </summary>
    private static readonly SemaphoreSlim 字位写锁 = new(1, 1);

    public 标签写入器(
        Modbus客户端接口 客户端,
        标签仓储接口 标签列表,
        地址解析器接口 解析器,
        ILogger<标签写入器> 日志)
    {
        _客户端 = 客户端;
        _标签 = 标签列表;
        _解析器 = 解析器;
        _日志 = 日志;
    }

    /// <summary>按点位名写入布尔值。</summary>
    public async Task 异步写入布尔(string 标签名称, bool 值, CancellationToken 取消令牌 = default)
    {
        var (标签项, 地址值) = 解析可写(标签名称);

        if (地址值.是字内位)
        {
            await 异步写入字内位核心(地址值, 值, 取消令牌).ConfigureAwait(false);
        }
        else
        {
            校验线圈区域(地址值, 标签名称);
            await _客户端.异步写入单个线圈(地址值.Modbus地址, 值, 取消令牌).ConfigureAwait(false);
        }

        _日志.LogDebug("写入 {Tag} = {Value}", 标签项.名称, 值);
    }

    /// <summary>
    /// 写线圈前的最后一道防线（R4）：上位机只允许置位 <b>M 区内部继电器</b>，
    /// Y 物理输出由 PLC 程序联锁决定。此前 <see cref="异步写入线圈地址"/> 有 M 区校验，
    /// 而按点位名写入的路径没有——tags.json 被手工改错即可越权驱动物理输出。
    /// </summary>
    private static void 校验线圈区域(PLC地址 地址值, string 标签名称)
    {
        if (地址值.元素 != 软元素.M)
        {
            throw new InvalidOperationException(
                $"点位「{标签名称}」解析为 {地址值.元素} 区，拒绝写入。"
                + "上位机只允许操作 M 区内部继电器，由 PLC 程序决定何时驱动 Y 输出。");
        }
    }

    /// <summary>
    /// 按<b>软元件地址</b>直接写线圈，不要求该地址预先在 <c>tags.json</c> 里定义。
    ///
    /// <para>给「配置驱动」的点位用：参数页的按钮表由管理员在界面上增删，
    /// 地址是运行时才知道的，不可能提前写进 tags.json。</para>
    ///
    /// <para><b>仍然强制 M 区</b>：这是"上位机不直接驱动 Y 输出"这条硬约束的最后一道防线，
    /// 即使配置文件被手工改错，也不会执行到真实输出点。</para>
    /// </summary>
    public async Task 异步写入线圈地址(
        string 地址值, bool 值, CancellationToken 取消令牌 = default)
    {
        var 已解析 = _解析器.解析(地址值);

        if (已解析.元素 != 软元素.M)
        {
            throw new InvalidOperationException(
                $"「{地址值}」必须是 M 区地址（当前解析为 {已解析.元素} 区）。"
                + "上位机只允许操作内部继电器，由 PLC 程序决定何时驱动 Y 输出。");
        }

        await _客户端.异步写入单个线圈(已解析.Modbus地址, 值, 取消令牌).ConfigureAwait(false);
        _日志.LogInformation("按地址写入线圈 {Address} = {Value}", 地址值, 值);
    }

    /// <summary>
    /// 按<b>软元件地址</b>直接写保持寄存器（16 位），不要求该地址预先在 <c>tags.json</c> 里定义。
    ///
    /// <para>给「配置驱动」的点位用：延时表的地址由管理员在界面上配置，运行时才知道。</para>
    ///
    /// <para><b>强制 D 区</b>：延时值属于 PLC 参数字，写错区（如线圈）没有意义且危险。</para>
    /// </summary>
    public async Task 异步写入寄存器地址(
        string 地址值, int 值, CancellationToken 取消令牌 = default)
    {
        var 已解析 = _解析器.解析(地址值);

        if (已解析.元素 != 软元素.D)
        {
            throw new InvalidOperationException(
                $"「{地址值}」必须是 D 区地址（当前解析为 {已解析.元素} 区）。");
        }

        var 寄存器项 = checked((ushort)值);   // 超出 0–65535 立刻抛，绝不静默截断
        await _客户端.异步写入单个寄存器(已解析.Modbus地址, 寄存器项, 取消令牌)
            .ConfigureAwait(false);
        _日志.LogInformation("按地址写入寄存器 {Address} = {Value}", 地址值, 寄存器项);
    }

    /// <summary>
    /// 按<b>软元件地址</b>直接写 <b>32 位整数（DINT）</b>，占 2 个连续寄存器（字序 CDAB：低字在前），
    /// 不要求该地址预先在 <c>tags.json</c> 里定义。
    ///
    /// <para><b>2026-10-16 现场要求：</b>参数页延时表默认按 DINT 存毫秒——16 位整数毫秒
    /// 上限只有 65.535 S（65535 ms），实际工位动辄十几秒到几分钟的等待会直接溢出。</para>
    ///
    /// <para><b>字序走 <c>寄存器编解码器</c>（红线 R1）</b>：调用方不得自行拆高低字；
    /// 与轴速度 Float32 参数、机种快照用的是同一份实现。</para>
    ///
    /// <para><b>强制 D 区</b>：延时值属于 PLC 参数字，写错区（如线圈）没有意义且危险。</para>
    /// </summary>
    public async Task 异步写入寄存器地址32(
        string 地址值, int 值, CancellationToken 取消令牌 = default)
    {
        var 已解析 = _解析器.解析(地址值);

        if (已解析.元素 != 软元素.D)
        {
            throw new InvalidOperationException(
                $"「{地址值}」必须是 D 区地址（当前解析为 {已解析.元素} 区）。");
        }

        var 寄存器列表 = 寄存器编解码器.编码(标签数据类型.Int32, 值, 字顺序.小字节序);

        await _客户端.异步写入多个寄存器(已解析.Modbus地址, 寄存器列表, 取消令牌)
            .ConfigureAwait(false);

        _日志.LogInformation(
            "按地址写入 32 位寄存器 {Address} = {Value}（占 {Count} 字，低字在前）",
            地址值, 值, 寄存器列表.Length);
    }

    /// <summary>
    /// 按<b>软元件地址</b>直接读 <b>32 位整数（DINT）</b>（占 2 个连续寄存器，字序 CDAB：低字在前），
    /// 不要求该地址预先在 <c>tags.json</c> 里定义。
    ///
    /// <para><b>2026-10-17 现场要求：</b>参数页延时区要<b>回读</b>——延时值由 PLC 程序也可能改写
    /// （切换机种、HMI 旁的操作面板），界面必须显示 PLC 当下真实值，而不是本地配置值。</para>
    ///
    /// <para><b>字序走 <c>寄存器编解码器</c>（红线 R1）</b>：与写入侧 <see cref="异步写入寄存器地址32"/>
    /// 用的是同一份实现，读回与写出严格互逆。</para>
    ///
    /// <para><b>强制 D 区</b>：与写入侧同口径。</para>
    /// </summary>
    public async Task<int> 异步读取寄存器地址32(
        string 地址值, CancellationToken 取消令牌 = default)
    {
        var 已解析 = _解析器.解析(地址值);

        if (已解析.元素 != 软元素.D)
        {
            throw new InvalidOperationException(
                $"「{地址值}」必须是 D 区地址（当前解析为 {已解析.元素} 区）。");
        }

        var 寄存器列表 = await _客户端
            .异步读取保持寄存器(已解析.Modbus地址, 2, 取消令牌)
            .ConfigureAwait(false);

        if (寄存器列表.Length < 2)
        {
            throw new InvalidOperationException(
                $"从 {地址值} 读取 2 个字时 PLC 只返回 {寄存器列表.Length} 个，已中止。");
        }

        return 寄存器编解码器.解码(标签数据类型.Int32, 寄存器列表, 字顺序.小字节序) is int 值
            ? 值
            : throw new InvalidOperationException($"解码 {地址值} 的 32 位整数失败。");
    }

    /// <summary>
    /// 按<b>软元件地址</b>直接读一个线圈（布尔），不要求该地址预先在 <c>tags.json</c> 里定义。
    /// <para>给参数页按钮区回读用：按钮状态由 PLC 程序或手动页都可能改写，界面要显示真实状态。</para>
    /// </summary>
    public async Task<bool> 异步读取线圈地址(string 地址值, CancellationToken 取消令牌 = default)
    {
        var 已解析 = _解析器.解析(地址值);

        if (已解析.元素 != 软元素.M)
        {
            throw new InvalidOperationException(
                $"「{地址值}」必须是 M 区线圈地址（当前解析为 {已解析.元素} 区）。");
        }

        var 线圈列表 = await _客户端
            .异步读取线圈(已解析.Modbus地址, 1, 取消令牌)
            .ConfigureAwait(false);

        return 线圈列表.Length > 0 && 线圈列表[0];
    }

    /// <summary>
    /// 写 D/R 区<b>字内位</b>（如 <c>D1210.2</c>）：读 1 字 → 改位 → 写回。
    ///
    /// <para>Modbus 没有写字内位的功能码（汇川位打包：BOOL 占 D 字的一位），
    /// 只能读-改-写。用进程内串行锁防止两条写命令并发交错互相覆盖；
    /// 与 PLC 程序并发写同一 D 字的竞态无法在上位机侧消除，
    /// 需 PLC 侧保证命令字只由上位机写（或走 OPC UA 变量节点直写位）。</para>
    /// </summary>
    private async Task 异步写入字内位核心(PLC地址 地址值, bool 值, CancellationToken 取消令牌)
    {
        await 字位写锁.WaitAsync(取消令牌).ConfigureAwait(false);
        try
        {
            var 寄存器列表 = await _客户端.异步读取保持寄存器(地址值.Modbus地址, 1, 取消令牌)
                .ConfigureAwait(false);

            if (寄存器列表.Length < 1)
            {
                throw new InvalidOperationException(
                    $"写入字内位 {地址值.来源} 失败：读回的寄存器为空。");
            }

            var 字值 = 寄存器列表[0];
            var 位掩码 = (ushort)(1 << 地址值.位序号);
            var 新字值 = (ushort)(值 ? 字值 | 位掩码 : 字值 & ~位掩码);

            // 刻意不做"值相同就跳过"的优化：点动方向命令里"停止"是安全出口，
            // 必须把目标状态真正写下去，不能因为一次读回恰好相符就省掉写。
            await _客户端.异步写入单个寄存器(地址值.Modbus地址, 新字值, 取消令牌)
                .ConfigureAwait(false);
        }
        finally
        {
            字位写锁.Release();
        }

        _日志.LogInformation("写字内位 {Address}（D 字第 {Bit} 位） = {Value}",
            地址值.来源, 地址值.位序号, 值);
    }

    /// <summary>按点位名写入数值（含范围校验）。</summary>
    public async Task 异步写入数值(string 标签名称, double 值, CancellationToken 取消令牌 = default)
    {
        var (标签项, 地址值) = 解析可写(标签名称);
        校验范围(标签项, 值);

        // 显示值还原为原始值
        var 原始值 = 标签项.缩放 != 0 ? (值 - 标签项.偏移) / 标签项.缩放 : 值;

        if (地址值.区域 == Modbus区域.线圈)
        {
            校验线圈区域(地址值, 标签名称);
            await _客户端.异步写入单个线圈(地址值.Modbus地址, 原始值 != 0, 取消令牌).ConfigureAwait(false);
        }
        else
        {
            var 寄存器列表 = 寄存器编解码器.编码(标签项.数据类型, 原始值, 标签项.字顺序);

            if (寄存器列表.Length == 1)
            {
                await _客户端.异步写入单个寄存器(地址值.Modbus地址, 寄存器列表[0], 取消令牌)
                    .ConfigureAwait(false);
            }
            else
            {
                await _客户端.异步写入多个寄存器(地址值.Modbus地址, 寄存器列表, 取消令牌)
                    .ConfigureAwait(false);
            }
        }

        _日志.LogDebug("写入 {Tag} = {Value}{Unit}", 标签项.名称, 值, 标签项.单元 ?? string.Empty);
    }

    /// <summary>短脉冲写命令：置位后延时复位，用于启动/停止这类按钮式命令。</summary>
    public async Task 异步脉冲(string 标签名称, TimeSpan 时长, CancellationToken 取消令牌 = default)
    {
        await 异步写入布尔(标签名称, true, 取消令牌).ConfigureAwait(false);

        try
        {
            await Task.Delay(时长, 取消令牌).ConfigureAwait(false);
        }
        finally
        {
            // 即使取消也要复位，避免命令点一直保持
            await 异步写入布尔(标签名称, false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private (标签定义 标签, PLC地址 地址) 解析可写(string 标签名称)
    {
        var 标签项 = _标签.查找(标签名称)
                  ?? throw new InvalidOperationException($"点位 \"{标签名称}\" 未在 tags.json 中定义。");

        if (!标签项.可写入)
        {
            throw new InvalidOperationException($"点位 \"{标签名称}\" 为只读，不允许写入。");
        }

        var 地址值 = _解析器.解析(标签项.地址);
        return (标签项, 地址值);
    }

    private static void 校验范围(标签定义 标签项, double 值)
    {
        if (标签项.最小.HasValue && 值 < 标签项.最小.Value)
        {
            throw new 标签值超范围异常(标签项.名称, 值, 标签项.最小, 标签项.最大);
        }

        if (标签项.最大.HasValue && 值 > 标签项.最大.Value)
        {
            throw new 标签值超范围异常(标签项.名称, 值, 标签项.最小, 标签项.最大);
        }
    }
}
