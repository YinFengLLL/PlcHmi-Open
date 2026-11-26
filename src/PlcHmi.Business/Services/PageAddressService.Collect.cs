using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="PageAddressService"/> 的「收集标定页」部分（partial）。
///
/// <para>从主文件拆出：主文件逼近 400 行规模门禁。这里只做"把标定页的所有元件地址
/// 摊成一份可编辑清单"，不碰读写与落盘。</para>
/// </summary>
public sealed partial class PageAddressService
{
    /// <summary>说明：标定页复用锁附 X/Y/Z 三根轴（点动与回零）。</summary>
    private static readonly string[] calibrationAxis = ["X", "Y", "Z"];

    /// <inheritdoc />
    public IReadOnlyList<AddressEditItem> CollectSetCalibrationPage()
    {
        var list = new List<AddressEditItem>();

        foreach (var fieldItem in _config.Calibration.Center)
        {
            list.Add(new AddressEditItem
            {
                Category = "标定字段",
                Key = fieldItem.Key,
                Name = fieldItem.DisplayName,
                AddressText = $"D{fieldItem.BaseAddress}",
                DataTypeText = fieldItem.DataType.ToString(),
            });
        }

        var Offset = _config.Calibration.PerPointOffset;
        list.Add(new AddressEditItem
        {
            Category = "标定字段",
            Key = Offset.Key,
            Name = Offset.DisplayName,
            AddressText = $"D{Offset.BaseAddress}",
            DataTypeText = Offset.DataType.ToString(),
        });

        list.Add(new AddressEditItem
        {
            Category = "标定按钮",
            Key = "启动",
            Name = "上相机标定启动",
            AddressText = _config.Calibration.StartAddress,
            DataTypeText = TagDataType.Boolean.ToString(),
        });

        // 下相机（2026-10-17 新增）：地址处理模式与上相机一致，但**没有 Y**，只有 X / Z。
        // 键加「下相机」前缀：上相机已占用 X / Y / Z / 偏移量 这几个键，
        // 编辑器的行是按「类别 + 键」定位的，重键会把两套地址写串。
        var receivedCameraKey = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fieldItem in _config.Calibration.BottomCameraCenter)
        {
            receivedCameraKey.Add(fieldItem.Key);
            list.Add(new AddressEditItem
            {
                Category = "标定字段",
                Key = $"下相机{fieldItem.Key}",
                Name = fieldItem.DisplayName,
                AddressText = $"D{fieldItem.BaseAddress}",
                DataTypeText = fieldItem.DataType.ToString(),
            });
        }

        // 老现场配置没有下相机字段时，编辑器必须照样给出 X / Z 两行：
        // 否则管理员根本看不到、也改不了地址，下发只会报"缺少该字段"（2026-10-21 现场报障）。
        // 这里只合成显示行，保存时由 应用标定字段 把字段补进配置。
        foreach (var DefaultValue in CalibrationConfig.BottomCameraDefaultCenter())
        {
            if (!receivedCameraKey.Add(DefaultValue.Key))
            {
                continue;
            }

            list.Add(new AddressEditItem
            {
                Category = "标定字段",
                Key = $"下相机{DefaultValue.Key}",
                Name = DefaultValue.DisplayName,
                AddressText = $"D{DefaultValue.BaseAddress}",
                DataTypeText = DefaultValue.DataType.ToString(),
            });
        }

        var bottomCameraOffset = _config.Calibration.BottomCameraPerPointOffset;
        var offsetBaseAddress = bottomCameraOffset.BaseAddress > 0 ? bottomCameraOffset.BaseAddress : CalibrationConfig.bottomCameraOffsetDefaultBaseAddress;

        list.Add(new AddressEditItem
        {
            Category = "标定字段",
            Key = "下相机偏移量",
            Name = bottomCameraOffset.DisplayName,
            AddressText = $"D{offsetBaseAddress}",
            DataTypeText = bottomCameraOffset.DataType.ToString(),
        });

        list.Add(new AddressEditItem
        {
            Category = "标定按钮",
            Key = "下相机启动",
            Name = "下相机标定启动",
            AddressText = _config.Calibration.BottomCameraStartAddress,
            DataTypeText = TagDataType.Boolean.ToString(),
        });

        // 「移至指定位置」已按现场要求删除（2026-10-15）：与锁附分组动作按钮「移全指定」
        // 是同一个 M460，重复；要改这个地址请走「标定」分组的动作按钮条目。

        foreach (var axisNumber in calibrationAxis)
        {
            if (FindJog(axisNumber, "锁附") is not { } jogItem)
            {
                continue;
            }

            list.Add(new AddressEditItem
            {
                Category = "点动",
                Key = $"{jogItem.Axis}|正转",
                Name = $"{jogItem.DisplayName}·正转",
                AddressText = jogItem.ForwardAddress,
                DataTypeText = TagDataType.Boolean.ToString(),
            });

            list.Add(new AddressEditItem
            {
                Category = "点动",
                Key = $"{jogItem.Axis}|反转",
                Name = $"{jogItem.DisplayName}·反转",
                AddressText = jogItem.ReverseAddress,
                DataTypeText = TagDataType.Boolean.ToString(),
            });

            list.Add(new AddressEditItem
            {
                Category = "回零",
                Key = jogItem.Axis,
                Name = $"{jogItem.DisplayName}·回零",
                AddressText = FindHome(jogItem.Axis)?.Address ?? string.Empty,
                DataTypeText = TagDataType.Boolean.ToString(),
            });
        }

        // 标定页采用与锁附页相同的元件配置方式：动作按钮 / 辅助地址按「标定」分组纳入，
        // 管理员可新增、改名、改址与删除（当前默认配置里为空，故页面上看不到条目）。
        foreach (var Item in _config.ActionButton.Where(Item => GroupMatch(Item.Group, "标定")))
        {
            list.Add(new AddressEditItem
            {
                Category = "动作按钮",
                Key = Item.Key,
                Name = Item.DisplayName,
                AddressText = Item.Address,
                DataTypeText = TagDataType.Boolean.ToString(),
                Group = Item.Group,
                Deletable = true,
            });
        }

        foreach (var Item in _config.AuxAddress.Where(Item => GroupMatch(Item.Group, "标定")))
        {
            list.Add(new AddressEditItem
            {
                Category = "辅助地址",
                Key = Item.Key,
                Name = Item.DisplayName,
                AddressText = Item.Address,
                DataTypeText = Item.DataType.ToString(),
                Group = Item.Group,
                Deletable = true,
            });
        }

        return list;
    }
}
