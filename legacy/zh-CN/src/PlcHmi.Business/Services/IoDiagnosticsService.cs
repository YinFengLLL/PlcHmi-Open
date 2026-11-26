using Microsoft.Extensions.Logging;
using 上位机.核心.抽象;
using 上位机.核心.枚举;

namespace 上位机.业务.服务;

/// <summary>
/// IO 通信诊断实现：X / Y 段各用 FC01 与 FC02 读一次，并排呈现。
///
/// <para><b>为什么按功能码对照而不是只报"读失败"：</b>2026-10-17 现场第二次报障的真因就是
/// "X 是物理输入、本该用 FC02 读，却被放在线圈表用 FC01 读"——连接正常、无异常、无告警，
/// 只是读到了同一段地址上无关的位。只有把两个功能码的读数并排摆出来，这种错位才看得见。</para>
/// </summary>
public sealed class IO诊断服务 : IO诊断服务接口
{
    private readonly Modbus客户端接口 _客户端;
    private readonly ILogger<IO诊断服务> _日志;

    public IO诊断服务(Modbus客户端接口 客户端, ILogger<IO诊断服务> 日志)
    {
        _客户端 = 客户端;
        _日志 = 日志;
    }

    /// <inheritdoc />
    public async Task<IO诊断报告> 异步诊断(
        int X基址, int Y基址, int 位数 = 16, CancellationToken 取消令牌 = default)
    {
        var 项集合 = new List<IO诊断项>(4);

        项集合.Add(await 探测("X 区 · 线圈", "FC01", X基址, 位数, Modbus区域.线圈, 取消令牌));
        项集合.Add(await 探测("X 区 · 离散输入", "FC02", X基址, 位数, Modbus区域.离散输入, 取消令牌));
        项集合.Add(await 探测("Y 区 · 线圈", "FC01", Y基址, 位数, Modbus区域.线圈, 取消令牌));
        项集合.Add(await 探测("Y 区 · 离散输入", "FC02", Y基址, 位数, Modbus区域.离散输入, 取消令牌));

        var 结论 = 生成结论(项集合);

        _日志.LogInformation(
            "IO 通信诊断完成：{Summary}；结论：{Conclusion}",
            string.Join("；", 项集合.Select(项 => $"{项.标题}={项.结果文本}")), 结论);

        return new IO诊断报告 { 项集合 = 项集合, 结论 = 结论 };
    }

    /// <summary>读一段位，无论成败都转成一行（失败也要让人看见，不能静默跳过）。</summary>
    private async Task<IO诊断项> 探测(
        string 标题, string 功能码, int 地址, int 位数, Modbus区域 区域, CancellationToken 取消令牌)
    {
        try
        {
            var 值列表 = 区域 is Modbus区域.离散输入
                ? await _客户端.异步读取离散输入(地址, 位数, 取消令牌).ConfigureAwait(false)
                : await _客户端.异步读取线圈(地址, 位数, 取消令牌).ConfigureAwait(false);

            var 置一位数 = 0;
            var 构建器 = new System.Text.StringBuilder(位数);

            // 高位在左书写：与 PLC 位号顺序一致（地址 + 序号 从左到右递增）
            for (var 序号 = 值列表.Length - 1; 序号 >= 0; 序号--)
            {
                var 值 = 序号 < 位数 && 值列表[序号];
                构建器.Append(值 ? '1' : '0');

                if (值)
                {
                    置一位数++;
                }
            }

            return new IO诊断项(标题, 功能码, 地址, true, 构建器.ToString(), 置一位数, string.Empty);
        }
        catch (Exception 异常)
        {
            // 诊断的目的就是收集失败：这里绝不能把异常抛给界面
            return new IO诊断项(标题, 功能码, 地址, false, string.Empty, 0, 异常.Message);
        }
    }

    /// <summary>按"哪个功能码读到了 1"给出结论与下一步操作。</summary>
    private static string 生成结论(IReadOnlyList<IO诊断项> 项集合)
    {
        var 构建器 = new System.Text.StringBuilder();

        构建器.Append(判断一侧(项集合, "X", 0, 1));
        构建器.Append(判断一侧(项集合, "Y", 2, 3));

        构建器.Append("配置方法：用文本编辑器打开 config/io-points.json，把「元」里对应的 "
                      + "X区域 / Y区域 配成上面结论中的功能码，保存后重启程序生效。");

        return 构建器.ToString();
    }

    /// <summary>判断某一侧（X 或 Y）的物理 IO 落在哪个功能码上。</summary>
    private static string 判断一侧(IReadOnlyList<IO诊断项> 项集合, string 侧名, int 线圈序号, int 离散序号)
    {
        var 线圈 = 项集合[线圈序号];
        var 离散 = 项集合[离散序号];

        var 线圈有 = 线圈.成功 && 线圈.置一位数 > 0;
        var 离散有 = 离散.成功 && 离散.置一位数 > 0;

        if (线圈有 && 离散有)
        {
            return $"{侧名} 区：两个功能码都读到信号，无法自动判定——请以 PLC 地址分配表为准，"
                   + $"在 io-points.json 元里把 {侧名}区域 显式配为 \"线圈\" 或 \"离散输入\"。";
        }

        if (离散有)
        {
            return $"{侧名} 区：物理状态在【离散输入（FC02）】——请把 {侧名}区域 配为 \"离散输入\"。";
        }

        if (线圈有)
        {
            return $"{侧名} 区：物理状态在【线圈（FC01）】——请把 {侧名}区域 配为 \"线圈\"。";
        }

        if (!线圈.成功 || !离散.成功)
        {
            return $"{侧名} 区：两个功能码都读不到（{摘要(线圈)} / {摘要(离散)}）——"
                   + "请检查 PLC 连接、从站号与该地址段是否在从站的映射范围内。";
        }

        return $"{侧名} 区：两个功能码都读到全 0——若现场这些点实际有输入/输出，"
               + "请核对地址（X/Y 按八进制编号：X10 = 第 8 点）与基址配置。";
    }

    private static string 摘要(IO诊断项 项) =>
        项.成功 ? "读取成功但全 0" : $"读取失败（{项.错误消息}）";
}
