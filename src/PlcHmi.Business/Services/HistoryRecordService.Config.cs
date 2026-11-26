using System.Text.Json;
using Microsoft.Extensions.Logging;
using 上位机.业务.配置;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// 历史记录服务的配置部分：校验与读写 <c>config/history-record.json</c>。
///
/// <para><b>校验为什么必须在服务层：</b>配置有两个入口——界面编辑器改的，
/// 和现场直接手改 JSON 的。规则只写在界面里，手改的文件就没人管：
/// 触发位填成字元件、DINT 与下一项地址重叠，都能安静地生效，直到数据失真才被发现。</para>
/// </summary>
public sealed partial class 历史记录服务
{
    /// <inheritdoc />
    public 历史配置校验 校验(历史记录配置 配置项)
    {
        ArgumentNullException.ThrowIfNull(配置项);

        var 错误列表 = new List<string>();

        if (string.IsNullOrWhiteSpace(配置项.标题))
        {
            错误列表.Add("数据体名称不能为空。");
        }

        校验触发地址(配置项, 错误列表);

        if (配置项.保留天数 is < 历史记录配置.最小保留天数 or > 历史记录配置.最大保留天数)
        {
            错误列表.Add($"保留天数必须填 {历史记录配置.最小保留天数}–{历史记录配置.最大保留天数} 天。");
        }

        if (配置项.数据项集合.Count == 0)
        {
            错误列表.Add("至少需要一个数据项（表格至少一列）。");
        }
        else if (配置项.数据项集合.Count > 历史记录配置.最大数据项数量)
        {
            错误列表.Add($"数据项最多 {历史记录配置.最大数据项数量} 个，当前 {配置项.数据项集合.Count} 个。");
        }

        校验数据项(配置项, 错误列表);

        return new 历史配置校验 { 是否有效 = 错误列表.Count == 0, 错误集合 = 错误列表 };
    }

    private void 校验触发地址(历史记录配置 配置项, List<string> 错误列表)
    {
        if (string.IsNullOrWhiteSpace(配置项.触发地址))
        {
            错误列表.Add("触发位地址不能为空（M 区位地址，如 M700）。");
            return;
        }

        if (!_解析器.尝试解析(配置项.触发地址.Trim(), out var 触发位) || 触发位 is null)
        {
            错误列表.Add($"触发位「{配置项.触发地址}」不是有效的软元件地址。");
            return;
        }

        if (触发位.元素 != 软元素.M)
        {
            错误列表.Add($"触发位必须是 M 区（如 M700），当前填的是 {触发位.元素}{触发位.逻辑序号}。");
        }
    }

    /// <summary>逐项校验名称、类型、小数位与读取地址，并检查地址区间重叠。</summary>
    private void 校验数据项(历史记录配置 配置项, List<string> 错误列表)
    {
        var 已用名称 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var 已占用区间 = new List<(软元素 元素, int 起, int 止, string 名称)>();

        for (var 序号 = 0; 序号 < 配置项.数据项集合.Count; 序号++)
        {
            var 项 = 配置项.数据项集合[序号];
            var 位置 = string.IsNullOrWhiteSpace(项.名称) ? $"第 {序号 + 1} 项" : $"第 {序号 + 1} 项「{项.名称}」";

            if (string.IsNullOrWhiteSpace(项.名称))
            {
                错误列表.Add($"{位置}：名称不能为空。");
            }
            else if (!已用名称.Add(项.名称.Trim()))
            {
                错误列表.Add($"{位置}：名称与其他数据项重复。");
            }

            // 2026-10-24：新增 REAL（32 位浮点）—— 现场有些量 PLC 直接按浮点给，不再放大成整数
            if (项.数据类型 is not (标签数据类型.Int16 or 标签数据类型.Int32 or 标签数据类型.Float32))
            {
                错误列表.Add($"{位置}：数值类型只支持 INT（16 位）、DINT（32 位）或 REAL（32 位浮点）。");
            }

            if (项.小数位 is < 0 or > 历史记录配置.最大小数位)
            {
                错误列表.Add($"{位置}：小数位必须填 0–{历史记录配置.最大小数位}。");
            }

            // ASCII 显示只对 DINT 有意义：INT 只有 2 字节凑不出 4 个字符，
            // REAL 的 4 个字节是浮点位模式、拆成字符全是乱码，
            // 而"配置能存但显示不对"比"当场报错"难查得多（2026-10-17 新增）
            if (项.显示为Ascii && !项.可设Ascii)
            {
                错误列表.Add($"{位置}：ASCII 显示只支持 DINT（32 位 = 4 字节 = 4 个字符），当前类型是 {项.类型文本}。");
            }

            校验数据项地址(项, 位置, 已占用区间, 错误列表);
        }
    }

