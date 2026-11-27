using System.Globalization;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using SteamStat.Core.Nintendo;

namespace ElectronNet.Features.Nintendo;

internal readonly record struct NintendoWriteResult(int Titles, int Days, int Changed);

/// <summary>
/// 把一次读到的游玩记录写进档案。只写变化：没变的游戏不碰（多设备同步的出站队列不会每次都塞满）。
/// 累计值变化时，把上一段平台期的结束时间推到上一次成功读取的时间——那时读到的还是旧值，
/// 这样两段之间的窗口就是真实的「这段时间里玩了 Δ 分钟」，不用每次都去更新每款游戏。
/// </summary>
internal static class NintendoArchiveWriter
{
    internal static readonly DateOnly Epoch = new(1970, 1, 1);

    public static async Task<NintendoWriteResult> ApplyAsync(
        AppDbContext db,
        string naId,
        NintendoPlayHistory history,
        long now,
        long? previousSuccessAt,
        CancellationToken cancellationToken = default)
    {
        var changed = 0;
        var keys = history.Titles.Select(title => title.TitleId).Concat(history.Recent.SelectMany(day => day.Titles).Select(title => title.TitleId)).Distinct().ToList();

        var titles = (await db.NintendoTitleTable.Where(row => keys.Contains(row.TitleId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => (row.TitleId, row.Platform));
        void Title(string titleId, string platform, string name, string? image)
        {
            if (!titles.TryGetValue((titleId, platform), out var row))
            {
                row = new NintendoTitle { TitleId = titleId, Platform = platform, Name = name, ImageUrl = image, UpdatedAt = now };
                titles[(titleId, platform)] = row;
                db.NintendoTitleTable.Add(row);
                changed++;
                return;
            }
            // 名字是 id 本身说明当时没给名字，不拿它覆盖已有的
            var better = name != titleId || row.Name == row.TitleId;
            if ((better && row.Name != name) || (image != null && row.ImageUrl != image))
            {
                if (better) row.Name = name;
                if (image != null) row.ImageUrl = image;
                row.UpdatedAt = now;
                changed++;
            }
        }

        var totals = (await db.NintendoTitleTotalTable.Where(row => row.NaId == naId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => (row.TitleId, row.Platform));
        var segments = (await db.NintendoPlaytimeSnapshotTable.Where(row => row.NaId == naId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(row => (row.TitleId, row.Platform))
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var title in history.Titles)
        {
            var key = (title.TitleId, title.Platform);
            Title(title.TitleId, title.Platform, title.Name, title.ImageUrl);

            if (!totals.TryGetValue(key, out var total))
            {
                total = new NintendoTitleTotal { NaId = naId, TitleId = title.TitleId, Platform = title.Platform };
                db.NintendoTitleTotalTable.Add(total);
                totals[key] = total;
                Copy(total, title, now);
                changed++;
            }
            else if (total.TotalMinutes != title.TotalMinutes || total.TotalDays != title.TotalDays
                     || total.FirstPlayedAt != title.FirstPlayedAt || total.LastPlayedAt != title.LastPlayedAt)
            {
                Copy(total, title, now);
                changed++;
            }

            if (!segments.TryGetValue(key, out var list)) segments[key] = list = [];
            var latest = list.OrderByDescending(row => row.LastObservedAt).ThenByDescending(row => row.Id).FirstOrDefault();
            if (latest?.TotalMinutes == title.TotalMinutes) continue;

            if (latest != null && previousSuccessAt is { } seenOld && seenOld > latest.LastObservedAt && seenOld < now)
                latest.LastObservedAt = seenOld;
            var same = list.FirstOrDefault(row => row.TotalMinutes == title.TotalMinutes);
            if (same != null)
            {
                // 数值回到了以前的某一段（任天堂改正了数据）：唯一键不允许再插一条，挪到现在
                same.FirstObservedAt = now;
                same.LastObservedAt = now;
                same.LastPlayedAt = title.LastPlayedAt;
            }
            else
            {
                var segment = new NintendoPlaytimeSnapshot
                {
                    NaId = naId,
                    TitleId = title.TitleId,
                    Platform = title.Platform,
                    TotalMinutes = title.TotalMinutes,
                    FirstObservedAt = now,
                    LastObservedAt = now,
                    LastPlayedAt = title.LastPlayedAt
                };
                db.NintendoPlaytimeSnapshotTable.Add(segment);
                list.Add(segment);
            }
            changed++;
        }

        var dayNumbers = history.Recent.Select(day => Day(day.PlayedDate)).Where(day => day != null).Select(day => day!.Value).Distinct().ToList();
        var daily = (await db.NintendoDailyPlaytimeTable.Where(row => row.NaId == naId && dayNumbers.Contains(row.PlayedDay)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => (row.PlayedDay, row.TitleId, row.Platform));
        foreach (var day in history.Recent)
        {
            if (Day(day.PlayedDate) is not { } number) continue;
            foreach (var title in day.Titles)
            {
                Title(title.TitleId, title.Platform, title.Name, title.ImageUrl);
                if (daily.TryGetValue((number, title.TitleId, title.Platform), out var row))
                {
                    if (row.Minutes == title.Minutes) continue;
                    row.Minutes = title.Minutes;
                    row.ObservedAt = now;
                }
                else
                {
                    row = new NintendoDailyPlaytime { NaId = naId, PlayedDay = number, TitleId = title.TitleId, Platform = title.Platform, Minutes = title.Minutes, ObservedAt = now };
                    db.NintendoDailyPlaytimeTable.Add(row);
                    daily[(number, title.TitleId, title.Platform)] = row;
                }
                changed++;
            }
        }

        if (changed > 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new NintendoWriteResult(history.Titles.Count, dayNumbers.Count, changed);
    }

    internal static int? Day(string date)
        => DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.DayNumber - Epoch.DayNumber
            : null;

    private static void Copy(NintendoTitleTotal total, NintendoPlayedTitle title, long now)
    {
        total.TotalMinutes = title.TotalMinutes;
        total.TotalDays = title.TotalDays;
        total.FirstPlayedAt = title.FirstPlayedAt;
        total.LastPlayedAt = title.LastPlayedAt;
        total.UpdatedAt = now;
    }
}
