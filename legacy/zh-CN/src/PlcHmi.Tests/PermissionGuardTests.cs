using Microsoft.Extensions.Logging.Abstractions;
using 上位机.业务.配置;
using 上位机.业务.服务;
using 上位机.通信.汇川;
using 上位机.核心.抽象;
using 上位机.核心.枚举;
using 上位机.核心.模型;
using 上位机.上位机测试.替身;

namespace 上位机.上位机测试;

/// <summary>
/// 服务层权限边界测试（审查标准 R5）。
///
/// <para><b>为什么必须有这一组：</b>2026-09-13 审查发现全部 7 处权限检查都在 ViewModel 里，
/// <c>PlcHmi.Business</c> 一处都没有 —— 而 <c>docs/01</c> 却写着"服务层再校验一次"。
/// 界面隐藏按钮只是防误触，不是边界；任何人都能直接调服务。</para>
///
/// <para>按 R5：<i>"管理员专有功能（延时表 / 按钮表 / 报警 / 元件编辑）双重校验 → 缺失 = 🔴"</i>。
/// 本组测试把"服务层确实兜住了"变成可执行的事实。</para>
///
/// <para><b>拒绝语义：</b>抛 <see cref="UnauthorizedAccessException"/>，且<b>不留任何状态</b>——
/// 校验发生在参数校验之前、任何变更之前。</para>
/// </summary>
public class 权限守卫测试
{
    /// <summary>未登录 → 操作员（最低权限）→ 一切写操作都应被拒。</summary>
    private static 用户权限守卫 操作员()
    {
        // 不做 InitializeAsync：构造出来的 UserService 未登录，CurrentRole 即操作员
        var 用户 = new 用户服务(NullLogger<用户服务>.Instance);
        return new 用户权限守卫(用户, NullLogger<用户权限守卫>.Instance);
    }

    private static 报警定义服务 新报警服务(权限守卫接口 守卫) =>
        new(new 汇川地址解析器(PLC系列.小型PLC),
            NullLogger<报警定义服务>.Instance, 守卫);

    private static 参数表格服务 新参数服务(权限守卫接口 守卫) =>
        new(new 汇川地址解析器(PLC系列.小型PLC),
            null!, NullLogger<参数表格服务>.Instance, 守卫);

    private static 手动元件服务 新元件服务(权限守卫接口 守卫) =>
        new(new 汇川地址解析器(PLC系列.小型PLC),
            NullLogger<手动元件服务>.Instance, 守卫);

    private static 报警定义 报警() =>
        new() { 标识 = "A901", 消息 = "测试报警", 地址 = "M500", 位 = -1, 启用 = true };

    private static 参数条目定义 延迟() =>
        new() { 标识 = "d-test", 名称 = "测试延时", 地址 = "D1000", 值 = 1, 最小 = 0, 最大 = 10 };

    private static 手动元件定义 元件() =>
        new() { 标识 = "w-test", 名称 = "测试气缸", 类型 = 手动元件类型.气缸, 地址 = "M220" };

    // ── 报警编辑（管理员专有）────────────────────────────────────────────

    [Fact]
    public void 操作员新增报警定义应被拒绝且不留下任何条目()
    {
        var 服务实例 = 新报警服务(操作员());

        Assert.Throws<UnauthorizedAccessException>(() => 服务实例.添加(报警()));

        // 关键：拒绝必须发生在任何状态变更之前
        Assert.Empty(服务实例.定义集合);
    }

    [Fact]
    public void 操作员删除报警定义应被拒绝()
    {
        var 服务实例 = 新报警服务(操作员());

        Assert.Throws<UnauthorizedAccessException>(() => 服务实例.移除("A001"));
    }

    [Fact]
    public void 操作员更新报警定义应被拒绝()
    {
        var 服务实例 = 新报警服务(操作员());

        Assert.Throws<UnauthorizedAccessException>(() => 服务实例.更新(报警()));
    }

    [Fact]
    public void 有权限时报警定义可以正常新增()
    {
        var 服务实例 = 新报警服务(new 允许全部权限守卫());

        服务实例.添加(报警());

        Assert.Single(服务实例.定义集合);
    }

    // ── 参数表定义（管理员专有）──────────────────────────────────────────

    [Fact]
    public void 操作员新增延时条目应被拒绝()
    {
        var 服务实例 = 新参数服务(操作员());

        Assert.Throws<UnauthorizedAccessException>(
            () => 服务实例.添加(参数表格类别.延迟, 延迟()));

        Assert.Empty(服务实例.延迟集合);
    }

    [Fact]
    public void 操作员删除参数表条目应被拒绝()
    {
        var 服务实例 = 新参数服务(操作员());

        Assert.Throws<UnauthorizedAccessException>(
            () => 服务实例.移除(参数表格类别.按钮, "btn-1"));
    }

    // ── 手动页元件（管理员专有）──────────────────────────────────────────