    private void 校验数据项地址(
        历史数据项 项, string 位置,
        List<(软元素 元素, int 起, int 止, string 名称)> 已占用区间,
        List<string> 错误列表)
    {
        if (string.IsNullOrWhiteSpace(项.地址))
        {
            错误列表.Add($"{位置}：读取地址不能为空。");
            return;
        }

        if (!_解析器.尝试解析(项.地址.Trim(), out var 地址) || 地址 is null)
        {
            错误列表.Add($"{位置}：「{项.地址}」不是有效的软元件地址。");
            return;
        }

        if (地址.位序号 >= 0)
        {
            错误列表.Add($"{位置}：{项.地址} 是位地址，数值必须读字元件（如 D2109）。");
            return;
        }

        if (地址.区域 != Modbus区域.保持寄存器)
        {
            错误列表.Add($"{位置}：读取地址必须是字元件（D / R 区），当前填的是 {地址.元素}{地址.逻辑序号}。");
            return;
        }

        var 字数 = _解析器.获取寄存器数量(项.数据类型);
        var 起 = 地址.Modbus地址;
        var 止 = 起 + 字数 - 1;

        // 区间重叠 = 两列的取值互相"咬"在一起，通常是地址抄错一位。
        // DINT 占 2 字，最容易出现"下一项紧接着写在上一个的低位字上"。
        var 冲突 = 已占用区间.FirstOrDefault(区间 => 区间.元素 == 地址.元素 && 起 <= 区间.止 && 止 >= 区间.起);

        if (冲突.名称 is not null)
        {
            错误列表.Add($"{位置}：地址 {项.地址}（占 {字数} 字）与「{冲突.名称}」的地址区间重叠，请检查地址。");
            return;
        }

        已占用区间.Add((地址.元素, 起, 止, string.IsNullOrWhiteSpace(项.名称) ? 项.地址 : 项.名称));
    }

    /// <inheritdoc />
    public async Task 异步保存配置(历史记录配置 配置项, CancellationToken 取消令牌 = default)
    {
        ArgumentNullException.ThrowIfNull(配置项);

        // 改的是"设备动作被记成什么"，界面隐藏按钮只是防误触，真正的边界在这里（R5）
        _权限.要求(权限.编辑历史记录);

        var 校验项 = 校验(配置项);

        if (!校验项.是否有效)
        {
            throw new InvalidOperationException($"历史记录配置不合法：{校验项.摘要}");
        }

        _配置 = 配置项.克隆();
        await 异步写入配置文件(_配置, 取消令牌).ConfigureAwait(false);

        // 触发位可能已换：重建电平基线，清掉与旧触发位绑定的握手状态，
        // 避免把新位的当前状态误当成一次动作、或把"待复位"带到新位上
        _上次触发 = null;
        _待复位 = false;
        _读取重试中 = false;
        _读取失败已提示 = false;
        _已提示配置错误 = false;

        _日志.LogInformation("历史记录配置已保存：触发位 {Address}，{Count} 个数据项，保留 {Days} 天",
            _配置.触发地址, _配置.数据项集合.Count, _配置.保留天数);

        配置已变更?.Invoke(this, EventArgs.Empty);

        // 保留期可能被改短，顺手清理一次超期文件
        _存储.清理过期(_时钟().Date, _配置.保留天数);
    }

