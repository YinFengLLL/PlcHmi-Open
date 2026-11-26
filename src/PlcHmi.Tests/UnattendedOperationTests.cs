using Microsoft.Extensions.Logging.Abstractions;
using PlcHmi.Business.Configuration;
using PlcHmi.Business.Services;
using PlcHmi.Communication.Inovance;
using PlcHmi.Core.Enums;
using PlcHmi.Core.Models;
using PlcHmi.Tests.Fake;

namespace PlcHmi.Tests;

/// <summary>
/// 无人值守相关的行为测试：报警历史落盘、全部确认的一致性、目录可写性预检。
///
/// <para><b>为什么这一组重要：</b>上位机替代触控屏后是无人值守长跑的。
/// 报警历史只存内存 → 换班查不到上一班故障；"全部确认"只改一半 →
/// 同一个界面有两种说法。这两条都属于"开发机上永远看不出来"的问题。</para>
/// </summary>
// 直接增删真实的报警历史文件与数据目录，与其它碰配置文件的测试互斥，禁止并行
[Collection("config-files")]
public class UnattendedOperationTest
{
    private static TagWriter NewWriter(FakeModbusClient Client) =>
        new(Client,
            new TagRepository(NullLogger<TagRepository>.Instance),
            new InovanceAddressParser(PlcSeries.SmallPlc),
            NullLogger<TagWriter>.Instance);

    /// <summary>备份真实的历史文件，返回还原动作。</summary>
    private static Action BackupHistory()
    {
        var filePath = ConfigPath.AlarmHistory;
        var Original = File.Exists(filePath) ? File.ReadAllText(filePath) : null;

        return () =>
        {
            if (Original is null)
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            else
            {
                File.WriteAllText(filePath, Original);
            }
        };
    }

    /// <summary>等待后台落盘完成。</summary>
    private static void WaitFile(string filePath, int TimeoutMilliseconds = 5000)
    {
        var truncateStopTime = DateTime.Now.AddMilliseconds(TimeoutMilliseconds);

        while (DateTime.Now < truncateStopTime && !File.Exists(filePath))
        {
            Thread.Sleep(20);
        }
    }

    // ── 报警历史落盘 ────────────────────────────────────────────────────

    [Fact]
    public void AlarmHistoryShouldBeAbleToPersistAndReReadBack()
    {
        var Recover = BackupHistory();

        try
        {
            using (var Store = new AlarmHistoryStore(NullLogger<AlarmHistoryStore>.Instance))
            {
                Store.RequestSave(
                [
                    new AlarmEntry
                    {
                        Code = "A001",
                        Message = "急停被按下",
                        Level = AlarmLevel.Critical,
                        Triggered = new DateTime(2026, 9, 13, 8, 30, 0),
                    },
                ]);

                // 落盘在后台线程，等它写完再释放（Dispose 会再刷一次，双保险）
                WaitFile(ConfigPath.AlarmHistory);
            }

            using var reload = new AlarmHistoryStore(NullLogger<AlarmHistoryStore>.Instance);
            var reloaded = reload.Load();

            Assert.Single(reloaded);
            Assert.Equal("A001", reloaded[0].Code);
            Assert.Equal(AlarmLevel.Critical, reloaded[0].Level);
        }
        finally
        {
            Recover();
        }
    }

    [Fact]
    public void HistoryFileMissingWhenShouldReturnEmptyListInsteadOfThrowException()
    {
        var Recover = BackupHistory();

        try
        {
            if (File.Exists(ConfigPath.AlarmHistory))
            {
                File.Delete(ConfigPath.AlarmHistory);
            }

            using var Store = new AlarmHistoryStore(NullLogger<AlarmHistoryStore>.Instance);

            Assert.Empty(Store.Load());
        }
        finally
        {
            Recover();
        }
    }

    // ── 报警服务：跨重启恢复 ────────────────────────────────────────────

