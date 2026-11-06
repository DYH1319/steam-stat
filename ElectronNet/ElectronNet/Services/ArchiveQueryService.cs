using System.Globalization;
using System.Text.Json;
using ElectronNet.Helpers;
using ElectronNet.Models;
using ElectronNet.Services.Collectors;
using Microsoft.EntityFrameworkCore;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Archives.Mining;
using SteamStat.Core.Archives.Recordings;
using SteamStat.Core.Archives.Store;
using SteamStat.Core.Archives.Web;
using SteamStat.Core.Features;

namespace ElectronNet.Services;

/// <summary>
/// 新档案的读取入口。隐藏游戏和主播模式按渲染层的约定在显示时生效，这里返回原始内容；
/// 对外查询走语义视图，那边已经剔除了隐藏游戏。
/// </summary>
internal sealed class ArchiveQueryService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    IAppNameResolver appNames,
    PlayerHistoryService playerHistory,
    PublicDataService publicData,
    StoreArchiveService storeArchive,
    TimeProvider timeProvider)
{
    private const string AssetBase = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps";
    private const string ScreenshotBase = "https://shared.akamai.steamstatic.com/store_item_assets";
    private const int MaxRichPresenceRecords = 200_000;
    private const int MaxEvents = 500;
    private const long MaxPaletteImageBytes = 12 * 1024 * 1024;

    private static bool InScope(IReadOnlyList<string>? scope, string steamId) => scope is not { Count: > 0 } || scope.Contains(steamId);

    /// <summary>名字优先用缓存；愿望单上没买过的游戏只有商店快照里才有名字。</summary>
    private async Task<Dictionary<int, string>> NamesAsync(AppDbContext db, IEnumerable<int> appIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<int, string>();
        var missing = new List<int>();
        foreach (var appId in appIds.Distinct())
        {
            var name = appNames.GetCachedName((uint)appId);
            if (AppNameHelper.IsPlaceholder(name)) missing.Add(appId);
            else names[appId] = name!;
        }
        if (missing.Count == 0) return names;
        var rows = await db.StorePageSnapshotTable.AsNoTracking().Where(row => missing.Contains(row.AppId))
            .OrderBy(row => row.CapturedAt).Select(row => new { row.AppId, row.Facts }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var name = StorePageFacts.FromJson(row.Facts)?.Name;
            if (!string.IsNullOrEmpty(name)) names[row.AppId] = name;
        }
        foreach (var appId in missing) names.TryAdd(appId, string.Create(CultureInfo.InvariantCulture, $"App {appId}"));
        return names;
    }

    #region 单游戏与平台

    public async Task<GameArchiveDto> GetGameAsync(GameArchiveRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var platforms = (await db.PlatformPlaytimeTable.AsNoTracking().Where(row => row.AppId == request.AppId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => InScope(request.SteamIds, row.SteamId))
            .GroupBy(row => row.SteamId)
            .Select(group => ToDto(group.OrderByDescending(row => row.ObservedAt).ThenByDescending(row => row.Id).First()))
            .ToArray();
        var installs = await db.AppInstallEventTable.AsNoTracking().Where(row => row.AppId == request.AppId).OrderBy(row => row.ObservedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var builds = await db.AppBuildTable.AsNoTracking().Where(row => row.AppId == request.AppId).OrderBy(row => row.ObservedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var artifacts = await db.AppArtifactEventTable.AsNoTracking().Where(row => row.AppId == request.AppId).OrderByDescending(row => row.ObservedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = await db.StorePageSnapshotTable.AsNoTracking().Where(row => row.AppId == request.AppId).OrderByDescending(row => row.CapturedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(db, [request.AppId], cancellationToken).ConfigureAwait(false);
        return new GameArchiveDto
        {
            Platforms = platforms,
            Installs = installs.Select(row => new InstallEventDto { Kind = row.Kind, ObservedAt = row.ObservedAt, SizeOnDisk = row.SizeOnDisk, BuildId = row.BuildId }).ToArray(),
            Builds = builds.Select(row => new BuildEventDto { BuildId = row.BuildId, ObservedAt = row.ObservedAt, IsBaseline = row.IsBaseline }).ToArray(),
            Artifacts = artifacts.Select(row => ToDto(row, names)).ToArray(),
            Snapshots = snapshots.Select(ToDto).OfType<StoreSnapshotDto>().ToArray()
        };
    }

    public async Task<PlayerCurveDto> GetPlayerCurveAsync(PlayerCurveRequest request, CancellationToken cancellationToken = default)
    {
        var points = await playerHistory.GetCurveAsync(request.AppId, request.Months ?? 24, cancellationToken).ConfigureAwait(false);
        return new PlayerCurveDto
        {
            Points = points.Select(point => new PlayerPointDto { Day = point.Day, Players = point.Players, Source = point.Source }).ToArray(),
            PublicEnabled = publicData.Enabled
        };
    }

    public async Task<RichPresenceMiningDto> GetRichPresenceAsync(RichPresenceMiningRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.RichPresenceRecordTable.AsNoTracking().Where(row => row.AppId == request.AppId);
        query = request.SteamId == null ? query.Where(row => row.IsSelf) : query.Where(row => row.SteamId == request.SteamId);
        var rows = await query.OrderByDescending(row => row.ObservedAt).Take(MaxRichPresenceRecords)
            .Select(row => new RichPresenceSample(row.ObservedAt, row.RawValues, row.DisplayText))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var report = RichPresenceMiner.Mine(rows, timeProvider.GetUtcNow().ToUnixTimeSeconds());
        return new RichPresenceMiningDto
        {
            RecordedSeconds = report.RecordedSeconds,
            Records = report.Records,
            FirstAt = report.FirstAt,
            LastAt = report.LastAt,
            Dimensions = report.Dimensions.Select(dimension => new RichPresenceDimensionDto
            {
                Kind = dimension.Kind,
                Key = dimension.Key,
                Values = dimension.Values.Select(value => new RichPresenceValueDto { Value = value.Value, Seconds = value.Seconds, Times = value.Times, FirstAt = value.FirstAt, LastAt = value.LastAt }).ToArray()
            }).ToArray(),
            RankHistory = report.RankHistory.Select(change => new RichPresenceChangeDto { At = change.At, Value = change.Value }).ToArray(),
            UnknownKeys = report.UnknownKeys
        };
    }

    public async Task<PlatformOverviewDto> GetPlatformsAsync(ArchiveScopeRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var latest = (await db.PlatformPlaytimeTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => InScope(request.SteamIds, row.SteamId))
            .GroupBy(row => (row.SteamId, row.AppId))
            .Select(group => group.OrderByDescending(row => row.ObservedAt).ThenByDescending(row => row.Id).First())
            .Where(row => row.WindowsMinutes + row.MacMinutes + row.LinuxMinutes + row.DeckMinutes + row.DisconnectedMinutes > 0)
            .ToArray();
        var names = await NamesAsync(db, latest.Select(row => row.AppId), cancellationToken).ConfigureAwait(false);
        return new PlatformOverviewDto
        {
            Games = latest.Select(ToDto).ToArray(),
            Names = names.ToDictionary(entry => entry.Key.ToString(CultureInfo.InvariantCulture), entry => entry.Value)
        };
    }

    private static PlatformPlaytimeDto ToDto(PlatformPlaytimeRecord row) => new()
    {
        SteamId = row.SteamId,
        AppId = row.AppId,
        WindowsMinutes = row.WindowsMinutes,
        MacMinutes = row.MacMinutes,
        LinuxMinutes = row.LinuxMinutes,
        DeckMinutes = row.DeckMinutes,
        DisconnectedMinutes = row.DisconnectedMinutes,
        FirstPlayed = row.FirstPlayed,
        LastPlayed = row.LastPlayed,
        FirstWindows = row.FirstWindows,
        FirstMac = row.FirstMac,
        FirstLinux = row.FirstLinux,
        FirstDeck = row.FirstDeck,
        LastWindows = row.LastWindows,
        LastMac = row.LastMac,
        LastLinux = row.LastLinux,
        LastDeck = row.LastDeck,
        ObservedAt = row.ObservedAt
    };

    private StoreSnapshotDto? ToDto(StorePageSnapshot row)
    {
        var facts = StorePageFacts.FromJson(row.Facts);
        if (facts == null) return null;
        var cover = string.IsNullOrEmpty(row.CoverFile) ? null : Path.Combine(storeArchive.CoverDirectory, row.CoverFile);
        return new StoreSnapshotDto
        {
            Id = row.Id,
            AppId = row.AppId,
            CapturedAt = row.CapturedAt,
            Name = facts.Name,
            Description = facts.ShortDescription,
            Developers = facts.Developers,
            Publishers = facts.Publishers,
            Franchise = facts.Franchise,
            ReleaseDate = facts.ReleaseDate,
            CoverPath = cover != null && File.Exists(cover) ? cover : null,
            HeaderUrl = facts.HeaderAsset == null ? null : string.Create(CultureInfo.InvariantCulture, $"{AssetBase}/{row.AppId}/{facts.HeaderAsset}"),
            Screenshots = facts.Screenshots.Take(8).Select(file => $"{ScreenshotBase}/{file}").ToArray()
        };
    }

    private static ArtifactEventDto ToDto(AppArtifactEvent row, IReadOnlyDictionary<int, string> names) => new()
    {
        Id = row.Id,
        AppId = row.AppId,
        Name = names.GetValueOrDefault(row.AppId) ?? string.Create(CultureInfo.InvariantCulture, $"App {row.AppId}"),
        Kind = row.Kind,
        ObservedAt = row.ObservedAt,
        Before = row.Before,
        After = row.After
    };

    #endregion

    #region 消费与愿望单

    public async Task<PurchasePageDto> GetPurchasesAsync(PurchasePageRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.PurchaseRecordTable.AsNoTracking();
        if (request.SteamIds is { Count: > 0 } scope) query = query.Where(row => scope.Contains(row.SteamId));
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query.OrderByDescending(row => row.Date).ThenByDescending(row => row.Id).Skip(request.Offset).Take(request.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var next = request.Offset + rows.Count;
        return new PurchasePageDto
        {
            Items = rows.Select(row => new PurchaseDto
            {
                Id = row.Id,
                SteamId = row.SteamId,
                Date = row.Date,
                Kind = row.Kind,
                TypeText = row.TypeText,
                TotalCents = row.TotalCents,
                Currency = row.Currency,
                WalletChangeCents = row.WalletChangeCents,
                PaymentMethod = row.PaymentMethod,
                Refunded = row.Refunded,
                Items = BillingArchive.Parse(row.Items).Select(item => new PurchaseItemDto { Name = item.Name, AppId = item.AppId, Source = item.Source, AllocatedCents = item.AllocatedCents, Shared = item.Shared }).ToArray()
            }).ToArray(),
            Total = total,
            NextOffset = next < total ? next : null
        };
    }

    public async Task<WishlistArchiveDto> GetWishlistAsync(ArchiveScopeRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await db.WishlistRecordTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => InScope(request.SteamIds, row.SteamId))
            .ToArray();
        var appIds = rows.Select(row => row.AppId).Distinct().ToList();
        var names = await NamesAsync(db, appIds, cancellationToken).ConfigureAwait(false);
        var store = await db.AppStoreStatusTable.AsNoTracking().Where(status => appIds.Contains(status.AppId)).ToDictionaryAsync(status => status.AppId, cancellationToken).ConfigureAwait(false);
        var accounts = await db.SteamUserTable.AsNoTracking().ToDictionaryAsync(user => user.SteamId, user => user.AccountName, cancellationToken).ConfigureAwait(false);
        var longIds = appIds.Select(id => (long)id).ToList();
        // 入库时间：许可证优先，只算加入愿望单之后的
        var acquisitions = (await db.LibraryAcquisitionTable.AsNoTracking().Where(row => longIds.Contains(row.AppId)).Select(row => new { row.AccountName, row.AppId, row.AcquiredAt }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(row => (row.AccountName, row.AppId));
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var items = rows.Select(row =>
        {
            var account = accounts.GetValueOrDefault(row.SteamId) ?? string.Empty;
            var acquired = acquisitions[(account, (long)row.AppId)].Select(entry => (long?)entry.AcquiredAt).Where(at => at >= row.AddedAt).Min();
            var end = acquired ?? row.RemovedAt ?? now;
            var status = store.GetValueOrDefault(row.AppId);
            return new WishlistItemDto
            {
                SteamId = row.SteamId,
                AppId = row.AppId,
                Name = names.GetValueOrDefault(row.AppId) ?? string.Empty,
                AddedAt = row.AddedAt,
                RemovedAt = row.RemovedAt,
                Priority = row.Priority,
                Outcome = acquired != null ? "acquired" : row.RemovedAt != null ? "removed" : "waiting",
                AcquiredAt = acquired,
                WaitDays = (int)Math.Max(0, (end - row.AddedAt) / 86400),
                PriceCents = status?.PriceCents,
                ReviewPercent = status?.ReviewPercent,
                Delisted = status?.DelistedAt != null
            };
        }).OrderByDescending(item => item.AddedAt).ToArray();
        return new WishlistArchiveDto { Items = items };
    }

    #endregion

    #region 数字文物

    public async Task<ArtifactOverviewDto> GetArtifactsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var events = await db.AppArtifactEventTable.AsNoTracking().OrderByDescending(row => row.ObservedAt).Take(MaxEvents).ToListAsync(cancellationToken).ConfigureAwait(false);
        var delisted = await db.AppStoreStatusTable.AsNoTracking().Where(status => status.DelistedAt != null).Select(status => new { status.AppId, status.DelistedAt }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var snapshotted = (await db.StorePageSnapshotTable.AsNoTracking().Select(row => row.AppId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var snapshots = await db.StorePageSnapshotTable.CountAsync(cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(db, events.Select(row => row.AppId).Concat(delisted.Select(row => row.AppId)), cancellationToken).ConfigureAwait(false);
        var covers = Directory.Exists(storeArchive.CoverDirectory) ? new DirectoryInfo(storeArchive.CoverDirectory).GetFiles("*.jpg") : [];
        return new ArtifactOverviewDto
        {
            Events = events.Select(row => ToDto(row, names)).ToArray(),
            Delisted = delisted.Select(row => new DelistedGameDto
            {
                AppId = row.AppId,
                Name = names.GetValueOrDefault(row.AppId) ?? string.Empty,
                DelistedAt = row.DelistedAt!.Value,
                HasSnapshot = snapshotted.Contains(row.AppId)
            }).OrderByDescending(row => row.DelistedAt).ToArray(),
            Snapshots = snapshots,
            Games = snapshotted.Count,
            Covers = covers.Length,
            CoverBytes = covers.Sum(file => file.Length)
        };
    }

    #endregion

    #region 社区、家庭、好友

    public async Task<CommunityArchiveDto> GetCommunityAsync(ArchiveScopeRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var reviews = (await db.CommunityReviewTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).Where(row => InScope(request.SteamIds, row.SteamId)).ToArray();
        var files = (await db.CommunityFileTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).Where(row => InScope(request.SteamIds, row.SteamId)).ToArray();
        var comments = request.SteamIds is { Count: > 0 } scope
            ? await db.CommunityCommentTable.CountAsync(row => scope.Contains(row.SteamId), cancellationToken).ConfigureAwait(false)
            : await db.CommunityCommentTable.CountAsync(cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(db, reviews.Select(row => row.AppId), cancellationToken).ConfigureAwait(false);
        return new CommunityArchiveDto
        {
            Reviews = reviews.OrderByDescending(row => row.PostedAt ?? row.FirstSeenAt).Select(row => new CommunityReviewDto
            {
                SteamId = row.SteamId,
                ReviewId = row.ReviewId,
                AppId = row.AppId,
                Name = names.GetValueOrDefault(row.AppId) ?? string.Empty,
                Recommended = row.Recommended,
                HoursTotal = row.HoursTotal,
                HoursAtReview = row.HoursAtReview,
                PostedAt = row.PostedAt,
                EditedAt = row.EditedAt,
                Text = row.Text,
                Helpful = row.Helpful,
                Funny = row.Funny,
                MissingSince = row.MissingSince
            }).ToArray(),
            Files = files.OrderByDescending(row => row.FirstSeenAt).Select(row => new CommunityFileDto
            {
                SteamId = row.SteamId,
                Kind = row.Kind,
                PublishedFileId = row.PublishedFileId,
                AppId = row.AppId,
                Title = row.Title,
                Description = row.Description,
                Author = row.Author,
                PreviewUrl = row.PreviewUrl,
                MissingSince = row.MissingSince
            }).ToArray(),
            Comments = comments
        };
    }

    public async Task<CommunityCommentPageDto> GetCommentsAsync(CommunityCommentsRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.CommunityCommentTable.AsNoTracking();
        if (request.SteamIds is { Count: > 0 } scope) query = query.Where(row => scope.Contains(row.SteamId));
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query.OrderByDescending(row => row.PostedAt).Skip(request.Offset).Take(request.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var next = request.Offset + rows.Count;
        return new CommunityCommentPageDto
        {
            Items = rows.Select(row => new CommunityCommentDto
            {
                SteamId = row.SteamId,
                CommentId = row.CommentId,
                AuthorSteamId = row.AuthorSteamId,
                AuthorName = row.AuthorName,
                PostedAt = row.PostedAt,
                Text = row.Text,
                MissingSince = row.MissingSince
            }).ToArray(),
            Total = total,
            NextOffset = next < total ? next : null
        };
    }

    public async Task<FamilyArchiveDto> GetFamilyAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var members = await db.FamilyMemberTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var playtime = await db.FamilyPlaytimeTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await NamesAsync(db, playtime.Select(row => row.AppId), cancellationToken).ConfigureAwait(false);
        return new FamilyArchiveDto
        {
            Members = members.Select(row => new FamilyMemberDto { SteamId = row.SteamId, Name = row.Name, Role = row.Role, JoinedAt = row.JoinedAt, Former = row.Former }).ToArray(),
            Playtime = playtime.OrderByDescending(row => row.Seconds).Select(row => new FamilyPlaytimeDto
            {
                SteamId = row.SteamId,
                AppId = row.AppId,
                Name = names.GetValueOrDefault(row.AppId) ?? string.Empty,
                ByOwner = row.ByOwner,
                FirstPlayed = row.FirstPlayed,
                LatestPlayed = row.LatestPlayed,
                Seconds = row.Seconds
            }).ToArray()
        };
    }

    /// <summary>差值：这段时间的最新值减去这段时间开始前的最后一个值。第一次观测的游戏没有差值。</summary>
    public async Task<FriendPlaytimeDto> GetFriendPlaytimeAsync(FriendPlaytimeRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.FriendPlaytimeSnapshotTable.AsNoTracking().Where(row => row.SteamId == request.SteamId).OrderBy(row => row.ObservedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return new FriendPlaytimeDto { Games = [] };
        var cutoff = timeProvider.GetUtcNow().ToUnixTimeSeconds() - (request.Days ?? 30) * 86400L;
        var names = await NamesAsync(db, rows.Select(row => row.AppId), cancellationToken).ConfigureAwait(false);
        var games = rows.GroupBy(row => row.AppId).Select(group =>
        {
            var latest = group.Last();
            var baseline = group.LastOrDefault(row => row.ObservedAt < cutoff) ?? group.First();
            return new FriendPlaytimeGameDto
            {
                AppId = group.Key,
                Name = names.GetValueOrDefault(group.Key) ?? string.Empty,
                Minutes = latest.PlaytimeMinutes,
                DeltaMinutes = Math.Max(0, latest.PlaytimeMinutes - baseline.PlaytimeMinutes),
                LastChangedAt = latest.ObservedAt
            };
        }).OrderByDescending(game => game.DeltaMinutes).ThenByDescending(game => game.Minutes).ToArray();
        return new FriendPlaytimeDto { Games = games, BaselineAt = rows[0].ObservedAt };
    }

    #endregion

    #region 录像

    public async Task<RecordingArchiveDto> GetRecordingsAsync(ArchiveScopeRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var timelines = (await db.RecordingTimelineTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).Where(row => InScope(request.SteamIds, row.SteamId)).ToArray();
        var media = (await db.RecordingMediaTable.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).Where(row => InScope(request.SteamIds, row.SteamId)).ToArray();
        var names = await NamesAsync(db, timelines.Select(row => row.AppId).Concat(media.Select(row => row.AppId)), cancellationToken).ConfigureAwait(false);
        return new RecordingArchiveDto
        {
            Timelines = timelines.OrderByDescending(row => row.StartedAt).Select(row => new RecordingTimelineDto
            {
                Id = row.Id,
                SteamId = row.SteamId,
                TimelineId = row.TimelineId,
                AppId = row.AppId,
                Name = names.GetValueOrDefault(row.AppId) ?? string.Empty,
                StartedAt = row.StartedAt,
                DurationMs = row.DurationMs,
                Markers = ToDtos(RecordingCollector.ParseMarkers(row.Markers))
            }).ToArray(),
            Media = media.OrderByDescending(row => row.StartedAt).Select(row => new RecordingMediaDto
            {
                Id = row.Id,
                SteamId = row.SteamId,
                MediaId = row.MediaId,
                Kind = row.Kind,
                AppId = row.AppId,
                Name = names.GetValueOrDefault(row.AppId) ?? string.Empty,
                Title = row.Name,
                StartedAt = row.StartedAt,
                DurationMs = row.DurationMs,
                SizeBytes = row.SizeBytes,
                ThumbnailPath = row.MissingSince == null ? row.ThumbnailPath : null,
                TimelineIds = ParseStrings(row.TimelineIds),
                Markers = ToDtos(RecordingCollector.ParseMarkers(row.Markers)),
                MissingSince = row.MissingSince
            }).ToArray()
        };
    }

    private static IReadOnlyList<RecordingMarkerDto> ToDtos(IReadOnlyList<RecordingMarker> markers)
        => markers.Select(marker => new RecordingMarkerDto { Kind = marker.Kind, Title = marker.Title, Group = marker.Group, OffsetMs = marker.OffsetMs, DurationMs = marker.DurationMs }).ToArray();

    private static IReadOnlyList<string> ParseStrings(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>路径由 Host 按 Id 解析，渲染层传不进任意路径。</summary>
    public async Task<string?> GetRecordingFolderAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var folder = await db.RecordingMediaTable.AsNoTracking().Where(row => row.Id == id).Select(row => row.FolderPath).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return folder != null && Directory.Exists(folder) ? folder : null;
    }

    #endregion

    #region 截图色谱

    public async Task<IReadOnlyList<int>> GetPalettePendingAsync(ScreenshotPaletteRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.ScreenshotArchiveTable.AsNoTracking()
            .Where(row => row.Palette == null && row.MissingSince == null)
            .OrderByDescending(row => row.TakenAt)
            .Take(request.Limit)
            .Select(row => row.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取色要读像素，而 steam-stat-file 协议的图片画到 canvas 上是不可读的，所以这里把图发成 data URL。
    /// 优先用缩略图，只有十几 KB，取色结果和原图差不多。路径由 Host 按 Id 解析，渲染层传不进任意路径。
    /// </summary>
    public async Task<string?> GetPaletteImageAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ScreenshotArchiveTable.AsNoTracking().Where(item => item.Id == id).Select(item => new { item.FilePath, item.ThumbnailPath }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row == null) return null;
        var path = !string.IsNullOrEmpty(row.ThumbnailPath) && File.Exists(row.ThumbnailPath) ? row.ThumbnailPath : row.FilePath;
        if (!File.Exists(path) || new FileInfo(path).Length > MaxPaletteImageBytes) return null;
        var type = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        return $"data:{type};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false))}";
    }

    public async Task<int> SavePalettesAsync(ScreenshotPaletteSaveRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = request.Items.Select(item => item.Id).ToList();
        var rows = await db.ScreenshotArchiveTable.Where(row => ids.Contains(row.Id)).ToDictionaryAsync(row => row.Id, cancellationToken).ConfigureAwait(false);
        var saved = 0;
        foreach (var item in request.Items)
        {
            if (!rows.TryGetValue(item.Id, out var row)) continue;
            row.Palette = item.Palette;
            saved++;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<ScreenshotSpectrumDto> GetSpectrumAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ScreenshotArchiveTable.AsNoTracking().Where(row => row.Palette != null && row.Palette != string.Empty)
            .OrderBy(row => row.TakenAt)
            .Select(row => new { row.TakenAt, row.AppId, row.Palette })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var pending = await db.ScreenshotArchiveTable.CountAsync(row => row.Palette == null && row.MissingSince == null, cancellationToken).ConfigureAwait(false);
        return new ScreenshotSpectrumDto
        {
            Times = rows.Select(row => (int)row.TakenAt).ToArray(),
            AppIds = rows.Select(row => (int)row.AppId).ToArray(),
            Colors = rows.Select(row => row.Palette!).ToArray(),
            Pending = pending
        };
    }

    #endregion

    #region 先行者与采集状态

    public async Task<PioneerReportDto> GetPioneerAsync(ArchiveScopeRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await db.AchievementUnlockArchiveTable.AsNoTracking()
                .Where(row => row.FirstPercentAt != null)
                .Select(row => new { row.SteamId, row.AppId, row.InternalName, row.Name, row.Icon, row.UnlockTime, row.FirstGlobalPercent, row.FirstPercentAt, row.GlobalPercent })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => InScope(request.SteamIds, row.SteamId))
            .ToArray();
        var report = PioneerIndex.Compute(
            rows.Select(row => new PioneerUnlock((int)row.AppId, row.InternalName, row.UnlockTime, row.FirstGlobalPercent, row.FirstPercentAt, row.GlobalPercent)),
            timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var details = rows.GroupBy(row => ((int)row.AppId, row.InternalName)).ToDictionary(group => group.Key, group => group.First());
        var names = await NamesAsync(db, report.Top.Select(entry => entry.AppId), cancellationToken).ConfigureAwait(false);
        return new PioneerReportDto
        {
            Score = report.Score,
            Fresh = report.Fresh,
            Matured = report.Matured,
            Late = report.Late,
            Top = report.Top.Select(entry =>
            {
                var detail = details.GetValueOrDefault((entry.AppId, entry.InternalName));
                return new PioneerEntryDto
                {
                    AppId = entry.AppId,
                    AppName = names.GetValueOrDefault(entry.AppId) ?? string.Empty,
                    InternalName = entry.InternalName,
                    Name = detail?.Name ?? entry.InternalName,
                    Icon = detail?.Icon ?? string.Empty,
                    UnlockTime = entry.UnlockTime,
                    FirstPercent = entry.FirstPercent,
                    CurrentPercent = entry.CurrentPercent,
                    Lead = entry.Lead
                };
            }).ToArray()
        };
    }

    public async Task<IReadOnlyList<WebCaptureSummaryDto>> GetWebCapturesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.WebCaptureTable.AsNoTracking().Select(row => new { row.Kind, row.Size, row.CapturedAt, row.State }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return WebCaptureKinds.All.Select(kind =>
        {
            var matching = rows.Where(row => row.Kind == kind).ToArray();
            return new WebCaptureSummaryDto
            {
                Kind = kind,
                Captures = matching.Length,
                Bytes = matching.Sum(row => row.Size),
                LastCapturedAt = matching.Length == 0 ? null : matching.Max(row => row.CapturedAt),
                LayoutFailures = matching.Count(row => row.State != "ok")
            };
        }).ToArray();
    }

    /// <summary>手动重新解析失败的页面：修好解析器之前按了也没用，但不会有副作用。</summary>
    public async Task<int> MarkLayoutFailuresForReparseAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.WebCaptureTable.Where(row => row.State != "ok").ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows) row.ParserVersion = 0;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    #endregion
}
