using System.Globalization;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Insights;
using SteamStat.Core.Memoir;

namespace ElectronNet.Features.Memoir;

/// <summary>Core 结果到 IPC DTO。</summary>
internal static class MemoirDtoMapper
{
    internal static MemoirValueDto ToDto(MemoirValue value) => new()
    {
        Value = value.Value,
        Evidence = value.Evidence.Id(),
        Upper = value.Upper is { } upper && upper > value.Value ? upper : null
    };

    internal static LifeEventDto ToDto(MemoirLifeEvent item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Note = item.Note,
        Kind = item.Kind,
        StartDay = item.StartDay,
        EndDay = item.EndDay,
        Start = MemoirDays.Format(item.StartDay),
        End = item.EndDay is { } end ? MemoirDays.Format(end) : null,
        Boundary = item.Boundary,
        UpdatedAt = item.UpdatedAt
    };

    internal static IReadOnlyDictionary<string, string> Names(MemoirArchive archive, IEnumerable<int> appIds)
        => appIds.Distinct().ToDictionary(appId => appId.ToString(CultureInfo.InvariantCulture), archive.NameOf);

    internal static MemoirChapterDto ToDto(LifeChapter chapter, MemoirArchive archive) => new()
    {
        Index = chapter.Index,
        FromMonth = chapter.FromMonth,
        ToMonth = chapter.ToMonth,
        Months = chapter.Months,
        Kind = chapter.Kind,
        AppIds = chapter.AppIds,
        Seconds = chapter.Seconds,
        MonthlySeconds = chapter.MonthlySeconds,
        PeakMonth = chapter.PeakMonth,
        NewGames = chapter.NewGames,
        Unlocks = chapter.Unlocks,
        NightShare = chapter.NightShare is { } night ? Math.Round(night, 3) : null,
        TopGames = chapter.TopGames.Select(game => new MemoirChapterGameDto { AppId = game.AppId, Name = archive.NameOf(game.AppId), Seconds = game.Seconds, Share = Math.Round(game.Share, 4) }).ToList(),
        Evidence = chapter.Evidence.Id(),
        StartEventId = chapter.StartEventId
    };

    internal static MemoirUnlockDto ToDto(InsightUnlock unlock, MemoirArchive archive, IReadOnlyDictionary<(int, string), string> icons) => new()
    {
        AppId = unlock.AppId,
        AppName = archive.NameOf(unlock.AppId),
        InternalName = unlock.InternalName,
        Name = string.IsNullOrWhiteSpace(unlock.Name) ? unlock.InternalName : unlock.Name,
        Icon = icons.GetValueOrDefault((unlock.AppId, unlock.InternalName)),
        Time = unlock.Time,
        GlobalPercent = unlock.GlobalPercent
    };

    internal static YearInReviewDto ToDto(YearReview review, MemoirArchive archive) => new()
    {
        SteamId = review.SteamId,
        Year = review.Year,
        FetchedAt = review.FetchedAt,
        Seconds = review.Seconds,
        Sessions = review.Sessions,
        GamesPlayed = review.GamesPlayed,
        NewGames = review.NewGames,
        Achievements = review.Achievements,
        Streak = review.Streak == null ? null : new YearInReviewStreakDto { Days = review.Streak.Days, Start = review.Streak.Start, AppIds = review.Streak.AppIds },
        Months = review.Months.Select(month => new YearInReviewMonthDto
        {
            Month = month.Month,
            Seconds = month.Seconds,
            Share = month.Share is { } share ? Math.Round(share, 4) : null,
            Sessions = month.Sessions,
            AppIds = month.Games.Take(5).Select(game => game.AppId).ToList()
        }).ToList(),
        Games = review.Games.Take(50).Select(game => new YearInReviewGameDto
        {
            AppId = game.AppId,
            Name = archive.NameOf(game.AppId),
            Seconds = game.Seconds,
            Share = game.Share is { } share ? Math.Round(share, 4) : null,
            Sessions = game.Sessions,
            NewThisYear = game.NewThisYear,
            FirstPlayed = game.FirstPlayed
        }).ToList(),
        Percentiles = review.Percentiles == null ? null : new YearInReviewPercentilesDto
        {
            MedianAchievements = review.Percentiles.MedianAchievements,
            MedianGames = review.Percentiles.MedianGames,
            MedianStreak = review.Percentiles.MedianStreak,
            GamesPlayedPercentile = review.Percentiles.GamesPlayedPercentile,
            AchievementsPercentile = review.Percentiles.AchievementsPercentile,
            StreakPercentile = review.Percentiles.StreakPercentile,
            NewReleasesShare = review.Percentiles.NewReleasesShare,
            RecentReleasesShare = review.Percentiles.RecentReleasesShare,
            ClassicReleasesShare = review.Percentiles.ClassicReleasesShare,
            RecentCutoffYear = review.Percentiles.RecentCutoffYear
        },
        Platforms = review.Platforms.ToDictionary(entry => entry.Key, entry => Math.Round(entry.Value, 4))
    };

