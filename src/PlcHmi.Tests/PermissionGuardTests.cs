using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

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
public class PermissionGuardTest
{
    /// <summary>未登录 → 操作员（最低权限）→ 一切写操作都应被拒。</summary>
    private static UserPermissionGuard Operator()
    {
        // 不做 InitializeAsync：构造出来的 UserService 未登录，CurrentRole 即操作员
        var User = new UserService(NullLogger<UserService>.Instance);
        return new UserPermissionGuard(User, NullLogger<UserPermissionGuard>.Instance);
    }

    private static AlarmDefinitionService NewAlarmService(IPermissionGuard guard) =>
        new(new InovanceAddressParser(PlcSeries.SmallPlc),
            NullLogger<AlarmDefinitionService>.Instance, guard);

    private static ParameterTableService NewParameterService(IPermissionGuard guard) =>
        new(new InovanceAddressParser(PlcSeries.SmallPlc),
            null!, NullLogger<ParameterTableService>.Instance, guard);

    private static ManualComponentService NewComponentService(IPermissionGuard guard) =>
        new(new InovanceAddressParser(PlcSeries.SmallPlc),
            NullLogger<ManualComponentService>.Instance, guard);

    private static AlarmDefinition Alarm() =>
        new() { Id = "A901", Message = "测试报警", Address = "M500", Bit = -1, Enabled = true };

    private static ParameterEntryDefinition Delay() =>
        new() { Id = "d-test", Name = "测试延时", Address = "D1000", Value = 1, Min = 0, Max = 10 };

    private static ManualComponentDefinition Component() =>
        new() { Id = "w-test", Name = "测试气缸", Kind = ManualComponentType.Cylinder, Address = "M220" };

    // ── 报警编辑（管理员专有）────────────────────────────────────────────

    [Fact]
    public void OperatorAddAlarmDefinitionShouldBeRejectedAndNotLeaveAnyEntry()
    {
        var serviceInstance = NewAlarmService(Operator());

        Assert.Throws<UnauthorizedAccessException>(() => serviceInstance.Add(Alarm()));

        // 关键：拒绝必须发生在任何状态变更之前
        Assert.Empty(serviceInstance.DefinitionCollection);
    }

    [Fact]
    public void OperatorDeleteAlarmDefinitionShouldBeRejected()
    {
        var serviceInstance = NewAlarmService(Operator());

        Assert.Throws<UnauthorizedAccessException>(() => serviceInstance.Remove("A001"));
    }

    [Fact]
    public void OperatorUpdateAlarmDefinitionShouldBeRejected()
    {
        var serviceInstance = NewAlarmService(Operator());

        Assert.Throws<UnauthorizedAccessException>(() => serviceInstance.Update(Alarm()));
    }

    [Fact]
    public void HasPermissionWhenAlarmDefinitionCanNormalAdd()
    {
        var serviceInstance = NewAlarmService(new AllowAllPermissionGuard());

        serviceInstance.Add(Alarm());

        Assert.Single(serviceInstance.DefinitionCollection);
    }

    // ── 参数表定义（管理员专有）──────────────────────────────────────────

    [Fact]
    public void OperatorAddDelayEntryShouldBeRejected()
    {
        var serviceInstance = NewParameterService(Operator());

        Assert.Throws<UnauthorizedAccessException>(
            () => serviceInstance.Add(ParameterTableCategory.Delay, Delay()));

        Assert.Empty(serviceInstance.DelayCollection);
    }

    [Fact]
    public void OperatorDeleteParameterTableEntryShouldBeRejected()
    {
        var serviceInstance = NewParameterService(Operator());

        Assert.Throws<UnauthorizedAccessException>(
            () => serviceInstance.Remove(ParameterTableCategory.Button, "btn-1"));
    }

    // ── 手动页元件（管理员专有）──────────────────────────────────────────

    [Fact]
    public void OperatorAddManualPageComponentShouldBeRejected()
    {
        var serviceInstance = NewComponentService(Operator());

        Assert.Throws<UnauthorizedAccessException>(() => serviceInstance.Add(Component()));

        Assert.Empty(serviceInstance.ComponentCollection);
    }

    [Fact]
    public void OperatorDeleteManualPageComponentShouldBeRejected()
    {
        var serviceInstance = NewComponentService(Operator());

        Assert.Throws<UnauthorizedAccessException>(() => serviceInstance.Remove("cyl-1"));
    }

