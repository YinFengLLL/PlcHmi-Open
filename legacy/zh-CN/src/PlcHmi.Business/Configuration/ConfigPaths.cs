using System.Text.Json;
using System.Text.Json.Serialization;

namespace 上位机.业务.配置;

/// <summary>配置文件路径解析。配置文件放在程序目录下的 <c>config/</c>，便于现场修改而无需重新编译。</summary>
public static class 配置路径
{
    /// <summary>配置目录。</summary>
    public static string 配置目录 { get; } =
        Path.Combine(AppContext.BaseDirectory, "config");

    /// <summary>主配置文件。</summary>
    public static string 应用设置 => Path.Combine(配置目录, "appsettings.json");

    /// <summary>业务点位表。</summary>
    public static string 标签集合 => Path.Combine(配置目录, "tags.json");

    /// <summary>IO 点位表。</summary>
    public static string IO点位集合 => Path.Combine(配置目录, "io-points.json");

    /// <summary>机种（配方）结构化配置：参数项定义与全部基址。</summary>
    public static string 配方配置 => Path.Combine(配置目录, "recipe-config.json");

    /// <summary>机种数据主存：每个机种一份 D2500–D3999 区段快照。</summary>
    public static string 配方集合 => Path.Combine(配置目录, "recipes.json");

    /// <summary>
    /// 全局参数主存（轴速度等）。
    /// <para>机种区段（D2500–D3999）之外的 D 区数据"全局记忆"，不随机种切换，
    /// 因此不再存进机种条目里（见 <see cref="上位机.核心.模型.机种区段"/>）。</para>
    /// </summary>
    public static string 参数集合 => Path.Combine(配置目录, "parameters.json");

    /// <summary>手动页元件定义（气缸 / 按钮 / 指示灯）。</summary>
    public static string 手动元件 => Path.Combine(配置目录, "manual-widgets.json");

    /// <summary>用户账号（只存 PBKDF2 哈希与盐，不存明文）。</summary>
    public static string 用户 => Path.Combine(配置目录, "users.json");

    /// <summary>报警定义：PLC 地址到报警文本的映射。</summary>
    public static string 报警集合 => Path.Combine(配置目录, "alarms.json");

    /// <summary>参数页自定义表格（延时表 / 按钮表）。</summary>
    public static string 参数表格 => Path.Combine(配置目录, "param-tables.json");

    /// <summary>汇川变量表（由变量表 CSV 导入生成；首次导入前不存在，属正常）。</summary>
    public static string 变量表 => Path.Combine(配置目录, "variables.json");

    /// <summary>历史记录采集配置：触发位 + 数据体（表格各列）定义。</summary>
    public static string 历史记录配置 => Path.Combine(配置目录, "history-record.json");

    /// <summary>辅助点（其它点）定义主存。</summary>
    public static string 其它点 => Path.Combine(配置目录, "aux-points.json");

    /// <summary>日志目录。</summary>
    public static string 日志目录 => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>
    /// 出厂配置模板目录（发布包里带的是它，<b>不是</b> <c>config/</c>）。
    ///
    /// <para><b>为什么这样设计：</b>现场升级是"把新版本目录覆盖到原程序目录"。
    /// 若发布包里直接带 <c>config/</c>，覆盖时会把现场调好的机种（<c>recipes.json</c>）、
    /// PLC 连接参数（<c>appsettings.json</c>）、账号、点位表一并冲掉——那是生产事故。
    /// 所以发布包只带模板，程序启动时<b>只补齐缺失的文件、绝不覆盖已存在的</b>
    /// （见 <see cref="补齐缺失配置"/>）。覆盖升级因此天然是安全的。</para>
    /// </summary>
    public static string 出厂模板目录 { get; } =
        Path.Combine(AppContext.BaseDirectory, "出厂配置");

    /// <summary>
    /// 运行期数据目录（区别于"配置"：这些是程序自己产生的、断电后仍需保留的数据）。
    /// <para>与 <c>config/</c> 分开，是为了让"现场要改的文件"与"程序自己写的文件"不混在一起——
    /// 备份或覆盖 config/ 时不会顺手把历史数据一起干掉。</para>
    /// </summary>
    public static string 数据目录 { get; } =
        Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>报警历史（跨重启保留，用于交接班追溯）。</summary>
    public static string 报警历史 => Path.Combine(数据目录, "alarm-history.json");

    /// <summary>产量小时采样历史（支撑运行页产量图表，跨重启保留近 8 天）。</summary>
    public static string 产量历史 => Path.Combine(数据目录, "production-history.json");

    /// <summary>
    /// 历史记录数据目录（<b>一天一个文件</b>：<c>yyyy-MM-dd.json</c>）。
    ///
    /// <para>不合成一个大文件的原因有三：按期翻页只需读一天的文件；
    /// 每天写入不重写全量历史；清除历史就是删文件，不必"读入—过滤—写回"。</para>
    /// </summary>
    public static string 历史记录目录 => Path.Combine(数据目录, "history-records");

    /// <summary>某个日期的历史记录文件路径。</summary>
    public static string 历史记录文件(DateTime 日期) =>
        Path.Combine(历史记录目录, $"{日期:yyyy-MM-dd}.json");

