using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Abstractions;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 下相机标定地址的回归（2026-10-21 现场报障）。
///
/// <para><b>现场现象：</b>标定页「编辑地址」里<b>看不到下相机 X / Z</b>，点下相机「写入」直接报
/// "标定配置缺少该字段"。真因是老现场 <c>recipe-config.json</c> 里没有较新版本新增的
/// <c>下相机中心点</c> 一节 → 反序列化后为空列表 → 既收集不出编辑行，下发也无字段可写。</para>
///
/// <para>修法：加载时按出厂定义补齐；编辑器即使配置缺字段也照样给出 X / Z 两行，
/// 保存时把字段补建进配置。</para>
/// </summary>
public class BottomCameraCalibrationAddressTest
{
    [Fact]
    public void LegacyConfigMissingBottomCameraFieldShouldFillXAndZ()
    {
        var configPairObject = new RecipeConfig();   // 复刻现场：没有 calibration.下相机中心点

        Assert.Empty(configPairObject.Calibration.BottomCameraCenter);

        var fill = configPairObject.Calibration.FillBottomCamera();

        Assert.Equal(2, fill);   // X + Z（下相机没有 Y）
        Assert.Equal(CalibrationConfig.bottomCameraXDefaultBaseAddress, configPairObject.Calibration.FindBottomCamera("X")!.BaseAddress);
        Assert.Equal(CalibrationConfig.bottomCameraZDefaultBaseAddress, configPairObject.Calibration.FindBottomCamera("Z")!.BaseAddress);
        Assert.Null(configPairObject.Calibration.FindBottomCamera("Y"));

        // 幂等：再补一次不重复添加
        Assert.Equal(0, configPairObject.Calibration.FillBottomCamera());
        Assert.Equal(2, configPairObject.Calibration.BottomCameraCenter.Count);
    }

    /// <summary>
    /// 旧口径的下相机偏移基址 3612 落在点胶结构体数组（D3500–D3739）里，
    /// 会与单点工艺参数互相覆盖——加载时必须纠正到 3744。
    /// </summary>
    [Fact]
    public void BottomCameraOffsetInReservedAreaShouldCorrectAsFactoryAddress()
    {
        var configPairObject = new RecipeConfig();
        configPairObject.Calibration.BottomCameraPerPointOffset.BaseAddress = 3612;

        configPairObject.Calibration.FillBottomCamera();

        Assert.Equal(CalibrationConfig.bottomCameraOffsetDefaultBaseAddress, configPairObject.Calibration.BottomCameraPerPointOffset.BaseAddress);
    }

    /// <summary>
    /// 配置里没有下相机字段时，标定页「编辑地址」也必须给出 X / Z（以及偏移 / 启动）可编辑行，
    /// 否则现场连"补地址"的入口都没有。
    /// </summary>
    [Fact]
    public async Task ConfigMissingBottomCameraFieldWhenEditorShouldStillGiveBottomCameraAddressRow()
    {
        var configPairObject = new RecipeConfig();   // 故意不跑补齐：验证编辑器自身的兜底

        await using var Client = new FakeModbusClient();
        var serviceInstance = new PageAddressService(
            Client,
            new TagWriter(Client, new TagRepository(NullLogger<TagRepository>.Instance),
                new InovanceAddressParser(PlcSeries.SmallPlc), NullLogger<TagWriter>.Instance),
            configPairObject,
            new AllowAllPermissionGuard(),
            NullLogger<PageAddressService>.Instance);

        var list = serviceInstance.CollectSetCalibrationPage();

        Assert.Contains(list, Item => Item.Category == "标定字段" && Item.Key == "下相机X"
            && Item.AddressText == $"D{CalibrationConfig.bottomCameraXDefaultBaseAddress}");
        Assert.Contains(list, Item => Item.Category == "标定字段" && Item.Key == "下相机Z"
            && Item.AddressText == $"D{CalibrationConfig.bottomCameraZDefaultBaseAddress}");
        Assert.Contains(list, Item => Item.Category == "标定字段" && Item.Key == "下相机偏移量"
            && Item.AddressText == $"D{CalibrationConfig.bottomCameraOffsetDefaultBaseAddress}");
        Assert.Contains(list, Item => Item.Category == "标定按钮" && Item.Key == "下相机启动");
    }
}