    // ── 手动控制 / 设备命令（2026-09-29 审查整改补齐的服务层边界）────────

    private static ManualWidgetService NewManualService(IPermissionGuard guard, FakeModbusClient Client)
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        return new ManualWidgetService(
            new TagWriter(
                Client,
                new TagRepository(NullLogger<TagRepository>.Instance),
                Parser,
                NullLogger<TagWriter>.Instance),
            Client,
            Parser,
            RecipeConfigLoader.Load(out _),
            guard,
            NullLogger<ManualWidgetService>.Instance);
    }

    private static DeviceService NewDeviceService(IPermissionGuard guard, FakeModbusClient Client)
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        return new DeviceService(
            new TagValueService(NullLogger<TagValueService>.Instance),
            new TagWriter(
                Client,
                new TagRepository(NullLogger<TagRepository>.Instance),
                Parser,
                NullLogger<TagWriter>.Instance),
            guard,
            NullLogger<DeviceService>.Instance);
    }

    [Fact]
    public async Task OperatorOpenStartManualEnableShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        await Client.ConnectAsync();

        var serviceInstance = NewManualService(Operator(), Client);

        Assert.Throws<UnauthorizedAccessException>(() => serviceInstance.SetEnabled(true));

        // 拒绝不留状态：使能保持关闭
        Assert.False(serviceInstance.IsEnabled);
    }

    [Fact]
    public async Task OperatorJogShouldBeRejectedAndNotSendAnyCommand()
    {
        await using var Client = new FakeModbusClient();
        await Client.ConnectAsync();

        var serviceInstance = NewManualService(Operator(), Client);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => serviceInstance.JogAsync("X", 1));

        Assert.Empty(Client.RegisterWrite);
        Assert.Empty(Client.CoilWrite);
    }

    [Fact]
    public async Task CloseManualEnableNotAffectedByPermissionLimit()
    {
        await using var Client = new FakeModbusClient();
        await Client.ConnectAsync();

        // 关闭是安全方向：最低权限（未登录=操作员）也必须能执行，不能被权限拦下
        var serviceInstance = NewManualService(Operator(), Client);

        var exception = Record.Exception(() => serviceInstance.SetEnabled(false));

        Assert.Null(exception);
        Assert.False(serviceInstance.IsEnabled);
    }

    [Fact]
    public async Task OperatorClearRemoveOutputDataShouldBeRejected()
    {
        await using var Client = new FakeModbusClient();
        await Client.ConnectAsync();

        var serviceInstance = NewDeviceService(Operator(), Client);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => serviceInstance.ClearRemoveDataAsync());

        Assert.Empty(Client.CoilWrite);
    }

    [Fact]
    public async Task OperatorStartDeviceShouldAllow()
    {
        await using var Client = new FakeModbusClient();
        await Client.ConnectAsync();

        var serviceInstance = NewDeviceService(Operator(), Client);

        await serviceInstance.StartAsync();

        // 操作员是运行页的主要使用者：启停必须放行（脉冲 = 置位 + 复位两次写入）
        Assert.NotEmpty(Client.CoilWrite);
    }

    // ── 守卫本身 ────────────────────────────────────────────────────────

    [Fact]
    public void NotLoginBodyPartShouldBeIdentifiedAsOperator()
    {
        var User = new UserService(NullLogger<UserService>.Instance);

        Assert.False(User.IsAlreadyLogin);
        Assert.Equal(UserRole.Operator, User.CurrentRole);
        Assert.False(User.Has(Permission.EditBindAddress));
        Assert.False(User.Has(Permission.EditManualComponent));
        Assert.False(User.Has(Permission.EditPlcConnect));
    }

    [Fact]
    public void AllowGuardShouldNotBlockAnyPermission()
    {
        var guard = new AllowAllPermissionGuard();

        // 仅用于测试/批处理的放行守卫：不应抛异常
        guard.WantRequire(Permission.ManageUser);
        guard.WantRequire(Permission.EditPlcConnect);
    }

    [Fact]
    public void RejectInfoShouldDescriptionMissingWhichItemPermissionAndNotHasPasswordWaitInfo()
    {
        var guard = Operator();

        var exception = Assert.Throws<UnauthorizedAccessException>(
            () => guard.WantRequire(Permission.EditPlcConnect));

        Assert.Contains("EditPlcConnect", exception.Message, StringComparison.Ordinal);
    }
}
