using System.Text.Json;
using ElectronNet.Infrastructure;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Services;

/// <summary>
/// 游戏规划：坑、心得、周目、自定义标签、游玩计划和年度挑战。数据属于「我」，按应用存，不分账号；
/// 记录的 Id 是同步 ID（插入时在这里生成，标签关联靠它跨设备对上）。全部表参与多设备同步。
/// </summary>
internal sealed class PlannerService(IDbContextFactory<AppDbContext> dbContextFactory, TimeProvider timeProvider)
{
    private const int MaxMilestones = 50;
    private const int MaxTagsPerGame = 20;
    private const int MaxBatch = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private long Now => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private static string NewUid() => Guid.NewGuid().ToString("N");

    public async Task<PlannerSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var journeys = await db.GameJourneyTable.AsNoTracking().OrderBy(row => row.AppId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var playthroughs = await db.GamePlaythroughTable.AsNoTracking().OrderBy(row => row.AppId).ThenBy(row => row.StartedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var tags = await db.GameTagTable.AsNoTracking().OrderBy(row => row.SortOrder).ThenBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var tagIds = tags.Select(tag => tag.SyncUid).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var links = await db.GameTagLinkTable.AsNoTracking().OrderBy(row => row.AppId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var plans = await db.PlayPlanTable.AsNoTracking().OrderBy(row => row.StartDay).ThenBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var challenges = await db.PlayChallengeTable.AsNoTracking().OrderBy(row => row.Year).ThenBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var stats = await db.GameJournalTable.AsNoTracking()
            .GroupBy(row => row.AppId)
            .Select(group => new { AppId = group.Key, Count = group.Count(), LastAt = group.Max(row => row.CreatedAt) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PlannerSnapshotDto
        {
            Journeys = journeys.Select(ToDto).ToList(),
            Playthroughs = playthroughs.Where(row => row.SyncUid != null).Select(ToDto).ToList(),
            Tags = tags.Where(row => row.SyncUid != null).Select(ToDto).ToList(),
            // 同步过来的关联可能先于标签到达，没有对应标签的不发
            TagLinks = links.Where(link => tagIds.Contains(link.TagUid))
                .Select(link => new GameTagLinkDto { TagId = link.TagUid, AppId = (int)link.AppId }).ToList(),
            Plans = plans.Where(row => row.SyncUid != null).Select(ToDto).ToList(),
            Challenges = challenges.Where(row => row.SyncUid != null).Select(ToDto).ToList(),
            JournalStats = stats.OrderBy(item => item.AppId)
                .Select(item => new GameJournalStatDto { AppId = (int)item.AppId, Count = item.Count, LastAt = item.LastAt }).ToList()
        };
    }

    #region 坑

    public async Task<GameJourneyDto?> SaveJourneyAsync(GameJourneyInput input, CancellationToken cancellationToken = default)
    {
        var milestones = (input.Milestones ?? [])
            .Select(item => new GameMilestoneDto { Id = Short(item.Id, 32), Title = UserText(item.Title, 120), DoneAt = item.DoneAt })
            .Where(item => item.Id.Length > 0 && item.Title.Length > 0)
            .DistinctBy(item => item.Id)
            .Take(MaxMilestones)
            .ToList();
        var goalNote = Optional(UserText(input.GoalNote, 500));
        var resumeNote = Optional(UserText(input.ResumeNote, 500));
        var summary = Optional(UserText(input.ReviewSummary, 280));
        var scores = input.ReviewScores is { } s && (s.Story ?? s.Gameplay ?? s.Visuals ?? s.Audio ?? s.Replay) != null ? s : null;
        var goalKind = input.GoalKind;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.GameJourneyTable.FirstOrDefaultAsync(item => item.AppId == input.AppId, cancellationToken).ConfigureAwait(false);
        var empty = input.StartedAt == null && goalKind == null && input.GoalHours == null && input.GoalAchievementPercent == null
            && goalNote == null && milestones.Count == 0 && input.CompletionLevel == null && input.ShelvedReason == null
            && input.ResumeAt == null && resumeNote == null && scores == null && summary == null
            && input.Recommend == null && input.WouldReplay == null
            && row?.QueueOrder == null;
        if (empty)
        {
            if (row != null)
            {
                db.GameJourneyTable.Remove(row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return null;
        }
        if (row == null)
        {
            row = new GameJourney { AppId = input.AppId };
            db.GameJourneyTable.Add(row);
        }
        row.StartedAt = input.StartedAt;
        row.GoalKind = goalKind;
        row.GoalHours = goalKind == "playtime" ? input.GoalHours : null;
        row.GoalAchievementPercent = goalKind == "achievements" ? input.GoalAchievementPercent ?? 100 : null;
        row.GoalNote = goalNote;
        row.Milestones = milestones.Count == 0 ? null : JsonSerializer.Serialize(milestones, JsonOptions);
        row.CompletionLevel = input.CompletionLevel;
        row.ShelvedReason = input.ShelvedReason;
        row.ResumeAt = input.ResumeAt;
        row.ResumeNote = resumeNote;
        row.ReviewScores = scores == null ? null : JsonSerializer.Serialize(scores, JsonOptions);
        row.ReviewSummary = summary;
        row.Recommend = input.Recommend;
        row.WouldReplay = input.WouldReplay;
        row.UpdatedAt = Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    public async Task<int> SetQueueOrderAsync(GameQueueOrderSetRequest request, CancellationToken cancellationToken = default)
    {
        var order = request.AppIds.Where(id => id > 0).Distinct().Take(MaxBatch).ToList();
        var now = Now;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = order.Select(id => (long)id).ToList();
        var rows = await db.GameJourneyTable
            .Where(row => row.QueueOrder != null || ids.Contains(row.AppId))
            .ToDictionaryAsync(row => row.AppId, cancellationToken).ConfigureAwait(false);
        foreach (var (appId, row) in rows)
        {
            if (ids.Contains(appId) || row.QueueOrder == null) continue;
            row.QueueOrder = null;
            row.UpdatedAt = now;
            if (IsBlank(row)) db.GameJourneyTable.Remove(row);
        }
        for (var index = 0; index < order.Count; index++)
        {
            if (!rows.TryGetValue(order[index], out var row))
            {
                row = new GameJourney { AppId = order[index] };
                db.GameJourneyTable.Add(row);
            }
            if (row.QueueOrder == index) continue;
            row.QueueOrder = index;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return order.Count;
    }

    private static bool IsBlank(GameJourney row)
        => row.StartedAt == null && row.GoalKind == null && row.GoalHours == null && row.GoalAchievementPercent == null
           && row.GoalNote == null && row.Milestones == null && row.CompletionLevel == null && row.ShelvedReason == null
           && row.ResumeAt == null && row.ResumeNote == null && row.QueueOrder == null && row.ReviewScores == null
           && row.ReviewSummary == null && row.Recommend == null && row.WouldReplay == null;

    #endregion

    #region 心得

    public async Task<IReadOnlyList<GameJournalEntryDto>> GetJournalAsync(GameJournalQueryRequest? request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.GameJournalTable.AsNoTracking().Where(row => row.SyncUid != null);
        if (request?.AppId is { } appId) query = query.Where(row => row.AppId == appId);
        var rows = await query.OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
            .Take(request?.Limit ?? (request?.AppId == null ? 50 : 500))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    public async Task<GameJournalEntryDto> SaveJournalAsync(GameJournalSaveRequest request, CancellationToken cancellationToken = default)
    {
        var body = UserText(request.Body, 20000);
        if (body.Length == 0) throw new ArgumentException("A journal entry needs some text.", nameof(request));
        var now = Now;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindOrAddAsync(db.GameJournalTable, request.Id, () => new GameJournalEntry { CreatedAt = now }, cancellationToken).ConfigureAwait(false);
        row.AppId = request.AppId;
        row.Kind = request.Kind;
        row.Body = body;
        row.Spoiler = request.Spoiler;
        row.Mood = request.Mood;
        if (request.CreatedAt is { } created && created > 0 && created <= now + 86400) row.CreatedAt = created;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    #endregion

    #region 周目

    public async Task<GamePlaythroughDto> SavePlaythroughAsync(GamePlaythroughSaveRequest request, CancellationToken cancellationToken = default)
    {
        var now = Now;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindOrAddAsync(db.GamePlaythroughTable, request.Id, () => new GamePlaythrough { CreatedAt = now }, cancellationToken).ConfigureAwait(false);
        row.AppId = request.AppId;
        row.Label = Optional(UserText(request.Label, 80));
        row.StartedAt = request.StartedAt;
        row.EndedAt = request.EndedAt is { } ended && (request.StartedAt == null || ended >= request.StartedAt) ? ended : null;
        row.Outcome = request.Outcome;
        row.CompletionLevel = request.Outcome == "completed" ? request.CompletionLevel : null;
        row.PlaytimeMinutes = request.PlaytimeMinutes;
        row.Rating = request.Rating;
        row.Note = Optional(UserText(request.Note, 2000));
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    #endregion

    #region 标签

    public async Task<GameTagDto> SaveTagAsync(GameTagSaveRequest request, CancellationToken cancellationToken = default)
    {
        var name = UserText(request.Name, 24);
        if (name.Length == 0) throw new ArgumentException("A tag needs a name.", nameof(request));
        var now = Now;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var nextOrder = (await db.GameTagTable.MaxAsync(row => (int?)row.SortOrder, cancellationToken).ConfigureAwait(false) ?? -1) + 1;
        var row = await FindOrAddAsync(db.GameTagTable, request.Id, () => new GameTag { CreatedAt = now, SortOrder = nextOrder }, cancellationToken).ConfigureAwait(false);
        row.Name = name;
        row.Color = request.Color;
        row.Icon = string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim();
        if (request.SortOrder is { } order) row.SortOrder = order;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    public async Task<IReadOnlyList<GameTagLinkDto>> SetTagLinksAsync(GameTagLinksSetRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var wanted = request.TagIds.Distinct(StringComparer.Ordinal).ToList();
        var known = await db.GameTagTable.Where(tag => tag.SyncUid != null && wanted.Contains(tag.SyncUid!))
            .Select(tag => tag.SyncUid!).ToListAsync(cancellationToken).ConfigureAwait(false);
        var target = wanted.Where(known.Contains).Take(MaxTagsPerGame).ToHashSet(StringComparer.Ordinal);
        var existing = await db.GameTagLinkTable.Where(link => link.AppId == request.AppId).ToListAsync(cancellationToken).ConfigureAwait(false);
        db.GameTagLinkTable.RemoveRange(existing.Where(link => !target.Contains(link.TagUid)));
        var now = Now;
        foreach (var tagId in target.Where(id => existing.All(link => link.TagUid != id)))
            db.GameTagLinkTable.Add(new GameTagLink { TagUid = tagId, AppId = request.AppId, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return wanted.Where(target.Contains).Select(id => new GameTagLinkDto { TagId = id, AppId = request.AppId }).ToList();
    }

    public async Task<int> AssignTagAsync(GameTagAssignRequest request, CancellationToken cancellationToken = default)
    {
        var appIds = request.AppIds.Where(id => id > 0).Distinct().Take(MaxBatch).Select(id => (long)id).ToList();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.GameTagTable.AnyAsync(tag => tag.SyncUid == request.TagId, cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException("The tag no longer exists.");
        var linked = await db.GameTagLinkTable.Where(link => link.TagUid == request.TagId && appIds.Contains(link.AppId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        int changed;
        if (request.Assigned)
        {
            var now = Now;
            var missing = appIds.Except(linked.Select(link => link.AppId)).ToList();
            db.GameTagLinkTable.AddRange(missing.Select(appId => new GameTagLink { TagUid = request.TagId, AppId = appId, CreatedAt = now }));
            changed = missing.Count;
        }
        else
        {
            db.GameTagLinkTable.RemoveRange(linked);
            changed = linked.Count;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    #endregion

    #region 计划与挑战

    public async Task<PlayPlanDto> SavePlanAsync(PlayPlanSaveRequest request, CancellationToken cancellationToken = default)
    {
        var title = Optional(UserText(request.Title, 80));
        if (request.AppId == null && title == null) throw new ArgumentException("A plan needs a game or a title.", nameof(request));
        if (request.Kind == "bucket" && request.Bucket == null) throw new ArgumentException("A bucket plan needs a period.", nameof(request));
        var now = Now;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindOrAddAsync(db.PlayPlanTable, request.Id, () => new PlayPlan { CreatedAt = now }, cancellationToken).ConfigureAwait(false);
        row.AppId = request.AppId;
        row.Title = title;
        row.Kind = request.Kind;
        row.StartDay = request.StartDay;
        row.EndDay = request.Kind != "day" && request.EndDay is { } end && end > request.StartDay ? end : null;
        row.Bucket = request.Kind == "bucket" ? request.Bucket : null;
        row.EstimatedMinutes = request.EstimatedMinutes;
        row.Status = request.Status;
        row.Note = Optional(UserText(request.Note, 2000));
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    public async Task<PlayChallengeDto> SaveChallengeAsync(PlayChallengeSaveRequest request, CancellationToken cancellationToken = default)
    {
        var now = Now;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await FindOrAddAsync(db.PlayChallengeTable, request.Id, () => new PlayChallenge { CreatedAt = now }, cancellationToken).ConfigureAwait(false);
        row.Year = request.Year;
        row.Kind = request.Kind;
        row.Target = request.Target;
        row.Title = Optional(UserText(request.Title, 80));
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    #endregion

    public async Task<bool> DeleteItemAsync(PlannerItemDeleteRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var id = request.Id;
        var deleted = request.Kind switch
        {
            "journal" => await db.GameJournalTable.Where(row => row.SyncUid == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false),
            "playthrough" => await db.GamePlaythroughTable.Where(row => row.SyncUid == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false),
            "plan" => await db.PlayPlanTable.Where(row => row.SyncUid == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false),
            "challenge" => await db.PlayChallengeTable.Where(row => row.SyncUid == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false),
            "tag" => await DeleteTagAsync(db, id, cancellationToken).ConfigureAwait(false),
            _ => 0
        };
        return deleted > 0;
    }

    private static async Task<int> DeleteTagAsync(AppDbContext db, string id, CancellationToken cancellationToken)
    {
        await db.GameTagLinkTable.Where(link => link.TagUid == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return await db.GameTagTable.Where(row => row.SyncUid == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>有 Id 时更新那一条（同步 ID），否则新建并生成同步 ID。</summary>
    private static async Task<T> FindOrAddAsync<T>(DbSet<T> set, string? id, Func<T> create, CancellationToken cancellationToken)
        where T : class
    {
        if (!string.IsNullOrEmpty(id))
        {
            return await set.FirstOrDefaultAsync(row => EF.Property<string>(row, "SyncUid") == id, cancellationToken).ConfigureAwait(false)
                   ?? throw new KeyNotFoundException("The item no longer exists.");
        }
        var created = create();
        set.Entry(created).Property("SyncUid").CurrentValue = NewUid();
        set.Add(created);
        return created;
    }

    #region 映射

    private static GameJourneyDto ToDto(GameJourney row) => new()
    {
        AppId = (int)row.AppId,
        StartedAt = row.StartedAt,
        GoalKind = row.GoalKind,
        GoalHours = row.GoalHours,
        GoalAchievementPercent = row.GoalAchievementPercent,
        GoalNote = row.GoalNote,
        Milestones = Parse<List<GameMilestoneDto>>(row.Milestones) ?? [],
        CompletionLevel = row.CompletionLevel,
        ShelvedReason = row.ShelvedReason,
        ResumeAt = row.ResumeAt,
        ResumeNote = row.ResumeNote,
        QueueOrder = row.QueueOrder,
        ReviewScores = Parse<GameReviewScoresDto>(row.ReviewScores),
        ReviewSummary = row.ReviewSummary,
        Recommend = row.Recommend,
        WouldReplay = row.WouldReplay,
        UpdatedAt = row.UpdatedAt
    };

    private static GameJournalEntryDto ToDto(GameJournalEntry row) => new()
    {
        Id = row.SyncUid!,
        AppId = (int)row.AppId,
        Kind = row.Kind,
        Body = row.Body,
        Spoiler = row.Spoiler,
        Mood = row.Mood,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };

    private static GamePlaythroughDto ToDto(GamePlaythrough row) => new()
    {
        Id = row.SyncUid!,
        AppId = (int)row.AppId,
        Label = row.Label,
        StartedAt = row.StartedAt,
        EndedAt = row.EndedAt,
        Outcome = row.Outcome,
        CompletionLevel = row.CompletionLevel,
        PlaytimeMinutes = row.PlaytimeMinutes,
        Rating = row.Rating,
        Note = row.Note,
        UpdatedAt = row.UpdatedAt
    };

    private static GameTagDto ToDto(GameTag row) => new()
    {
        Id = row.SyncUid!,
        Name = row.Name,
        Color = row.Color,
        Icon = row.Icon,
        SortOrder = row.SortOrder,
        UpdatedAt = row.UpdatedAt
    };

    private static PlayPlanDto ToDto(PlayPlan row) => new()
    {
        Id = row.SyncUid!,
        AppId = row.AppId is { } appId ? (int)appId : null,
        Title = row.Title,
        Kind = row.Kind,
        StartDay = row.StartDay,
        EndDay = row.EndDay,
        Bucket = row.Bucket,
        EstimatedMinutes = row.EstimatedMinutes,
        Status = row.Status,
        Note = row.Note,
        UpdatedAt = row.UpdatedAt
    };

    private static PlayChallengeDto ToDto(PlayChallenge row) => new()
    {
        Id = row.SyncUid!,
        Year = row.Year,
        Kind = row.Kind,
        Target = row.Target,
        Title = row.Title,
        UpdatedAt = row.UpdatedAt
    };

    /// <summary>同步来的 JSON 可能是别的版本写的，读不了时当作没有。</summary>
    private static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    #endregion

    /// <summary>渲染层传来的文字是 s1: 编码；不是编码的按原文处理。</summary>
    internal static string UserText(string? value, int maxLength)
    {
        var text = (IpcSecret.Decode(value) ?? value ?? string.Empty).Trim();
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private static string? Optional(string text) => text.Length == 0 ? null : text;

    private static string Short(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length > maxLength ? text[..maxLength] : text;
    }
}
