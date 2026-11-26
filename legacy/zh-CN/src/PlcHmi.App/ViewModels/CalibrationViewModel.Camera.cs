using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using 上位机.应用.服务;
using 上位机.核心.模型;

namespace 上位机.应用.视图模型;

/// <summary>
/// <see cref="标定视图模型"/> 的「相机标定」部分（partial）。
///
/// <para><b>2026-10-17 现场要求：</b>标定页上方标定栏分成两套相机——
/// <b>上相机</b>（中心点 X/Y/Z + 偏移量 + 标定启动）与
/// <b>下相机</b>（<b>起点 X/Z</b> + 偏移量 + 标定启动）。
/// 下相机的地址处理模式与上相机完全一致，只是<b>没有 Y</b>（下相机不参与 Y 方向）。</para>
///
/// <para>从主文件拆出：主文件已顶到项目规模门禁（400 行），而"写入 / 启动"是关键动作的集中地，
/// 单独一处也便于审阅（都要求手动使能 + 确认框）。</para>
/// </summary>
public sealed partial class 标定视图模型
{
    // ── 上相机 ──────────────────────────────────────────────────────────

    /// <summary>写入上相机标定中心点坐标（X / Y / Z）。</summary>
    [RelayCommand]
    private async Task 异步写入中心点()
    {
        if (!坐标辅助.尝试解析(中心X, out var X)
            || !坐标辅助.尝试解析(中心Y, out var Y)
            || !坐标辅助.尝试解析(中心Z, out var Z))
        {
            _对话框.错误信息("写入失败", "上相机标定中心点坐标不是有效数字。");
            return;
        }

        是否忙碌 = true;

        try
        {
            if (await 界面命令
                    .异步执行(_日志, _对话框, "写入中心点", () => _标定.异步写入中心点(X, Y, Z))
                    .ConfigureAwait(true))
            {
                _对话框.提示("写入完成", "上相机标定中心点坐标已写入 PLC。");
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>写入上相机标定偏移量。</summary>
    [RelayCommand]
    private async Task 异步写入偏移量()
    {
        if (!坐标辅助.尝试解析(偏移量, out var 值))
        {
            _对话框.错误信息("写入失败", "上相机标定偏移量不是有效数字。");
            return;
        }

        是否忙碌 = true;

        try
        {
            if (await 界面命令
                    .异步执行(_日志, _对话框, "写入偏移量", () => _标定.异步写入偏移量(值))
                    .ConfigureAwait(true))
            {
                _对话框.提示("写入完成", "上相机标定偏移量已写入 PLC。");
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>上相机标定启动（脉冲 M 区线圈）。</summary>
    [RelayCommand]
    private Task 异步标定启动() => 异步启动标定("上相机标定启动", () => _标定.异步标定启动());

    // ── 下相机（没有 Y：只写 X / Z）──────────────────────────────────────

    /// <summary>写入下相机标定起点坐标（X / Z）。</summary>
    [RelayCommand]
    private async Task 异步写入下相机中心点()
    {
        if (!坐标辅助.尝试解析(下相机X, out var X)
            || !坐标辅助.尝试解析(下相机Z, out var Z))
        {
            _对话框.错误信息("写入失败", "下相机标定起点坐标不是有效数字（只有 X / Z）。");
            return;
        }

        是否忙碌 = true;

        try
        {
            if (await 界面命令
                    .异步执行(_日志, _对话框, "写入下相机起点", () => _标定.异步写入下相机中心点(X, Z))
                    .ConfigureAwait(true))
            {
                _对话框.提示("写入完成", "下相机标定起点坐标已写入 PLC（X / Z）。");
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>写入下相机标定偏移量。</summary>
    [RelayCommand]
    private async Task 异步写入下相机偏移量()
    {
        if (!坐标辅助.尝试解析(下相机偏移量, out var 值))
        {
            _对话框.错误信息("写入失败", "下相机标定偏移量不是有效数字。");
            return;
        }

        是否忙碌 = true;

        try
        {
            if (await 界面命令
                    .异步执行(_日志, _对话框, "写入下相机偏移量", () => _标定.异步写入下相机偏移量(值))
                    .ConfigureAwait(true))
            {
                _对话框.提示("写入完成", "下相机标定偏移量已写入 PLC。");
            }
        }
        finally
        {
            是否忙碌 = false;
        }
    }

    /// <summary>下相机标定启动（脉冲 M 区线圈）。</summary>
    [RelayCommand]
    private Task 异步下相机标定启动() =>
        异步启动标定("下相机标定启动", () => _标定.异步下相机标定启动());

    /// <summary>
    /// 两个「标定启动」共用的一段：手动使能联锁 → 确认框 → 脉冲写线圈。
    /// <para>联锁与确认是安全边界，两台相机必须一致——复制一遍很容易漏掉其中一条。</para>
    /// </summary>
    private async Task 异步启动标定(string 标题, Func<Task> 动作)
    {
        if (!是否手动启用)
        {
            _对话框.提示("操作被拒绝", "请先开启手动使能，再执行标定。");
            return;
        }

        if (!_对话框.确认(标题,
                $"确认启动{标题}？\n\n标定过程中设备将自动移动，确认无人员处于动作范围。",
                视图.确认等级.警告, "启动"))
        {
            return;
        }

        是否忙碌 = true;

        try
        {
            await 界面命令
                .异步执行(_日志, _对话框, 标题, 动作)
                .ConfigureAwait(true);
        }
        finally
        {
            是否忙碌 = false;
        }
    }
}