    /// <summary>确保配置目录存在。
    /// <para>出厂配置的补齐是独立一步（<see cref="补齐缺失配置"/>），由启动流程显式调用，
    /// 这样补齐清单可以进日志与启动提示。</para>
    /// </summary>
    public static void 确保已创建()
    {
        Directory.CreateDirectory(配置目录);
        Directory.CreateDirectory(数据目录);
    }

    /// <summary>
    /// 把 <see cref="出厂模板目录"/> 里存在、而 <see cref="配置目录"/> 里缺失的文件复制过去。
    ///
    /// <para><b>只补齐、绝不覆盖</b>——这是"覆盖升级不丢现场数据"的关键：
    /// 新版本新增的配置文件会自动出现；现场改过的机种、PLC 连接参数、账号、点位表
    /// 一律保持原样。若模板目录不存在（开发机 / 从源码运行），本方法什么都不做。</para>
    /// </summary>
    /// <returns>本次补齐的文件名清单（供日志与启动横幅使用）。</returns>
    public static IReadOnlyList<string> 补齐缺失配置()
    {
        var 已补齐 = new List<string>();

        if (!Directory.Exists(出厂模板目录))
        {
            return 已补齐;
        }

        foreach (var 模板文件 in Directory.EnumerateFiles(出厂模板目录, "*", SearchOption.AllDirectories))
        {
            var 相对 = Path.GetRelativePath(出厂模板目录, 模板文件);
            var 目标 = Path.Combine(配置目录, 相对);

            if (File.Exists(目标))
            {
                continue;
            }

            var 目标目录 = Path.GetDirectoryName(目标);
            if (!string.IsNullOrEmpty(目标目录))
            {
                Directory.CreateDirectory(目标目录);
            }

            // 复制失败不该挡住启动：缺配置的后果界面上会以"配置降级横幅"呈现
            try
            {
                File.Copy(模板文件, 目标);
                已补齐.Add(相对);
            }
            catch (IOException)
            {
                // 磁盘/占用问题：留待加载器按"缺失"处理
            }
            catch (UnauthorizedAccessException)
            {
                // 目录不可写：启动横幅会给出可读原因（见 验证可写）
            }
        }

        return 已补齐;
    }

    /// <summary>
    /// 启动期可写性预检。
    ///
    /// <para><b>为什么必须在启动时查：</b>程序以 <c>asInvoker</c> 运行，配置与数据都写在
    /// 程序目录下。若现场把程序装到 <c>C:\Program Files\</c> 这类普通用户不可写的路径，
    /// 一切都要等到"操作员按下保存"才失败 —— 那时人已经在设备前面等着了。
    /// 提前查出来，就能在启动横幅上直接说明原因。</para>
    /// </summary>
    /// <returns>不可写的目录描述；全部可写时返回空列表。</returns>
    public static IReadOnlyList<string> 验证可写()
    {
        var 问题 = new List<string>();

        foreach (var (名称值, 目录) in new[]
                 {
                     ("配置目录", 配置目录),
                     ("数据目录", 数据目录),
                     ("日志目录", 日志目录),
                 })
        {
            try
            {
                Directory.CreateDirectory(目录);

                var 探查 = Path.Combine(目录, $".write-probe-{Guid.NewGuid():N}");
                File.WriteAllText(探查, "probe");
                File.Delete(探查);
            }
            catch (IOException 异常)
            {
                问题.Add($"{名称值}不可写（{目录}）：{异常.Message}");
            }
            catch (UnauthorizedAccessException 异常)
            {
                问题.Add($"{名称值}不可写（{目录}）：{异常.Message}");
            }
        }

        return 问题;
    }
}

/// <summary>统一的 JSON 序列化设置。</summary>
public static class JSON配置
{
    /// <summary>读取用设置：容忍注释与尾随逗号，枚举按字符串解析且忽略大小写。</summary>
    public static readonly JsonSerializerOptions 读取 = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) },
    };

    /// <summary>写入用设置：缩进输出、中文不转义，便于人工编辑。</summary>
    /// <remarks>
    /// <b>IgnoreReadOnlyProperties 是必须的（2026-10-21）：</b>模型里有一批"派生属性"
    /// （<c>寄存器数量</c> / <c>显示名称</c> / <c>是开关</c> / <c>是结构体数组</c> /
    /// <c>字每点位</c> / <c>范围文本</c> …）。它们只有 getter，默认会被一起写进配置文件，
    /// 于是现场手改 JSON 时会看到<b>自相矛盾</b>的内容——例如
    /// <c>"结构体长度": 8</c> 旁边躺着 <c>"是结构体数组": false</c>（上次保存时的旧值），
    /// 让人以为新功能没生效。派生值一律不入盘，文件只留"真配置"。
    /// </remarks>
    public static readonly JsonSerializerOptions 写入 = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        IgnoreReadOnlyProperties = true,
    };

    /// <summary>
    /// 写入用设置（camelCase）：专给<b>人工手写</b>的配置文件（<c>recipe-config.json</c>、
    /// <c>tags.json</c>）用。
    ///
    /// <para><b>为什么要单独一份</b>：<see cref="写入"/> 不带命名策略，写出的是 PascalCase。
    /// 用程序保存一次这两份文件，就会把现场工程师习惯的 <c>"address"</c> 全变成 <c>"Address"</c>——
    /// 功能上仍能读（<see cref="读取"/> 忽略大小写），但文件风格被程序悄悄改掉，
    /// 人工对照文档时会以为看错了。</para>
    /// </summary>
    public static readonly JsonSerializerOptions 写入人工风格 = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
