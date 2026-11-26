using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PlcHmi.App.Services;
using PlcHmi.Core.Models;

namespace PlcHmi.App.ViewModels;

/// <summary>
/// <see cref="CalibrationViewModel"/> 的「相机标定」部分（partial）。
///
/// <para><b>2026-10-17 现场要求：</b>标定页上方标定栏分成两套相机——
/// <b>上相机</b>（中心点 X/Y/Z + 偏移量 + 标定启动）与
/// <b>下相机</b>（<b>起点 X/Z</b> + 偏移量 + 标定启动）。
/// 下相机的地址处理模式与上相机完全一致，只是<b>没有 Y</b>（下相机不参与 Y 方向）。</para>
///
/// <para>从主文件拆出：主文件已顶到项目规模门禁（400 行），而"写入 / 启动"是关键动作的集中地，
/// 单独一处也便于审阅（都要求手动使能 + 确认框）。</para>
/// </summary>
public sealed partial class CalibrationViewModel
{
    // ── 上相机 ──────────────────────────────────────────────────────────

    /// <summary>写入上相机标定中心点坐标（X / Y / Z）。</summary>
    [RelayCommand]
    private async Task WriteCenterAsync()
    {
        if (!CoordinateAux.TryParse(CenterX, out var X)
            || !CoordinateAux.TryParse(CenterY, out var Y)
            || !CoordinateAux.TryParse(CenterZ, out var Z))
        {
            _dialog.ErrorInfo("写入失败", "上相机标定中心点坐标不是有效数字。");
            return;
        }

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "写入中心点", () => _calibration.WriteCenterAsync(X, Y, Z))
                    .ConfigureAwait(true))
            {
                _dialog.Hint("写入完成", "上相机标定中心点坐标已写入 PLC。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>写入上相机标定偏移量。</summary>
    [RelayCommand]
    private async Task WriteOffsetAsync()
    {
        if (!CoordinateAux.TryParse(OffsetValue, out var Value))
        {
            _dialog.ErrorInfo("写入失败", "上相机标定偏移量不是有效数字。");
            return;
        }

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "写入偏移量", () => _calibration.WriteOffsetAsync(Value))
                    .ConfigureAwait(true))
            {
                _dialog.Hint("写入完成", "上相机标定偏移量已写入 PLC。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>上相机标定启动（脉冲 M 区线圈）。</summary>
    [RelayCommand]
    private Task CalibrationStartAsync() => StartCalibrationAsync("上相机标定启动", () => _calibration.CalibrationStartAsync());

    // ── 下相机（没有 Y：只写 X / Z）──────────────────────────────────────

    /// <summary>写入下相机标定起点坐标（X / Z）。</summary>
    [RelayCommand]
    private async Task WriteBottomCameraCenterAsync()
    {
        if (!CoordinateAux.TryParse(BottomCameraX, out var X)
            || !CoordinateAux.TryParse(BottomCameraZ, out var Z))
        {
            _dialog.ErrorInfo("写入失败", "下相机标定起点坐标不是有效数字（只有 X / Z）。");
            return;
        }

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "写入下相机起点", () => _calibration.WriteBottomCameraCenterAsync(X, Z))
                    .ConfigureAwait(true))
            {
                _dialog.Hint("写入完成", "下相机标定起点坐标已写入 PLC（X / Z）。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>写入下相机标定偏移量。</summary>
    [RelayCommand]
    private async Task WriteBottomCameraOffsetAsync()
    {
        if (!CoordinateAux.TryParse(BottomCameraOffset, out var Value))
        {
            _dialog.ErrorInfo("写入失败", "下相机标定偏移量不是有效数字。");
            return;
        }

        IsBusy = true;

        try
        {
            if (await UiCommand
                    .ExecuteAsync(_log, _dialog, "写入下相机偏移量", () => _calibration.WriteBottomCameraOffsetAsync(Value))
                    .ConfigureAwait(true))
            {
                _dialog.Hint("写入完成", "下相机标定偏移量已写入 PLC。");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>下相机标定启动（脉冲 M 区线圈）。</summary>
    [RelayCommand]
    private Task BottomCameraCalibrationStartAsync() =>
        StartCalibrationAsync("下相机标定启动", () => _calibration.BottomCameraCalibrationStartAsync());

    /// <summary>
    /// 两个「标定启动」共用的一段：手动使能联锁 → 确认框 → 脉冲写线圈。
    /// <para>联锁与确认是安全边界，两台相机必须一致——复制一遍很容易漏掉其中一条。</para>
    /// </summary>
    private async Task StartCalibrationAsync(string Title, Func<Task> action)
    {
        if (!IsManualEnabled)
        {
            _dialog.Hint("操作被拒绝", "请先开启手动使能，再执行标定。");
            return;
        }

        if (!_dialog.Confirm(Title,
                $"确认启动{Title}？\n\n标定过程中设备将自动移动，确认无人员处于动作范围。",
                Views.ConfirmLevel.Warning, "启动"))
        {
            return;
        }

        IsBusy = true;

        try
        {
            await UiCommand
                .ExecuteAsync(_log, _dialog, Title, action)
                .ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
