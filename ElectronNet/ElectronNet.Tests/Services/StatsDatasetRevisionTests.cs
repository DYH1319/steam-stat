using ElectronNet.Models;
using ElectronNet.Services;
using ElectronNet.Tests.Features.Insights;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElectronNet.Tests.Services;

/// <summary>
/// 数据集修订号：渲染层定时刷新前先比它。进行中会话的心跳不能让它变（否则托盘常驻时每十分钟还是整份重载），
/// 会话结束、清洗规则变化必须让它变。
/// </summary>
[TestFixture]
public sealed class StatsDatasetRevisionTests : InsightHostFixture
{
    private StatsDatasetService _service = null!;

    [SetUp]
    public void SetUpService()
        => _service = new StatsDatasetService(DbFactory, null!, null!, null!, NullLogger<StatsDatasetService>.Instance);

    [Test]
    public async Task Revision_IgnoresHeartbeatsOfRunningSessionsButFollowsFinishedOnes()
    {
        await using (var db = await DbFactory.CreateDbContextAsync())
        {
            db.UseAppRecordTable.Add(new UseAppRecord { AppId = 10, SteamId = Me, StartTime = 1000, EndTime = 4600, Duration = 3600 });
            db.UseAppRecordTable.Add(new UseAppRecord { AppId = 20, SteamId = Me, StartTime = 5000, LastHeartbeat = 5010 });
            await db.SaveChangesAsync();
        }
        var initial = _service.GetRevision("rules").Revision;

        await using (var db = await DbFactory.CreateDbContextAsync())
        {
            var running = db.UseAppRecordTable.Single(record => record.EndTime == null);
            running.LastHeartbeat = 5600;
            running.IdleDuration = 30;
            await db.SaveChangesAsync();
        }
        _service.GetRevision("rules").Revision.Should().Be(initial);

        await using (var db = await DbFactory.CreateDbContextAsync())
        {
            var running = db.UseAppRecordTable.Single(record => record.EndTime == null);
            running.EndTime = 6000;
            running.Duration = 1000;
            await db.SaveChangesAsync();
        }
        var finished = _service.GetRevision("rules").Revision;
        finished.Should().NotBe(initial);

        // 清洗规则（最短时长、合并间隔、排除的应用）变了，同样的记录算出来的数据集也不同
        _service.GetRevision("other rules").Revision.Should().NotBe(finished);
    }

    [Test]
    public void Revision_OfAnEmptyArchiveIsStable()
        => _service.GetRevision("rules").Revision.Should().Be(_service.GetRevision("rules").Revision).And.HaveLength(16);
}