    internal static TimeMachineDto ToDto(TimeMachineSnapshot snapshot, MemoirArchive archive, IReadOnlyDictionary<(int, string), string> icons)
    {
        var options = archive.Options;
        string Day(long time) => options.DayOf(time);
        var summary = snapshot.Summary;
        return new TimeMachineDto
        {
            Day = MemoirDays.Format(snapshot.Day),
            DayNumber = snapshot.Day,
            Today = MemoirDays.Format(snapshot.Today),
            TodayNumber = snapshot.Today,
            FirstDay = snapshot.FirstDay is { } first ? MemoirDays.Format(first) : null,
            FirstDayNumber = snapshot.FirstDay,
            TrackingStartDay = snapshot.TrackingStartDay is { } tracking ? MemoirDays.Format(tracking) : null,
            Summary = new TimeMachineSummaryDto
            {
                DaySeconds = ToDto(summary.DaySeconds),
                WeekSeconds = ToDto(summary.WeekSeconds),
                MonthSeconds = ToDto(summary.MonthSeconds),
                TotalSeconds = ToDto(summary.TotalSeconds),
                GamesPlayed = ToDto(summary.GamesPlayed),
                Sessions = summary.Sessions.Select(session => new TimeMachineSessionDto { SteamId = session.SteamId, AppId = session.AppId, Name = archive.NameOf(session.AppId), Start = session.Start, Seconds = session.Seconds }).ToList(),
                Traces = summary.Traces.Select(trace => new MemoirTraceDto { AppId = trace.AppId, Name = archive.NameOf(trace.AppId), Kind = trace.Kind.Id(), Time = trace.Time }).ToList(),
                RecentGames = summary.RecentGames.Select(game => new TimeMachineGameDto { AppId = game.AppId, Name = archive.NameOf(game.AppId), Seconds = game.Seconds, Evidence = game.Evidence.Id() }).ToList(),
                OfficialMonth = summary.OfficialMonth == null ? null : new MemoirOfficialMonthDto { Month = summary.OfficialMonth.Month, Seconds = summary.OfficialMonth.Seconds, Games = summary.OfficialMonth.AppIds.Count, AppIds = summary.OfficialMonth.AppIds },
                Profile = summary.Profile == null ? null : new TimeMachineProfileDto { PersonaName = summary.Profile.PersonaName, Level = summary.Profile.Level, Evidence = summary.Profile.Evidence.Id(), ObservedAt = summary.Profile.ObservedAt },
                Chapter = summary.Chapter == null ? null : new MemoirChapterRefDto { Index = summary.Chapter.Index, Kind = summary.Chapter.Kind, AppIds = summary.Chapter.AppIds, FromMonth = summary.Chapter.FromMonth, ToMonth = summary.Chapter.ToMonth },
                LifeEvents = summary.LifeEvents.Select(ToDto).ToList()
            },
            Library = new TimeMachineLibraryDto
            {
                Owned = ToDto(snapshot.Library.Owned),
                Unknown = snapshot.Library.Unknown,
                Later = snapshot.Library.Later,
                Games = snapshot.Library.Games.Select(game => new TimeMachineLibraryGameDto
                {
                    AppId = game.AppId,
                    Name = archive.NameOf(game.AppId),
                    AcquiredAt = game.AcquiredAt,
                    AcquiredEvidence = game.AcquiredEvidence.Id(),
                    Playtime = ToDto(game.Playtime),
                    FirstPlayed = game.FirstPlayed,
                    LastPlayed = game.LastPlayed
                }).ToList()
            },
            Achievements = new TimeMachineAchievementsDto
            {
                Unlocked = ToDto(snapshot.Achievements.Unlocked),
                Completed = ToDto(snapshot.Achievements.Completed),
                Games = snapshot.Achievements.Games,
                DayUnlocks = snapshot.Achievements.DayUnlocks.Select(unlock => ToDto(unlock, archive, icons)).ToList(),
                Recent = snapshot.Achievements.Recent.Select(unlock => ToDto(unlock, archive, icons)).ToList(),
                Rarest = snapshot.Achievements.Rarest.Select(unlock => ToDto(unlock, archive, icons)).ToList(),
                Progress = snapshot.Achievements.Progress.Select(item => new TimeMachineProgressDto { AppId = item.AppId, Name = item.Name, Unlocked = item.Unlocked, Total = item.Total, Evidence = item.Evidence.Id() }).ToList()
            },
            Friends = new TimeMachineFriendsDto
            {
                Count = ToDto(snapshot.Friends.Count),
                KnownSince = snapshot.Friends.KnownSince is { } known ? Day(known) : null,
                Friends = snapshot.Friends.Friends.Select(friend => new TimeMachineFriendDto { SteamId = friend.SteamId, Name = friend.Name, Evidence = friend.Evidence.Id(), AddedAt = friend.AddedAt, RemovedAt = friend.RemovedAt }).ToList(),
                Activity = snapshot.Friends.Activity.Select(play => new TimeMachineFriendPlayDto { SteamId = play.SteamId, Name = play.Name, AppId = play.AppId, GameName = archive.NameOf(play.AppId), Seconds = play.Seconds }).ToList(),
                Added = snapshot.Friends.Added,
                Removed = snapshot.Friends.Removed
            }
        };
    }
}
