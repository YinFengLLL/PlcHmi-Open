using PlcHmi.Core.Models;

namespace PlcHmi.Business.Services;

/// <summary>
/// <see cref="PageAddressService"/> 的「应用（写回配置）」部分之一（partial）。
///
/// <para>从 <c>PageAddressService.Save.cs</c> 拆出：加完"结构体成员重排"后该文件越过 400 行规模门禁，
/// 按"保存流程 / 单条应用"两类职责切开——这里只负责把编辑器里的一行写回配置对象，
/// 不碰落盘与流程编排。</para>
/// </summary>
public sealed partial class PageAddressService
{
    private void ApplyJog(AddressEditItem Item, string Group)
    {
        var splitSection = Item.Key.Split('|');

        if (splitSection.Length != 2)
        {
            throw new InvalidOperationException($"点动条目「{Item.Name}」的键格式不正确。");
        }

        var jogItem = FindJog(splitSection[0], Group)
            ?? throw new InvalidOperationException($"未配置「{splitSection[0]}」轴的点动地址。");

        if (splitSection[1] == "正转")
        {
            jogItem.ForwardAddress = Item.AddressText.Trim();
        }
        else
        {
            jogItem.ReverseAddress = Item.AddressText.Trim();
        }
    }

    private void ApplyHome(AddressEditItem Item)
    {
        var homeItem = FindHome(Item.Key);

        if (homeItem is null)
        {
            if (string.IsNullOrWhiteSpace(Item.AddressText))
            {
                return;
            }

            _config.Home.Add(new HomeButtonItem
            {
                Axis = Item.Key,
                Name = $"{Item.Key} 轴回零",
                Address = Item.AddressText.Trim(),
            });

            return;
        }

        homeItem.Address = Item.AddressText.Trim();
    }

    private void ApplyCalibrationField(AddressEditItem Item)
    {
        // 下相机先判：它的键带「下相机」前缀，不会与上相机的 X / Y / Z 相撞
        if (Item.Key.StartsWith("下相机", StringComparison.Ordinal))
        {
            var bottomCameraKey = Item.Key["下相机".Length..];

            if (string.Equals(_config.Calibration.BottomCameraPerPointOffset.Key, bottomCameraKey, StringComparison.OrdinalIgnoreCase))
            {
                _config.Calibration.BottomCameraPerPointOffset.BaseAddress = ParseDArea(Item);
                return;
            }

            var bottomCameraField = _config.Calibration.BottomCameraCenter
                .FirstOrDefault(item => string.Equals(item.Key, bottomCameraKey, StringComparison.OrdinalIgnoreCase));

            if (bottomCameraField is null)
            {
                // 老配置没有下相机字段（编辑器里合成了显示行）：保存时补建，
                // 否则"填了地址却存不下来"，下次进页面这行又消失（2026-10-21 现场报障）
                bottomCameraField = new CalibrationFieldDefinition
                {
                    Key = bottomCameraKey,
                    Name = Item.Name,
                    DataType = ParseType(Item.DataTypeText),
                };

                _config.Calibration.BottomCameraCenter.Add(bottomCameraField);
            }

            bottomCameraField.BaseAddress = ParseDArea(Item);
            return;
        }

        var fieldItem = _config.Calibration.Center
            .FirstOrDefault(item => string.Equals(item.Key, Item.Key, StringComparison.OrdinalIgnoreCase));

        if (fieldItem is not null)
        {
            fieldItem.BaseAddress = ParseDArea(Item);
            return;
        }

        if (string.Equals(_config.Calibration.PerPointOffset.Key, Item.Key, StringComparison.OrdinalIgnoreCase))
        {
            _config.Calibration.PerPointOffset.BaseAddress = ParseDArea(Item);
            return;
        }

        throw new InvalidOperationException($"未找到标定字段「{Item.Name}」。");
    }
}
