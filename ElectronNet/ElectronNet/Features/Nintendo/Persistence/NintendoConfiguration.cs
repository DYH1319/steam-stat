using ElectronNet.Enums;
using ElectronNet.Features.Achievements.Persistence;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.Nintendo.Persistence;

internal sealed class NintendoAccountConfiguration : IEntityTypeConfiguration<NintendoAccount>
{
    public void Configure(EntityTypeBuilder<NintendoAccount> builder)
    {
        builder.ToTable("nintendo_account", t => t.HasComment("绑定过的任天堂账号"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.NaId, "nintendo_account_na_id_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.NaId).Column("na_id", ESqliteTypeName.TEXT, "任天堂账号 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Nickname).Column("nickname", ESqliteTypeName.TEXT, "昵称").HasMaxLength(64).IsRequired();
        builder.Property(e => e.IconUrl).Column("icon_url", ESqliteTypeName.TEXT, "头像地址").HasMaxLength(1000);
        builder.Property(e => e.Country).Column("country", ESqliteTypeName.TEXT, "地区").HasMaxLength(8);
        builder.Property(e => e.Mine).Column("mine", ESqliteTypeName.INTEGER, "属于用户本人").IsRequired();
        builder.Property(e => e.LinkedAt).Column("linked_at", ESqliteTypeName.INTEGER, "绑定时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.LastSyncAt).Column("last_sync_at", ESqliteTypeName.INTEGER, "本机最近一次读取时间");
        builder.Property(e => e.LastSuccessAt).Column("last_success_at", ESqliteTypeName.INTEGER, "本机最近一次读取成功的时间");
        builder.Property(e => e.LastError).Column("last_error", ESqliteTypeName.TEXT, "本机最近一次读取的错误码").HasMaxLength(16);
    }
}

internal sealed class NintendoCredentialConfiguration : IEntityTypeConfiguration<NintendoCredential>
{
    public void Configure(EntityTypeBuilder<NintendoCredential> builder)
    {
        builder.ToTable("nintendo_credential", t => t.HasComment("任天堂账号的登录凭证（加密，只属于本机）"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.NaId, "nintendo_credential_na_id_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.NaId).Column("na_id", ESqliteTypeName.TEXT, "任天堂账号 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.SessionToken).Column("session_token", ESqliteTypeName.TEXT, "加密后的 session token").IsRequired();
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "写入时间（Unix 时间戳，秒）").IsRequired();
    }
}

internal sealed class NintendoTitleConfiguration : IEntityTypeConfiguration<NintendoTitle>
{
    public void Configure(EntityTypeBuilder<NintendoTitle> builder)
    {
        builder.ToTable("nintendo_title", t => t.HasComment("Nintendo 游戏名称和封面"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.TitleId, e.Platform }, "nintendo_title_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.TitleId).Column("title_id", ESqliteTypeName.TEXT, "标题 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Platform).Column("platform", ESqliteTypeName.TEXT, "主机（switch / switch2 / other）").HasMaxLength(16).IsRequired();
        builder.Property(e => e.Name).Column("name", ESqliteTypeName.TEXT, "名称").HasMaxLength(200).IsRequired();
        builder.Property(e => e.ImageUrl).Column("image_url", ESqliteTypeName.TEXT, "封面地址").HasMaxLength(1000);
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "更新时间（Unix 时间戳，秒）").IsRequired();
    }
}

internal sealed class NintendoTitleTotalConfiguration : IEntityTypeConfiguration<NintendoTitleTotal>
{
    public void Configure(EntityTypeBuilder<NintendoTitleTotal> builder)
    {
        builder.ToTable("nintendo_title_total", t => t.HasComment("任天堂给的每款游戏的累计值"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.NaId, e.TitleId, e.Platform }, "nintendo_title_total_uidx").IsUnique();

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.NaId).Column("na_id", ESqliteTypeName.TEXT, "任天堂账号 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.TitleId).Column("title_id", ESqliteTypeName.TEXT, "标题 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Platform).Column("platform", ESqliteTypeName.TEXT, "主机").HasMaxLength(16).IsRequired();
        builder.Property(e => e.TotalMinutes).Column("total_minutes", ESqliteTypeName.INTEGER, "累计分钟数").IsRequired();
        builder.Property(e => e.TotalDays).Column("total_days", ESqliteTypeName.INTEGER, "游玩天数").IsRequired();
        builder.Property(e => e.FirstPlayedAt).Column("first_played_at", ESqliteTypeName.INTEGER, "首次游玩时间（Unix 时间戳，秒）");
        builder.Property(e => e.LastPlayedAt).Column("last_played_at", ESqliteTypeName.INTEGER, "最近游玩时间（Unix 时间戳，秒）");
        builder.Property(e => e.UpdatedAt).Column("updated_at", ESqliteTypeName.INTEGER, "更新时间（Unix 时间戳，秒）").IsRequired();
    }
}