    private async Task 异步加载配置(CancellationToken 取消令牌)
    {
        var 结果值 = await 配置文件加载器
            .异步加载JSON<历史记录配置文件>(配置路径.历史记录配置, JSON配置.读取, _日志, 取消令牌)
            .ConfigureAwait(false);

        _健康?.上报("历史记录配置", 结果值);

        if (结果值.加载结果 is 加载结果分类.缺失)
        {
            _日志.LogInformation("未找到历史记录配置，写入一份默认配置：{Path}", 配置路径.历史记录配置);

            _配置 = 创建默认值();
            await 异步写入配置文件(_配置, 取消令牌).ConfigureAwait(false);
            return;
        }

        _配置 = 结果值.值?.配置 ?? 创建默认值();

        _日志.LogInformation("已加载历史记录配置：{Title}，触发位 {Address}，{Count} 个数据项",
            _配置.标题, _配置.触发地址, _配置.数据项集合.Count);
    }

    private async Task 异步写入配置文件(历史记录配置 配置项, CancellationToken 取消令牌)
    {
        var JSON文本 = JsonSerializer.Serialize(
            new 历史记录配置文件 { 配置 = 配置项.克隆() }, JSON配置.写入);

        Directory.CreateDirectory(配置路径.配置目录);

        await 原子文件
            .异步写入全部文本(配置路径.历史记录配置, JSON文本, 取消令牌)
            .ConfigureAwait(false);
    }

    /// <summary>首次运行时的默认数据体（对应一台锁螺丝机，地址与 PLC 程序约定一致）。</summary>
    private static 历史记录配置 创建默认值() => new()
    {
        标题 = "锁螺丝记录",
        触发地址 = "M700",
        启用 = true,
        保留天数 = 90,
        数据项集合 =
        [
            new() { 名称 = "工位号", 地址 = "D2100", 数据类型 = 标签数据类型.Int16, 小数位 = 0 },
            new() { 名称 = "机种号", 地址 = "D2101", 数据类型 = 标签数据类型.Int16, 小数位 = 0 },
            new() { 名称 = "工步", 地址 = "D2102", 数据类型 = 标签数据类型.Int16, 小数位 = 0 },
            new() { 名称 = "设定扭力", 地址 = "D2103", 数据类型 = 标签数据类型.Int32, 小数位 = 2, 单元 = "N·m" },
            new() { 名称 = "扭力上限", 地址 = "D2105", 数据类型 = 标签数据类型.Int32, 小数位 = 2, 单元 = "N·m" },
            new() { 名称 = "扭力下限", 地址 = "D2107", 数据类型 = 标签数据类型.Int32, 小数位 = 2, 单元 = "N·m" },
            new() { 名称 = "实际扭力", 地址 = "D2109", 数据类型 = 标签数据类型.Int32, 小数位 = 2, 单元 = "N·m" },
            new() { 名称 = "设定角度", 地址 = "D2111", 数据类型 = 标签数据类型.Int32, 小数位 = 1, 单元 = "°" },
            new() { 名称 = "角度上限", 地址 = "D2113", 数据类型 = 标签数据类型.Int32, 小数位 = 1, 单元 = "°" },
            new() { 名称 = "角度下限", 地址 = "D2115", 数据类型 = 标签数据类型.Int32, 小数位 = 1, 单元 = "°" },
            new() { 名称 = "实际角度", 地址 = "D2117", 数据类型 = 标签数据类型.Int32, 小数位 = 1, 单元 = "°" },
            new() { 名称 = "锁付时间", 地址 = "D2119", 数据类型 = 标签数据类型.Int32, 小数位 = 2, 单元 = "s" },
            new() { 名称 = "结果", 地址 = "D2121", 数据类型 = 标签数据类型.Int16, 小数位 = 0 },
            new() { 名称 = "错误代码", 地址 = "D2122", 数据类型 = 标签数据类型.Int16, 小数位 = 0 },
        ],
    };
}
