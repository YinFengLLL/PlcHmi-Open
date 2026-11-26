using System.Collections.Concurrent;
using System.Globalization;
using Opc.Ua;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.通信.汇川;

namespace 上位机.通信.OPCUA;

/// <summary>
/// OPC UA NodeId 推导器。把“区域 + Modbus 地址”推导为 PLC 服务端上的节点标识。
///
/// <para><b>为什么需要它：</b>业务层只持有软元件地址体系（X10 / D100），
/// 但接口契约传给客户端的是已换算的 Modbus 地址。OPC UA 服务端按软元件符号组织节点，
/// 因此客户端必须先经 <see cref="汇川地址解析器.反解"/> 还原软元件符号，再按
/// <c>PLC连接选项.OPCUA节点格式</c> 模板拼出 NodeId——例如 D100 → <c>ns=2;s=D100</c>。</para>
///
/// <para><b>变量绑定优先</b>：配置条目绑定了变量表变量时，该地址段以<b>变量名</b>做符号寻址
/// （模板 <c>OPCUA变量格式</c>，默认 <c>ns=2;s=X轴命令.b_轴使能</c>）——
/// 变量节点的值类型与占用字数由 PLC 程序决定，不受基址调整影响。</para>
///
/// <para>推导结果按 (区域, 地址) 缓存，避免快轮询下每周期重复字符串拼接与解析；
/// 变量表重新导入时缓存整体失效重建。</para>
/// </summary>
public sealed class NodeId推导器
{
    private readonly PLC连接选项 _选项;
    private readonly 汇川地址解析器 _解析器;
    private readonly 变量表服务接口? _变量表;
    private readonly ConcurrentDictionary<(Modbus区域 区域, int 地址), NodeId> _缓存 = new();

    public NodeId推导器(PLC连接选项 选项, 汇川地址解析器 解析器, 变量表服务接口? 变量表 = null)
    {
        _选项 = 选项 ?? throw new ArgumentNullException(nameof(选项));
        _解析器 = 解析器 ?? throw new ArgumentNullException(nameof(解析器));
        _变量表 = 变量表;

        if (_变量表 is not null)
        {
            // 重新导入变量表后，地址→变量的映射可能变化，旧缓存全部作废
            _变量表.已重新导入 += (_, _) => _缓存.Clear();
        }
    }

    /// <summary>推导指定区域与 Modbus 地址对应的 NodeId（带缓存）。</summary>
    public NodeId 推导(Modbus区域 区域值, int Modbus地址值) =>
        _缓存.GetOrAdd((区域值, Modbus地址值),
            键 => NodeId.Parse(推导文本(键.区域, 键.地址)));

    /// <summary>
    /// 尝试把 (区域, 起始地址) 匹配到变量表中的变量（按变量首地址）。
    /// 命中时返回条目并经 <paramref name="寄存器数量值"/> 给出该变量的占用字数。
    /// </summary>
    public bool 尝试匹配变量(Modbus区域 区域值, int 起始地址值, out 变量表条目? 条目, out int 寄存器数量值)
    {
        if (_变量表 is not null)
        {
            条目 = _变量表.按首地址查找(区域值, 起始地址值, out 寄存器数量值);
            return 条目 is not null;
        }

        条目 = null;
        寄存器数量值 = 0;
        return false;
    }

    /// <summary>按变量名推导 NodeId（模板 <c>OPCUA变量格式</c>，默认 <c>ns=2;s={变量}</c>）。</summary>
    public string 推导变量文本(string 变量名) =>
        _选项.OPCUA变量格式
            .Replace("{命名空间}", _选项.OPCUA命名空间索引.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{变量}", 变量名, StringComparison.Ordinal);

    /// <summary>按变量名推导 NodeId 对象。</summary>
    public NodeId 推导变量节点(string 变量名) => NodeId.Parse(推导变量文本(变量名));

    /// <summary>推导指定区域与 Modbus 地址对应的 NodeId 文本（不做缓存，供测试与诊断）。</summary>
    public string 推导文本(Modbus区域 区域值, int Modbus地址值)
    {
        var 地址值 = _解析器.反解(区域值, Modbus地址值);

        // 来源形如 "D100" / "X10"，前缀部分即软元件符号
        var 符号结束 = 0;
        while (符号结束 < 地址值.来源.Length && !char.IsDigit(地址值.来源[符号结束]))
        {
            符号结束++;
        }

        var 软元件 = 地址值.来源[..符号结束];
        var 序号文本 = 地址值.来源[符号结束..];

        return _选项.OPCUA节点格式
            .Replace("{命名空间}", _选项.OPCUA命名空间索引.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{软元件}", 软元件, StringComparison.Ordinal)
            .Replace("{序号}", 序号文本, StringComparison.Ordinal);
    }
}