internal sealed class NintendoPlaytimeSnapshotConfiguration : IEntityTypeConfiguration<NintendoPlaytimeSnapshot>
{
    public void Configure(EntityTypeBuilder<NintendoPlaytimeSnapshot> builder)
    {
        builder.ToTable("nintendo_playtime_snapshot", t => t.HasComment("Nintendo 累计时长快照（按值变化分段）"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.NaId, e.TitleId, e.Platform, e.TotalMinutes }, "nintendo_playtime_snapshot_uidx").IsUnique();
        builder.HasIndex(e => e.FirstObservedAt, "nintendo_playtime_snapshot_first_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.NaId).Column("na_id", ESqliteTypeName.TEXT, "任天堂账号 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.TitleId).Column("title_id", ESqliteTypeName.TEXT, "标题 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Platform).Column("platform", ESqliteTypeName.TEXT, "主机").HasMaxLength(16).IsRequired();
        builder.Property(e => e.TotalMinutes).Column("total_minutes", ESqliteTypeName.INTEGER, "累计分钟数").IsRequired();
        builder.Property(e => e.FirstObservedAt).Column("first_observed_at", ESqliteTypeName.INTEGER, "首次读到该值的时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.LastObservedAt).Column("last_observed_at", ESqliteTypeName.INTEGER, "最近一次读到该值的时间（Unix 时间戳，秒）").IsRequired();
        builder.Property(e => e.LastPlayedAt).Column("last_played_at", ESqliteTypeName.INTEGER, "读到该值时任天堂给的最近游玩时间");
    }
}

internal sealed class NintendoDailyPlaytimeConfiguration : IEntityTypeConfiguration<NintendoDailyPlaytime>
{
    public void Configure(EntityTypeBuilder<NintendoDailyPlaytime> builder)
    {
        builder.ToTable("nintendo_daily_playtime", t => t.HasComment("任天堂给的日级时长"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.NaId, e.PlayedDay, e.TitleId, e.Platform }, "nintendo_daily_playtime_uidx").IsUnique();
        builder.HasIndex(e => e.PlayedDay, "nintendo_daily_playtime_day_idx");

        builder.Property(e => e.Id).Column("id", ESqliteTypeName.INTEGER, "ID").IsRequired().ValueGeneratedOnAdd();
        builder.Property(e => e.NaId).Column("na_id", ESqliteTypeName.TEXT, "任天堂账号 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.PlayedDay).Column("played_day", ESqliteTypeName.INTEGER, "日期（距 1970-01-01 的天数，任天堂那边的日期）").IsRequired();
        builder.Property(e => e.TitleId).Column("title_id", ESqliteTypeName.TEXT, "标题 ID").HasMaxLength(32).IsRequired();
        builder.Property(e => e.Platform).Column("platform", ESqliteTypeName.TEXT, "主机").HasMaxLength(16).IsRequired();
        builder.Property(e => e.Minutes).Column("minutes", ESqliteTypeName.INTEGER, "分钟数").IsRequired();
        builder.Property(e => e.ObservedAt).Column("observed_at", ESqliteTypeName.INTEGER, "读取时间（Unix 时间戳，秒）").IsRequired();
    }
}
