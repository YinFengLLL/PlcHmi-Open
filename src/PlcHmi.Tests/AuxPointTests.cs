using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Core.Abstractions;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 其它点（辅助点）定义管理测试。
///
/// <para><b>守的是什么：</b>现场反馈"编辑其它点时改不了已有名称"（2026-10-15）——
/// 原先编辑模式把名称框锁死，而名称其实是可改的：服务层按原名定位后整体替换。
/// 改名不能让两个点撞名（名称是"按名查找 / 删除 / CSV 交换"的依据），
/// 也不能让"只改坐标、名称原样提交"被误判成重复。</para>
///
/// <para>会写测试输出目录里的 <c>config/aux-points.json</c>，与其它碰配置文件的用例同集合，禁用并行。</para>
/// </summary>
[Collection("config-files")]
public class OtherPointCoordinateTest
{
    private static AuxPointService NewService(FakeModbusClient Client) =>
        new(
            Client,
            RecipeConfigLoader.Load(out _),
            new AllowAllPermissionGuard(),
            NullLogger<AuxPointService>.Instance);

    [Fact]
    public async Task EditWhenShouldBeAbleToAlreadyHasNameChangeRemoveAndPersist()
    {
        var path = ConfigPath.OtherPoint;
        var Original = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(Client);
            await serviceInstance.InitializeAsync();

            var originalName = serviceInstance.PointCollection[0].Name;
            var candidate = serviceInstance.PointCollection[0].Clone();
            candidate.Name = "改名后的点";

            await serviceInstance.EditAsync(originalName, candidate);

            Assert.DoesNotContain(serviceInstance.PointCollection, PointEntry => PointEntry.Name == originalName);
            Assert.Equal("改名后的点", serviceInstance.PointCollection[0].Name);
            Assert.Contains("改名后的点", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            await Restore(path, Original);
        }
    }

    [Fact]
    public async Task ChangeNameCollideOnOtherPointShouldBeRejectedAndNotKeepState()
    {
        var path = ConfigPath.OtherPoint;
        var Original = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(Client);
            await serviceInstance.InitializeAsync();

            Assert.True(serviceInstance.PointCollection.Count >= 2, "配置里的其它点默认定义不足两个，本用例失去意义");

            var item = serviceInstance.PointCollection[0];
            var item2 = serviceInstance.PointCollection[1];
            var candidate = item.Clone();
            candidate.Name = item2.Name;

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => serviceInstance.EditAsync(item.Name, candidate));

            Assert.Contains("已存在", exception.Message);
            Assert.Equal(item.Name, serviceInstance.PointCollection[0].Name);
        }
        finally
        {
            await Restore(path, Original);
        }
    }

    [Fact]
    public async Task OnlyChangeCoordinateNotChangeNameShouldBeAccepted()
    {
        var path = ConfigPath.OtherPoint;
        var Original = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;

        try
        {
            await using var Client = new FakeModbusClient();
            var serviceInstance = NewService(Client);
            await serviceInstance.InitializeAsync();

            var originalName = serviceInstance.PointCollection[0].Name;
            var candidate = serviceInstance.PointCollection[0].Clone();
            candidate.XBaseAddress += 2;

            await serviceInstance.EditAsync(originalName, candidate);

            Assert.Equal(candidate.XBaseAddress, serviceInstance.PointCollection[0].XBaseAddress);
            Assert.Equal(originalName, serviceInstance.PointCollection[0].Name);
        }
        finally
        {
            await Restore(path, Original);
        }
    }

    /// <summary>配置文件是测试输出目录里的共享副本，用完必须还原（原先不存在就删掉）。</summary>
    private static async Task Restore(string path, string? Original)
    {
        if (Original is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        await File.WriteAllTextAsync(path, Original);
    }
}
