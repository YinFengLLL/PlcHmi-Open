using CommunityToolkit.Mvvm.ComponentModel;
using 上位机.应用.服务;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="点表示教视图模型"/> 的「指令码」部分（partial）。
///
/// <para><b>2026-10-21 现场要求：</b>点胶示教页的指令码不再手填数字，改为<b>辅助地址区首行的下拉选单</b>：
/// 选中一个点后，在 00 结束 … 09 对针 之间选择，选中即把码值写进该点的指令码字
/// （第 2 点选「直线」→ <c>D3401 = 2</c>），点表的指令码列显示对应文字。</para>
///
/// <para><b>锁附页没有指令码列</b>（<see cref="点表定义.含指令码"/> 为 false），下拉框整块隐藏，
/// 不影响锁附示教的现有布局。</para>
/// </summary>
public abstract partial class 点表示教视图模型
{
    /// <summary>正在把内存值同步到下拉框（用于避免"同步"被当成"用户选择"而反过来写 PLC）。</summary>
    private bool _指令码同步中;

    /// <summary>本页点表是否含指令码列（决定下拉框是否出现）。</summary>
    public bool 含指令码 => 表定义.含指令码;

    /// <summary>指令码下拉选项（00 结束 … 09 对针）。</summary>
    public IReadOnlyList<指令码项> 指令码选项 => 指令码定义.全部;

    /// <summary>当前点选中的指令码。</summary>
    [ObservableProperty] private 指令码项? _选中指令码项;

    /// <summary>当前点的指令码要写到哪个寄存器（如「第 2 点 · 写入 D3401」）。</summary>
    [ObservableProperty] private string _指令码地址文本 = string.Empty;

    partial void On选中指令码项Changed(指令码项? value)
    {
        if (_指令码同步中 || value is null)
        {
            return;
        }

        异步安全调用.即发即忘(() => 异步写入指令码核心(value), _日志, $"写入指令码 {value.显示}");
    }

    /// <summary>
    /// 把「当前点 + 下拉框」对齐到点表里的真实值（切点、清空、下发后都走这里）。
    /// <para>同步期间置 <see cref="_指令码同步中"/>：否则设置下拉选中项会被当成用户操作，
    /// 反过来再写一次 PLC（切点时表现为"每切一点就无谓写一次"）。</para>
    /// </summary>
    protected void 同步指令码下拉()
    {
        if (!含指令码)
        {
            return;
        }

        var 列项 = 表定义.查找列(点表定义.指令码列键);
        var 行列表 = 点表服务.当前点表(表键);

        var 值 = 选中序号 >= 0 && 选中序号 < 行列表.Count
            ? 行列表[选中序号].取(点表定义.指令码列键)
            : 0;

        _指令码同步中 = true;

        try
        {
            选中指令码项 = 指令码定义.查找((int)Math.Round(值));

            指令码地址文本 = 列项 is null
                ? string.Empty
                : $"第 {选中序号 + 1} 点 · 写入 D{列项.基址 + 选中序号 * 列项.字每点位}";
        }
        finally
        {
            _指令码同步中 = false;
        }
    }

    /// <summary>把选中的指令码写给当前点（失败则回滚下拉框显示，不假装写成功）。</summary>
    private async Task 异步写入指令码核心(指令码项 项)
    {
        // 先取当前点：写入是异步的，期间操作员可能已经切到别的点
        var 序号 = 选中序号;

        是否忙碌 = true;

        try
        {
            var 成功 = await 界面命令
                .异步执行(
                    _日志,
                    _对话框,
                    $"写入指令码「{项.名称}」",
                    () => 点表服务.异步写入指令码(表键, 序号, 项.码),
                    $"第 {序号 + 1} 点")
                .ConfigureAwait(true);

            if (!成功)
            {
                同步指令码下拉();
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }
}
