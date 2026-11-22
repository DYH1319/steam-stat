using ElectronNet.Enums;
using ElectronNet.Features.Achievements.Persistence;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.Planner.Persistence;

internal sealed class GameJourneyConfiguration : IEntityTypeConfiguration<GameJourney>
{
    public void Configure(EntityTypeBuilder<GameJourney> builder)
    {
        builder.ToTable("game_journey", t => t.HasComment("游戏规划：开坑、完成预期、搁置、队列与完成评价（每个应用一行，不分账号）"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.AppId, "game_journey_app_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.StartedAt).Column("started_at", ESqliteTypeName.INTEGER, "开坑时间（Unix 时间戳，秒）");
        builder.Property(e => e.GoalKind).Column("goal_kind", ESqliteTypeName.TEXT, "完成预期（story/storyPlus/achievements/playtime/custom/none）").HasMaxLength(16);
        builder.Property(e => e.GoalHours).Column("goal_hours", ESqliteTypeName.INTEGER, "时长目标（小时）");
        builder.Property(e => e.GoalAchievementPercent).Column("goal_achievement_percent", ESqliteTypeName.INTEGER, "成就目标（%）");
        builder.Property(e => e.GoalNote).Column("goal_note", ESqliteTypeName.TEXT, "完成预期说明").HasMaxLength(500);
        builder.Property(e => e.Milestones).Column("milestones", ESqliteTypeName.TEXT, "里程碑清单（JSON）").HasMaxLength(20000);
        builder.Property(e => e.CompletionLevel).Column("completion_level", ESqliteTypeName.TEXT, "完成程度（beaten/goalReached/perfect）").HasMaxLength(16);
        builder.Property(e => e.ShelvedReason).Column("shelved_reason", ESqliteTypeName.TEXT, "搁置原因").HasMaxLength(16);
        builder.Property(e => e.ResumeAt).Column("resume_at", ESqliteTypeName.INTEGER, "回坑提醒时间（Unix 时间戳，秒）");
        builder.Property(e => e.ResumeNote).Column("resume_note", ESqliteTypeName.TEXT, "回坑时要做什么").HasMaxLength(500);
        builder.Property(e => e.QueueOrder).Column("queue_order", ESqliteTypeName.REAL, "「下一个」队列顺序");
        builder.Property(e => e.ReviewScores).Column("review_scores", ESqliteTypeName.TEXT, "分项评分（JSON）").HasMaxLength(500);
        builder.Property(e => e.ReviewSummary).Column("review_summary", ESqliteTypeName.TEXT, "一句话评价").HasMaxLength(280);
        builder.Property(e => e.Recommend).Column("recommend", ESqliteTypeName.INTEGER, "是否推荐");
        builder.Property(e => e.WouldReplay).Column("would_replay", ESqliteTypeName.INTEGER, "是否会重玩");
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "修改时间（Unix 时间戳，秒）").IsRequired();
    }
}

internal sealed class GameJournalEntryConfiguration : IEntityTypeConfiguration<GameJournalEntry>
{
    public void Configure(EntityTypeBuilder<GameJournalEntry> builder)
    {
        builder.ToTable("game_journal", t => t.HasComment("游戏心得"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.AppId, e.CreatedAt }, "game_journal_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "类型（note/shelve/milestone/review/resume）").HasMaxLength(16).IsRequired();
        builder.Property(e => e.Body).Column("body", ESqliteTypeName.TEXT, "正文（Markdown）").HasMaxLength(20000).IsRequired();
        builder.Property(e => e.Spoiler).Column("spoiler", ESqliteTypeName.INTEGER, "含剧透").IsRequired();
        builder.Property(e => e.Mood).Column("mood", ESqliteTypeName.TEXT, "心情").HasMaxLength(16);
        builder.Property(e => e.CreatedAt).Column("created_at", ESqliteTypeName.INTEGER, "写下的时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "修改时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.SyncUid).Column("sync_uid", ESqliteTypeName.TEXT, "多设备同步 ID").HasMaxLength(32);
        builder.HasIndex(e => e.SyncUid, "game_journal_sync_uid_uidx").IsUnique();
    }
}

internal sealed class GamePlaythroughConfiguration : IEntityTypeConfiguration<GamePlaythrough>
{
    public void Configure(EntityTypeBuilder<GamePlaythrough> builder)
    {
        builder.ToTable("game_playthrough", t => t.HasComment("游戏周目"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.AppId, "game_playthrough_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.Label).Column("label", ESqliteTypeName.TEXT, "名称（一周目、DLC……）").HasMaxLength(80);
        builder.Property(e => e.StartedAt).Column("started_at", ESqliteTypeName.INTEGER, "开始时间（Unix 时间戳，秒）");
        builder.Property(e => e.EndedAt).Column("ended_at", ESqliteTypeName.INTEGER, "结束时间（Unix 时间戳，秒）");
        builder.Property(e => e.Outcome).Column("outcome", ESqliteTypeName.TEXT, "结果（ongoing/completed/abandoned）").HasMaxLength(16).IsRequired();
        builder.Property(e => e.CompletionLevel).Column("completion_level", ESqliteTypeName.TEXT, "完成程度").HasMaxLength(16);
        builder.Property(e => e.PlaytimeMinutes).Column("playtime_minutes", ESqliteTypeName.INTEGER, "用时（分钟）");
        builder.Property(e => e.Rating).Column("rating", ESqliteTypeName.INTEGER, "这一周目的评分（1~10）");
        builder.Property(e => e.Note).Column("note", ESqliteTypeName.TEXT, "备注").HasMaxLength(2000);
        builder.Property(e => e.CreatedAt).Column("created_at", ESqliteTypeName.INTEGER, "创建时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "修改时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.SyncUid).Column("sync_uid", ESqliteTypeName.TEXT, "多设备同步 ID").HasMaxLength(32);
        builder.HasIndex(e => e.SyncUid, "game_playthrough_sync_uid_uidx").IsUnique();
    }
}

internal sealed class GameTagConfiguration : IEntityTypeConfiguration<GameTag>
{
    public void Configure(EntityTypeBuilder<GameTag> builder)
    {
        builder.ToTable("game_tag", t => t.HasComment("自定义游戏分类标签"));
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.Name).Column("name", ESqliteTypeName.TEXT, "名称").HasMaxLength(24).IsRequired();
        builder.Property(e => e.Color).Column("color", ESqliteTypeName.TEXT, "颜色").HasMaxLength(16).IsRequired();
        builder.Property(e => e.Icon).Column("icon", ESqliteTypeName.TEXT, "图标").HasMaxLength(64);
        builder.Property(e => e.SortOrder).Column("sort_order", ESqliteTypeName.INTEGER, "排序").IsRequired();
        builder.Property(e => e.CreatedAt).Column("created_at", ESqliteTypeName.INTEGER, "创建时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "修改时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.SyncUid).Column("sync_uid", ESqliteTypeName.TEXT, "多设备同步 ID").HasMaxLength(32);
        builder.HasIndex(e => e.SyncUid, "game_tag_sync_uid_uidx").IsUnique();
    }
}

internal sealed class GameTagLinkConfiguration : IEntityTypeConfiguration<GameTagLink>
{
    public void Configure(EntityTypeBuilder<GameTagLink> builder)
    {
        builder.ToTable("game_tag_link", t => t.HasComment("标签与游戏的关联"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.TagUid, e.AppId }, "game_tag_link_uidx").IsUnique();
        builder.HasIndex(e => e.AppId, "game_tag_link_app_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.TagUid).Column("tag_uid", ESqliteTypeName.TEXT, "标签的同步 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID").IsRequired();
        builder.Property(e => e.CreatedAt).Column("created_at", ESqliteTypeName.INTEGER, "创建时间（Unix 时间戳，秒）").IsRequired();
    }
}

internal sealed class PlayPlanConfiguration : IEntityTypeConfiguration<PlayPlan>
{
    public void Configure(EntityTypeBuilder<PlayPlan> builder)
    {
        builder.ToTable("play_plan", t => t.HasComment("游玩计划"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.StartDay, "play_plan_start_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.AppId).Column("app_id", ESqliteTypeName.INTEGER, "应用 ID");
        builder.Property(e => e.Title).Column("title", ESqliteTypeName.TEXT, "标题").HasMaxLength(80);
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "类型（day/range/bucket）").HasMaxLength(16).IsRequired();
        builder.Property(e => e.StartDay).Column("start_day", ESqliteTypeName.INTEGER, "开始的逻辑日（距 1970-01-01 的天数）").IsRequired();
        builder.Property(e => e.EndDay).Column("end_day", ESqliteTypeName.INTEGER, "结束的逻辑日，单日计划为空");
        builder.Property(e => e.Bucket).Column("bucket", ESqliteTypeName.TEXT, "粗略时段（week/month/quarter/year）").HasMaxLength(16);
        builder.Property(e => e.EstimatedMinutes).Column("estimated_minutes", ESqliteTypeName.INTEGER, "预计时长（分钟）");
        builder.Property(e => e.Status).Column("status", ESqliteTypeName.TEXT, "状态（pending/done/skipped）").HasMaxLength(16).IsRequired();
        builder.Property(e => e.Note).Column("note", ESqliteTypeName.TEXT, "备注").HasMaxLength(2000);
        builder.Property(e => e.CreatedAt).Column("created_at", ESqliteTypeName.INTEGER, "创建时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "修改时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.SyncUid).Column("sync_uid", ESqliteTypeName.TEXT, "多设备同步 ID").HasMaxLength(32);
        builder.HasIndex(e => e.SyncUid, "play_plan_sync_uid_uidx").IsUnique();
    }
}

internal sealed class PlayChallengeConfiguration : IEntityTypeConfiguration<PlayChallenge>
{
    public void Configure(EntityTypeBuilder<PlayChallenge> builder)
    {
        builder.ToTable("play_challenge", t => t.HasComment("年度挑战"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Year, "play_challenge_year_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.Year).Column("year", ESqliteTypeName.INTEGER, "年份").IsRequired();
        builder.Property(e => e.Kind).Column("kind", ESqliteTypeName.TEXT, "类型").HasMaxLength(16).IsRequired();
        builder.Property(e => e.Target).Column("target", ESqliteTypeName.INTEGER, "目标数量").IsRequired();
        builder.Property(e => e.Title).Column("title", ESqliteTypeName.TEXT, "标题").HasMaxLength(80);
        builder.Property(e => e.CreatedAt).Column("created_at", ESqliteTypeName.INTEGER, "创建时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "修改时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.SyncUid).Column("sync_uid", ESqliteTypeName.TEXT, "多设备同步 ID").HasMaxLength(32);
        builder.HasIndex(e => e.SyncUid, "play_challenge_sync_uid_uidx").IsUnique();
    }
}
