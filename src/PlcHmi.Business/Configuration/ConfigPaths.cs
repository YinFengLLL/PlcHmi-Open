using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcHmi.Business.Configuration;

/// <summary>配置文件路径解析。配置文件放在程序目录下的 <c>config/</c>，便于现场修改而无需重新编译。</summary>
public static class ConfigPath
{
    /// <summary>配置目录。</summary>
    public static string ConfigDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "config");

    /// <summary>主配置文件。</summary>
    public static string AppSettings => Path.Combine(ConfigDirectory, "appsettings.json");

    /// <summary>业务点位表。</summary>
    public static string TagCollection => Path.Combine(ConfigDirectory, "tags.json");

    /// <summary>IO 点位表。</summary>
    public static string IoPointCollection => Path.Combine(ConfigDirectory, "io-points.json");

    /// <summary>机种（配方）结构化配置：参数项定义与全部基址。</summary>
    public static string RecipeConfig => Path.Combine(ConfigDirectory, "recipe-config.json");

    /// <summary>机种数据主存：每个机种一份 D2500–D3999 区段快照。</summary>
    public static string RecipeCollection => Path.Combine(ConfigDirectory, "recipes.json");

    /// <summary>
    /// 全局参数主存（轴速度等）。
    /// <para>机种区段（D2500–D3999）之外的 D 区数据"全局记忆"，不随机种切换，
    /// 因此不再存进机种条目里（见 <see cref="PlcHmi.Core.Models.MachineModelSection"/>）。</para>
    /// </summary>
    public static string ParameterCollection => Path.Combine(ConfigDirectory, "parameters.json");

    /// <summary>手动页元件定义（气缸 / 按钮 / 指示灯）。</summary>
    public static string ManualComponent => Path.Combine(ConfigDirectory, "manual-widgets.json");

    /// <summary>用户账号（只存 PBKDF2 哈希与盐，不存明文）。</summary>
    public static string User => Path.Combine(ConfigDirectory, "users.json");

    /// <summary>报警定义：PLC 地址到报警文本的映射。</summary>
    public static string AlarmCollection => Path.Combine(ConfigDirectory, "alarms.json");

    /// <summary>参数页自定义表格（延时表 / 按钮表）。</summary>
    public static string ParameterTable => Path.Combine(ConfigDirectory, "param-tables.json");

    /// <summary>汇川变量表（由变量表 CSV 导入生成；首次导入前不存在，属正常）。</summary>
    public static string VariableTable => Path.Combine(ConfigDirectory, "variables.json");

    /// <summary>历史记录采集配置：触发位 + 数据体（表格各列）定义。</summary>
    public static string HistoryRecordConfig => Path.Combine(ConfigDirectory, "history-record.json");

    /// <summary>辅助点（其它点）定义主存。</summary>
    public static string OtherPoint => Path.Combine(ConfigDirectory, "aux-points.json");

    /// <summary>日志目录。</summary>
    public static string LogDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>
    /// 出厂配置模板目录（发布包里带的是它，<b>不是</b> <c>config/</c>）。
    ///
    /// <para><b>为什么这样设计：</b>现场升级是"把新版本目录覆盖到原程序目录"。
    /// 若发布包里直接带 <c>config/</c>，覆盖时会把现场调好的机种（<c>recipes.json</c>）、
    /// PLC 连接参数（<c>appsettings.json</c>）、账号、点位表一并冲掉——那是生产事故。
    /// 所以发布包只带模板，程序启动时<b>只补齐缺失的文件、绝不覆盖已存在的</b>
    /// （见 <see cref="FillMissingConfig"/>）。覆盖升级因此天然是安全的。</para>
    /// </summary>
    public static string FactoryTemplateDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "出厂配置");

    /// <summary>
    /// 运行期数据目录（区别于"配置"：这些是程序自己产生的、断电后仍需保留的数据）。
    /// <para>与 <c>config/</c> 分开，是为了让"现场要改的文件"与"程序自己写的文件"不混在一起——
    /// 备份或覆盖 config/ 时不会顺手把历史数据一起干掉。</para>
    /// </summary>
    public static string DataDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>报警历史（跨重启保留，用于交接班追溯）。</summary>
    public static string AlarmHistory => Path.Combine(DataDirectory, "alarm-history.json");

    /// <summary>产量小时采样历史（支撑运行页产量图表，跨重启保留近 8 天）。</summary>
    public static string OutputHistory => Path.Combine(DataDirectory, "production-history.json");

    /// <summary>
    /// 历史记录数据目录（<b>一天一个文件</b>：<c>yyyy-MM-dd.json</c>）。
    ///
    /// <para>不合成一个大文件的原因有三：按期翻页只需读一天的文件；
    /// 每天写入不重写全量历史；清除历史就是删文件，不必"读入—过滤—写回"。</para>
    /// </summary>
    public static string HistoryRecordDirectory => Path.Combine(DataDirectory, "history-records");

    /// <summary>某个日期的历史记录文件路径。</summary>
    public static string HistoryRecordFile(DateTime Date) =>
        Path.Combine(HistoryRecordDirectory, $"{Date:yyyy-MM-dd}.json");

    /// <summary>确保配置目录存在。
    /// <para>出厂配置的补齐是独立一步（<see cref="FillMissingConfig"/>），由启动流程显式调用，
    /// 这样补齐清单可以进日志与启动提示。</para>
    /// </summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
    }

    /// <summary>
    /// 把 <see cref="FactoryTemplateDirectory"/> 里存在、而 <see cref="ConfigDirectory"/> 里缺失的文件复制过去。
    ///
    /// <para><b>只补齐、绝不覆盖</b>——这是"覆盖升级不丢现场数据"的关键：
    /// 新版本新增的配置文件会自动出现；现场改过的机种、PLC 连接参数、账号、点位表
    /// 一律保持原样。若模板目录不存在（开发机 / 从源码运行），本方法什么都不做。</para>
    /// </summary>
    /// <returns>本次补齐的文件名清单（供日志与启动横幅使用）。</returns>
    public static IReadOnlyList<string> FillMissingConfig()
    {
        var filled = new List<string>();

        if (!Directory.Exists(FactoryTemplateDirectory))
        {
            return filled;
        }

        foreach (var templateFile in Directory.EnumerateFiles(FactoryTemplateDirectory, "*", SearchOption.AllDirectories))
        {
            var mutualPair = Path.GetRelativePath(FactoryTemplateDirectory, templateFile);
            var target = Path.Combine(ConfigDirectory, mutualPair);

            if (File.Exists(target))
            {
                continue;
            }

            var targetDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // 复制失败不该挡住启动：缺配置的后果界面上会以"配置降级横幅"呈现
            try
            {
                File.Copy(templateFile, target);
                filled.Add(mutualPair);
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

        return filled;
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
    public static IReadOnlyList<string> VerifyWritable()
    {
        var Issue = new List<string>();

        foreach (var (nameValue, dir) in new[]
                 {
                     ("配置目录", ConfigDirectory),
                     ("数据目录", DataDirectory),
                     ("日志目录", LogDirectory),
                 })
        {
            try
            {
                Directory.CreateDirectory(dir);

                var probeCheck = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probeCheck, "probe");
                File.Delete(probeCheck);
            }
            catch (IOException exception)
            {
                Issue.Add($"{nameValue}不可写（{dir}）：{exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                Issue.Add($"{nameValue}不可写（{dir}）：{exception.Message}");
            }
        }

        return Issue;
    }
}

/// <summary>统一的 JSON 序列化设置。</summary>
public static class JsonConfig
{
    /// <summary>读取用设置：容忍注释与尾随逗号，枚举按字符串解析且忽略大小写。</summary>
    public static readonly JsonSerializerOptions Read = new()
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
    public static readonly JsonSerializerOptions Write = new()
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
    /// <para><b>为什么要单独一份</b>：<see cref="Write"/> 不带命名策略，写出的是 PascalCase。
    /// 用程序保存一次这两份文件，就会把现场工程师习惯的 <c>"address"</c> 全变成 <c>"Address"</c>——
    /// 功能上仍能读（<see cref="Read"/> 忽略大小写），但文件风格被程序悄悄改掉，
    /// 人工对照文档时会以为看错了。</para>
    /// </summary>
    public static readonly JsonSerializerOptions writeHumanStyle = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