    [Fact]
    public void 操作员新增手动页元件应被拒绝()
    {
        var 服务实例 = 新元件服务(操作员());

        Assert.Throws<UnauthorizedAccessException>(() => 服务实例.添加(元件()));

        Assert.Empty(服务实例.元件集合);
    }

    [Fact]
    public void 操作员删除手动页元件应被拒绝()
    {
        var 服务实例 = 新元件服务(操作员());

        Assert.Throws<UnauthorizedAccessException>(() => 服务实例.移除("cyl-1"));
    }

    // ── 手动控制 / 设备命令（2026-09-29 审查整改补齐的服务层边界）────────

    private static 手动控件服务 新手动服务(权限守卫接口 守卫, 替身Modbus客户端 客户端)
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        return new 手动控件服务(
            new 标签写入器(
                客户端,
                new 标签仓储(NullLogger<标签仓储>.Instance),
                解析器,
                NullLogger<标签写入器>.Instance),
            客户端,
            解析器,
            配方配置加载器.加载(out _),
            守卫,
            NullLogger<手动控件服务>.Instance);
    }

    private static 设备服务 新设备服务(权限守卫接口 守卫, 替身Modbus客户端 客户端)
    {
        var 解析器 = new 汇川地址解析器(PLC系列.小型PLC);

        return new 设备服务(
            new 标签值服务(NullLogger<标签值服务>.Instance),
            new 标签写入器(
                客户端,
                new 标签仓储(NullLogger<标签仓储>.Instance),
                解析器,
                NullLogger<标签写入器>.Instance),
            守卫,
            NullLogger<设备服务>.Instance);
    }

    [Fact]
    public async Task 操作员开启手动使能应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        await 客户端.异步连接();

        var 服务实例 = 新手动服务(操作员(), 客户端);

        Assert.Throws<UnauthorizedAccessException>(() => 服务实例.设置启用(true));

        // 拒绝不留状态：使能保持关闭
        Assert.False(服务实例.是否启用);
    }

    [Fact]
    public async Task 操作员点动应被拒绝且不下发任何命令()
    {
        await using var 客户端 = new 替身Modbus客户端();
        await 客户端.异步连接();

        var 服务实例 = 新手动服务(操作员(), 客户端);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 服务实例.异步点动("X", 1));

        Assert.Empty(客户端.寄存器写入);
        Assert.Empty(客户端.线圈写入);
    }

    [Fact]
    public async Task 关闭手动使能不受权限限制()
    {
        await using var 客户端 = new 替身Modbus客户端();
        await 客户端.异步连接();

        // 关闭是安全方向：最低权限（未登录=操作员）也必须能执行，不能被权限拦下
        var 服务实例 = 新手动服务(操作员(), 客户端);

        var 异常 = Record.Exception(() => 服务实例.设置启用(false));

        Assert.Null(异常);
        Assert.False(服务实例.是否启用);
    }

    [Fact]
    public async Task 操作员清除产量数据应被拒绝()
    {
        await using var 客户端 = new 替身Modbus客户端();
        await 客户端.异步连接();

        var 服务实例 = 新设备服务(操作员(), 客户端);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 服务实例.异步清除数据());

        Assert.Empty(客户端.线圈写入);
    }

    [Fact]
    public async Task 操作员启动设备应放行()
    {
        await using var 客户端 = new 替身Modbus客户端();
        await 客户端.异步连接();

        var 服务实例 = 新设备服务(操作员(), 客户端);

        await 服务实例.异步启动();

        // 操作员是运行页的主要使用者：启停必须放行（脉冲 = 置位 + 复位两次写入）
        Assert.NotEmpty(客户端.线圈写入);
    }

    // ── 守卫本身 ────────────────────────────────────────────────────────

    [Fact]
    public void 未登录身份应被识别为操作员()
    {
        var 用户 = new 用户服务(NullLogger<用户服务>.Instance);

        Assert.False(用户.是否已登录);
        Assert.Equal(用户角色.操作员, 用户.当前角色);
        Assert.False(用户.有(权限.编辑绑定地址));
        Assert.False(用户.有(权限.编辑手动元件));
        Assert.False(用户.有(权限.编辑PLC连接));
    }

    [Fact]
    public void 放行守卫不应拦截任何权限()
    {
        var 守卫 = new 允许全部权限守卫();

        // 仅用于测试/批处理的放行守卫：不应抛异常
        守卫.要求(权限.管理用户);
        守卫.要求(权限.编辑PLC连接);
    }

    [Fact]
    public void 拒绝信息应说明缺少哪项权限且不含密码等信息()
    {
        var 守卫 = 操作员();

        var 异常 = Assert.Throws<UnauthorizedAccessException>(
            () => 守卫.要求(权限.编辑PLC连接));

        Assert.Contains("编辑PLC连接", 异常.Message, StringComparison.Ordinal);
    }
}