    [Fact]
    public async Task AlarmProduceAfterHistoryShouldBeAbleToBeNewRealCaseReadBack()
    {
        var Recover = BackupHistory();

        try
        {
            await using (var Client = new FakeModbusClient())
            using (var History = new AlarmHistoryStore(NullLogger<AlarmHistoryStore>.Instance))
            {
                var first = new AlarmService(NewWriter(Client), NullLogger<AlarmService>.Instance, History);

                first.Trigger("A010", "压装压力偏低", AlarmLevel.Warning);
            }

            WaitFile(ConfigPath.AlarmHistory);

            // 模拟"重启"：新实例应当把上一班的报警读回来
            await using var client2 = new FakeModbusClient();
            using var history2 = new AlarmHistoryStore(NullLogger<AlarmHistoryStore>.Instance);

            var noTwo = new AlarmService(NewWriter(client2), NullLogger<AlarmService>.Instance, history2);

            Assert.Contains(noTwo.History, historyEntry => historyEntry.Code == "A010");
        }
        finally
        {
            Recover();
        }
    }

    // ── 报警一致性 ──────────────────────────────────────────────────────

    [Fact]
    public async Task AllConfirmShouldSameWhenUpdateHistoryAvoidAvoidSameOneUiTwoKindSayMethod()
    {
        await using var Client = new FakeModbusClient();

        // 不传 historyStore：本用例只验证内存一致性
        var serviceInstance = new AlarmService(NewWriter(Client), NullLogger<AlarmService>.Instance);

        serviceInstance.Trigger("A020", "气缸超时", AlarmLevel.Warning);
        serviceInstance.ConfirmAll();

        Assert.True(serviceInstance.ActiveAlarm[0].IsAlreadyConfirm);

        // 历史里的同一条也必须已确认 —— 此前 AcknowledgeAll 只改 _active，
        // 导致"全部确认"之后历史列表仍显示未确认
        Assert.True(
            serviceInstance.History.First(historyEntry => historyEntry.Code == "A020").IsAlreadyConfirm,
            "全部确认必须同步历史，否则同一件事在同一个界面上有两种说法");
    }

    [Fact]
    public async Task SingleItemConfirmShouldSameWhenUpdateHistory()
    {
        await using var Client = new FakeModbusClient();
        var serviceInstance = new AlarmService(NewWriter(Client), NullLogger<AlarmService>.Instance);

        serviceInstance.Trigger("A021", "测试", AlarmLevel.Warning);
        serviceInstance.Confirm("A021");

        Assert.True(serviceInstance.History.First(historyEntry => historyEntry.Code == "A021").IsAlreadyConfirm);
    }

    [Fact]
    public async Task WithoutHistoryStoreWhenShouldNotWriteDiskAndShouldNormalWorkOperation()
    {
        var Recover = BackupHistory();

        try
        {
            if (File.Exists(ConfigPath.AlarmHistory))
            {
                File.Delete(ConfigPath.AlarmHistory);
            }

            await using var Client = new FakeModbusClient();
            var serviceInstance = new AlarmService(NewWriter(Client), NullLogger<AlarmService>.Instance);

            serviceInstance.Trigger("A030", "内存模式", AlarmLevel.Info);
            serviceInstance.ClearEmptyHistory();

            Assert.Empty(serviceInstance.History);
            Assert.False(File.Exists(ConfigPath.AlarmHistory), "未注入存储时不应产生磁盘文件");
        }
        finally
        {
            Recover();
        }
    }

    // ── 目录可写性预检 ──────────────────────────────────────────────────

    [Fact]
    public void WritableEnvironmentDownPreCheckShouldHaveNoIssueAndBuildGoodDataDirectory()
    {
        var Issue = ConfigPath.VerifyWritable();

        Assert.Empty(Issue);
        Assert.True(Directory.Exists(ConfigPath.DataDirectory), "预检应顺便把数据目录建出来");
    }
}
