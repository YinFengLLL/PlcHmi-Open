using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// IO 点位「新增一栏」的服务层测试。
///
/// <para><b>守的是什么</b>：新增一栏做的是三件事 —— 算编号、改采集范围、写配置。
/// 编号算错（把八进制当十进制）会让点位指到错误的 PLC 地址，属于"看着正常、读的是别处"；
/// 落盘失败却返回成功，重启后新增的栏会凭空消失。</para>
///
/// <para>测试会真实写 <c>config/io-points.json</c>，但那是**测试输出目录**下的
/// 那份（<see cref="ConfigPath"/> 以 AppContext.BaseDirectory 为根），不碰仓库里的 config/。
/// 与其它写配置的测试共用同一集合，禁用并行。</para>
/// </summary>
[Collection("config-files")]
public sealed class IoPointAddNewRowTest : IDisposable
{
    /// <summary>
    /// 原始 <c>config/io-points.json</c> 快照。本类用例会把测试输出目录里的这份文件写成"只剩几栏"
    /// 的状态，而<b>其它测试类（如端到端仿真测试）会读同一份文件</b>——不还原就构成跨类污染：
    /// 端到端测试曾因此在「X10 变为 ON」上超时（点位表被改小，X10 根本不在表里）。
    /// 故每个用例结束后还原，保证并行读取的其它类看到的仍是仓库配置。
    /// </summary>
    private readonly string? _originalContent;

    public IoPointAddNewRowTest()
    {
        _originalContent = File.Exists(ConfigPath.IoPointCollection)
            ? File.ReadAllText(ConfigPath.IoPointCollection)
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_originalContent is not null)
        {
            File.WriteAllText(ConfigPath.IoPointCollection, _originalContent);
        }
    }

    /// <summary>清空两侧点位，让每个用例都从已知状态出发（配置文件可能残留上一次运行的产物）。</summary>
    private static IoPointService NewService(IPermissionGuard? Permission = null)
    {
        var serviceInstance = new IoPointService(
            Permission ?? new AllowAllPermissionGuard(), NullLogger<IoPointService>.Instance);

        serviceInstance.DataGrid.Input.Clear();
        serviceInstance.DataGrid.Output.Clear();
        serviceInstance.DataGrid.Element = new IoPointTableMetadata { GroupSize = 8 };

        return serviceInstance;
    }

    /// <summary>未登录 → 操作员（最低权限）→ 一切写操作都应被拒。走的是生产用的真实守卫。</summary>
    private static IPermissionGuard OperatorPermission() =>
        new UserPermissionGuard(new UserService(NullLogger<UserService>.Instance),
            NullLogger<UserPermissionGuard>.Instance);

    private static IEnumerable<IoPoint> GeneratePoint(string prefix, params int[] indexList) =>
        indexList.Select(Sequence => new IoPoint { Code = prefix + Convert.ToString(Sequence, 8), Enabled = true });

    private static void Fill(IoPointService serviceInstance, string prefix, params int[] indexList)
    {
        if (prefix == "X")
        {
            serviceInstance.DataGrid.Input.AddRange(GeneratePoint(prefix, indexList));
        }
        else
        {
            serviceInstance.DataGrid.Output.AddRange(GeneratePoint(prefix, indexList));
        }
    }

    [Fact]
    public async Task EmptyTableAddNewRowShouldFromX0Start()
    {
        var serviceInstance = NewService();

        var Insert = await serviceInstance.AddNewRowAsync("X");

        Assert.Equal(8, Insert.Count);
        Assert.Equal("X0", Insert[0].Code);
        Assert.Equal("X7", Insert[^1].Code);
        Assert.All(Insert, PointItem => Assert.True(PointItem.Enabled));
        Assert.Equal(8, serviceInstance.DataGrid.Element.InputCount);
    }

    [Fact]
    public async Task AddNewRowShouldByOctalAlignToNextGroup()
    {
        // 这是本功能最容易写错的地方：X27 的八进制序号是 23，
        // 下一栏起点是 24（八进制 30），而不是十进制的 28。
        var serviceInstance = NewService();
        Fill(serviceInstance, "X", 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23);

        var Insert = await serviceInstance.AddNewRowAsync("X");

        Assert.Equal(8, Insert.Count);
        Assert.Equal("X30", Insert[0].Code);
        Assert.Equal("X37", Insert[^1].Code);
        Assert.Equal(32, serviceInstance.DataGrid.Input.Count);

        // 已落盘的编号必须能被地址解析器接受 —— 否则轮询编译时会被静默跳过
        var Parser = new PlcHmi.Communication.Inovance.InovanceAddressParser(PlcSeries.SmallPlc);
        Assert.All(Insert, PointItem => Assert.True(Parser.TryParse(PointItem.Code, out _)));
    }

    [Fact]
    public async Task YSideAddShouldNotAffectImpactXSide()
    {
        var serviceInstance = NewService();
        Fill(serviceInstance, "X", 0, 1, 2, 3, 4, 5, 6, 7);
        Fill(serviceInstance, "Y", 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);

        var Insert = await serviceInstance.AddNewRowAsync("Y");

        Assert.Equal("Y20", Insert[0].Code);
        Assert.Equal("Y27", Insert[^1].Code);
        Assert.Equal(8, Insert.Count);
        Assert.Equal(24, serviceInstance.DataGrid.Output.Count);
        Assert.Equal(8, serviceInstance.DataGrid.Input.Count);   // X 侧未被牵动
        Assert.Equal(24, serviceInstance.DataGrid.Element.OutputCount);
        Assert.Equal(0, serviceInstance.DataGrid.Element.InputCount);
    }

    [Fact]
    public async Task PreviewNextRowShouldNotChangeMoveData()
    {
        var serviceInstance = NewService();
        Fill(serviceInstance, "X", 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23);

        var preview = serviceInstance.PreviewNextRow("X");

        Assert.Equal("X30 — X37", preview);
        Assert.Equal(24, serviceInstance.DataGrid.Input.Count);
    }

    [Fact]
    public async Task NumberBeyondOutEditAddressUpperLimitShouldWholeRowReject()
    {
        var serviceInstance = NewService();
        // 八进制 1777 = 十进制 1023，正好是汇川 X 区编址上限
        Fill(serviceInstance, "X", Convert.ToInt32("1777", 8));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => serviceInstance.AddNewRowAsync("X"));

        // 不允许"加到一半失败"的部分写入
        Assert.Single(serviceInstance.DataGrid.Input);
    }

    /// <summary>
    /// 服务层权限边界：界面隐藏按钮只是防误触，绕过它的成本极低（审查标准 R5）。
    /// 被拒的调用必须<b>不留痕迹</b>——表未变、也未落盘。
    /// </summary>
    [Fact]
    public async Task NonePermissionAddNewRowShouldBeRejectedAndNotChangeMovePointTable()
    {
        var serviceInstance = NewService(OperatorPermission());
        Fill(serviceInstance, "X", 0, 1, 2, 3, 4, 5, 6, 7);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => serviceInstance.AddNewRowAsync("X"));

        Assert.Equal(8, serviceInstance.DataGrid.Input.Count);
        Assert.Equal(0, serviceInstance.DataGrid.Element.InputCount);   // 元数据也没被改
    }
}
