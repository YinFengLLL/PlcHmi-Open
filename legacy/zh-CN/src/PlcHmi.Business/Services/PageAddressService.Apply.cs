using 上位机.核心.模型;

namespace 上位机.业务.服务;

/// <summary>
/// <see cref="页面地址服务"/> 的「应用（写回配置）」部分之一（partial）。
///
/// <para>从 <c>PageAddressService.Save.cs</c> 拆出：加完"结构体成员重排"后该文件越过 400 行规模门禁，
/// 按"保存流程 / 单条应用"两类职责切开——这里只负责把编辑器里的一行写回配置对象，
/// 不碰落盘与流程编排。</para>
/// </summary>
public sealed partial class 页面地址服务
{
    private void 应用点动(地址编辑项 项, string 分组)
    {
        var 分段 = 项.键.Split('|');

        if (分段.Length != 2)
        {
            throw new InvalidOperationException($"点动条目「{项.名称}」的键格式不正确。");
        }

        var 点动项 = 查找点动(分段[0], 分组)
            ?? throw new InvalidOperationException($"未配置「{分段[0]}」轴的点动地址。");

        if (分段[1] == "正转")
        {
            点动项.正转地址 = 项.地址文本.Trim();
        }
        else
        {
            点动项.反转地址 = 项.地址文本.Trim();
        }
    }

    private void 应用回零(地址编辑项 项)
    {
        var 回零项 = 查找回零(项.键);

        if (回零项 is null)
        {
            if (string.IsNullOrWhiteSpace(项.地址文本))
            {
                return;
            }

            _配置.回零.Add(new 回零按钮项
            {
                轴 = 项.键,
                名称 = $"{项.键} 轴回零",
                地址 = 项.地址文本.Trim(),
            });

            return;
        }

        回零项.地址 = 项.地址文本.Trim();
    }

    private void 应用标定字段(地址编辑项 项)
    {
        // 下相机先判：它的键带「下相机」前缀，不会与上相机的 X / Y / Z 相撞
        if (项.键.StartsWith("下相机", StringComparison.Ordinal))
        {
            var 下相机键 = 项.键["下相机".Length..];

            if (string.Equals(_配置.标定.下相机每点偏移.键, 下相机键, StringComparison.OrdinalIgnoreCase))
            {
                _配置.标定.下相机每点偏移.基址 = 解析D区(项);
                return;
            }

            var 下相机字段 = _配置.标定.下相机中心点
                .FirstOrDefault(甲 => string.Equals(甲.键, 下相机键, StringComparison.OrdinalIgnoreCase));

            if (下相机字段 is null)
            {
                // 老配置没有下相机字段（编辑器里合成了显示行）：保存时补建，
                // 否则"填了地址却存不下来"，下次进页面这行又消失（2026-10-21 现场报障）
                下相机字段 = new 标定字段定义
                {
                    键 = 下相机键,
                    名称 = 项.名称,
                    数据类型 = 解析类型(项.数据类型文本),
                };

                _配置.标定.下相机中心点.Add(下相机字段);
            }

            下相机字段.基址 = 解析D区(项);
            return;
        }

        var 字段项 = _配置.标定.中心点
            .FirstOrDefault(甲 => string.Equals(甲.键, 项.键, StringComparison.OrdinalIgnoreCase));

        if (字段项 is not null)
        {
            字段项.基址 = 解析D区(项);
            return;
        }

        if (string.Equals(_配置.标定.每点偏移.键, 项.键, StringComparison.OrdinalIgnoreCase))
        {
            _配置.标定.每点偏移.基址 = 解析D区(项);
            return;
        }

        throw new InvalidOperationException($"未找到标定字段「{项.名称}」。");
    }
}
