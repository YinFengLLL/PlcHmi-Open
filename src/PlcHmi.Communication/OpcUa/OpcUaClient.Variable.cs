using Opc.Ua;
using Opc.Ua.Client;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.通信.Modbus;

namespace 上位机.通信.OPCUA;

/// <summary>
/// OPC UA 客户端的变量绑定读写部分。
///
/// <para><b>变量段的语义</b>：变量绑定的地址段整段对应 PLC 服务端上的一个类型化节点
/// （如 REAL 变量节点值就是浮点数本身），与逐寄存器节点完全不同——
/// 因此读写前先把连续地址切成操作块，变量段经
/// <see cref="寄存器编解码器"/> 做“类型值 ↔ 寄存器数组”转换（红线 R1：字序处理只走编解码器）。</para>
/// </summary>
public sealed partial class OPCUA客户端
{
    /// <summary>
    /// 读一个变量节点并按其数据类型转换为寄存器数组（字序由寄存器编解码器统一处理，红线 R1）。
    /// 类型转换失败或占用字数与请求不符时返回 <c>false</c>，调用方回退到逐寄存器路径。
    /// </summary>
    private async Task<bool> 尝试读变量块(
        ISession 会话, 变量表条目 变量, int 数量值, ushort[] 目标, int 目标偏移, CancellationToken 取消令牌)
    {
        try
        {
            var 值列表 = await 批量读(会话, [_推导器.推导变量节点(变量.变量名)], 取消令牌)
                .ConfigureAwait(false);

            if (值列表.Count != 1 || 值列表[0].Value is null)
            {
                return false;
            }

            var 寄存器列表 = 寄存器编解码器.编码(变量.数据类型, Convert.ToDouble(值列表[0].Value));

            if (寄存器列表.Length != 数量值)
            {
                return false;
            }

            Array.Copy(寄存器列表, 0, 目标, 目标偏移, 数量值);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把寄存器片段按变量数据类型还原为单值并写入变量节点（字序由寄存器编解码器统一处理）。
    /// 类型转换失败或占用字数与请求不符时返回 <c>false</c>，调用方回退到逐寄存器路径。
    /// </summary>
    private async Task<bool> 尝试写变量块(
        ISession 会话, 变量表条目 变量, IReadOnlyList<ushort> 值列表, int 源偏移, int 数量值,
        CancellationToken 取消令牌)
    {
        if (数量值 <= 0 || 源偏移 + 数量值 > 值列表.Count)
        {
            return false;
        }

        var 片段 = new ushort[数量值];
        for (var 条目 = 0; 条目 < 数量值; 条目++)
        {
            片段[条目] = 值列表[源偏移 + 条目];
        }

        object 单值;
        try
        {
            单值 = 寄存器编解码器.解码(变量.数据类型, 片段);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var 写入集合 = new WriteValueCollection
        {
            new WriteValue
            {
                NodeId = _推导器.推导变量节点(变量.变量名),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(单值)),
            },
        };

        await 批量写(会话, 写入集合, 取消令牌).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 把连续地址区间切分为读写操作块：<b>变量首地址命中的整段优先</b>（一块 = 一个变量节点），
    /// 其余地址按连续段合并（一次批量请求）。
    ///
    /// <para>轮询引擎合并的是“同功能码区”的连续地址，其中可能嵌着变量绑定段——
    /// 变量段必须整段单独读写（节点值是完整类型值），不能拆成逐寄存器节点。</para>
    /// </summary>
    private List<(int 起始, int 数量, 变量表条目? 变量)> 切分操作块(
        Modbus区域 区域值, int 起始地址, int 数量值)
    {
        var 块列表 = new List<(int, int, 变量表条目?)>();
        var 段起始 = 0;
        var 段长度 = 0;
        var 偏移 = 0;

        while (偏移 < 数量值)
        {
            if (_推导器.尝试匹配变量(区域值, 起始地址 + 偏移, out var 变量, out var 变量数量)
                && 变量数量 <= 数量值 - 偏移)
            {
                if (段长度 > 0)
                {
                    块列表.Add((段起始, 段长度, null));
                    段长度 = 0;
                }

                块列表.Add((起始地址 + 偏移, 变量数量, 变量));
                偏移 += 变量数量;
            }
            else
            {
                if (段长度 == 0)
                {
                    段起始 = 起始地址 + 偏移;
                }

                段长度++;
                偏移++;
            }
        }

        if (段长度 > 0)
        {
            块列表.Add((段起始, 段长度, null));
        }

        return 块列表;
    }
}
