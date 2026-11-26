using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Tests;

/// <summary>
/// 延时表地址区间约束（D1000–D1099）。
///
/// <para><b>背景</b>：D1000–D1500 原先被记为「PLC 公共区、禁止占用」，2026-09-12 需求方确认
/// PLC 侧已释放整段，现划分为 <b>D1000–D1099 延时专属区</b> 与 <b>D1100–D1500 上位机预留段</b>。</para>
///
/// <para>此前延时表只校验"必须是 D 区"，上下限都不管——管理员能填 <c>D0</c> 或 <c>D7000</c>，
/// 撞掉轴速度参数区或示教点表。本文件把这条边界钉在服务层。</para>
/// </summary>
public class DelayAddressRangeTest
{
    private static ParameterTableService NewService()
    {
        var Parser = new InovanceAddressParser(PlcSeries.SmallPlc);

        // Validate 不触碰 TagWriter；只有真正写 PLC 时才用到它
        TagWriter Writer = null!;
        return new ParameterTableService(
            Parser, Writer, NullLogger<ParameterTableService>.Instance, new AllowAllPermissionGuard());
    }

    private static ParameterEntryDefinition DelayEntry(string addressValue) => DelayEntry(addressValue, "test-delay");

    private static ParameterEntryDefinition DelayEntry(string addressValue, string idValue) => new()
    {
        Id = idValue,
        Name = "测试延时",
        Address = addressValue,
        Unit = "s",
        Min = 0,
        Max = 65.535,
        Enabled = true,
    };

    [Theory]
    [InlineData("D1000")]
    [InlineData("D1050")]
    [InlineData("D1098")]     // 2026-10-16 起延时占 2 字（DINT），上限收到 D1098
    public void ExclusiveAreaInnerDelayAddressShouldNotReportAreaBetweenError(string addressValue)
    {
        var resultValue = NewService().Validate(ParameterTableCategory.Delay, DelayEntry(addressValue), isUpdate: false);

        Assert.DoesNotContain(resultValue.ErrorCollection, eventArgs => eventArgs.Contains("必须在 D", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("D999")]
    [InlineData("D1099")]     // 会跨到 D1100（上位机预留段）：DINT 占 2 字后不再放行
    [InlineData("D1100")]
    [InlineData("D700")]      // 迁移前的旧地址，现已越界
    [InlineData("D1600")]     // 轴参数区（D1200–D1799）
    [InlineData("D2500")]     // 示教点表区
    public void ExclusiveAreaOuterDelayAddressShouldBeRejected(string addressValue)
    {
        var resultValue = NewService().Validate(ParameterTableCategory.Delay, DelayEntry(addressValue), isUpdate: false);

        Assert.False(resultValue.IsValid);
        Assert.Contains(resultValue.ErrorCollection, eventArgs => eventArgs.Contains("必须在 D1000", StringComparison.Ordinal));
    }

    [Fact]
    public void NonDAreaAddressShouldBeRejected()
    {
        var resultValue = NewService().Validate(ParameterTableCategory.Delay, DelayEntry("M1000"), isUpdate: false);

        Assert.False(resultValue.IsValid);
        Assert.Contains(resultValue.ErrorCollection, eventArgs => eventArgs.Contains("D 区", StringComparison.Ordinal));
    }

    [Fact]
    public void AndOtherDelayOverlapAddressShouldBeRejected()
    {
        // 延时按 DINT 存毫秒、每条占 2 字：D1050 与 D1051 会互相吃掉高位字
        //（表现为"某条延时偶尔变成几万秒"，现场毫无线索），必须当场拒绝。
        var serviceInstance = NewService();
        serviceInstance.Add(ParameterTableCategory.Delay, DelayEntry("D1050", "delay-甲"));

        var overlap = serviceInstance.Validate(ParameterTableCategory.Delay, DelayEntry("D1051", "delay-乙"), isUpdate: false);

        Assert.False(overlap.IsValid);
        Assert.Contains(overlap.ErrorCollection, eventArgs => eventArgs.Contains("重叠", StringComparison.Ordinal));

        // 相隔 2 字则放行（D1052 占用 D1052–D1053，与 D1050–D1051 不冲突）
        var Legal = serviceInstance.Validate(ParameterTableCategory.Delay, DelayEntry("D1052", "delay-丙"), isUpdate: false);
        Assert.True(Legal.IsValid, Legal.Summary);
    }

    /// <summary>
    /// 出厂配置里已经配好的延时地址必须落在专属区（D1000–D1098）。
    /// <para><b>2026-10-21：</b>现场版出厂配置只用按钮表、延时表为空——
    /// 没有条目时这条自检自然成立，不强行要求"必须有延时"。</para>
    /// </summary>
    [Fact]
    public async Task ConfigFileInDelayAddressShouldPersistAtExclusiveAreaInner()
    {
        var serviceInstance = NewService();
        await serviceInstance.InitializeAsync();

        foreach (var delayValue in serviceInstance.DelayCollection)
        {
            Assert.StartsWith("D", delayValue.Address, StringComparison.OrdinalIgnoreCase);
            Assert.True(int.TryParse(delayValue.Address.AsSpan(1), out var number),
                $"地址格式异常：{delayValue.Address}");

            Assert.InRange(number, ParameterTableService.delayAddressMin, ParameterTableService.delayAddressMax);
        }
    }
}
