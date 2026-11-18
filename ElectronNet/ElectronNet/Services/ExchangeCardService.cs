using System.Text;
using System.Text.Json;
using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Infrastructure;
using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Circle;
using SteamStat.Core.Environment;
using SteamStat.Core.Insights;

namespace ElectronNet.Services;

/// <summary>
/// 交换卡和双人回顾。卡由 Core 的 <see cref="ExchangeCardCodec"/> 编码，这里只管生成、存取和文件对话框。
/// 导入的卡和好友记录一样不进同步：是别人的数据，每台设备自己导入。
/// </summary>
internal sealed class ExchangeCardService(
    CircleService circle,
    IDbContextFactory<AppDbContext> dbContextFactory,
    SteamStat.Core.Steam.Cache.SteamFeatureSnapshotStore snapshotStore,
    IMainWindowAccessor mainWindowAccessor,
    AppEnvironment environment,
    TimeProvider timeProvider,
    ILogger<ExchangeCardService> logger)
{
    internal const string Extension = "sscard";
    private const int MaxFileBytes = 512 * 1024;

    public async Task<ExchangeCardExportDto> ExportAsync(ExchangeCardExportRequest request, CancellationToken cancellationToken = default)
    {
        var (card, _) = await BuildMineAsync(request.SteamIds, request.Year, MemoirJournalService.UserText(request.Name, 64), cancellationToken).ConfigureAwait(false);
        var passphrase = ElectronNet.Infrastructure.IpcSecret.Decode(request.Passphrase) ?? string.Empty;
        var code = ExchangeCardCodec.Encode(card, passphrase.Length > 0 ? passphrase : null);
        return new ExchangeCardExportDto { Code = code, Protected = passphrase.Length > 0, Card = ToDto(card, null, null, false) };
    }

    internal async Task<(CardPayload Card, CircleService.Loaded Loaded)> BuildMineAsync(IReadOnlyList<string>? steamIds, int year, string? name, CancellationToken cancellationToken)
    {
        var loaded = await circle.LoadAsync(steamIds, cancellationToken).ConfigureAwait(false);
        // 本机没有任何 Steam 账号时界面上没有能生成卡的数据，走到这里是缺陷
        if (string.IsNullOrEmpty(loaded.Primary)) throw new InvalidOperationException("No local Steam account to build an exchange card from");
        var persona = PersonaAnalyzer.ByYear(loaded.Archive).FirstOrDefault(item => item.Year == year);
        var display = string.IsNullOrWhiteSpace(name) ? await circle.MyNameAsync(loaded, cancellationToken).ConfigureAwait(false) : name;
        var card = ExchangeCardBuilder.Build(loaded.Archive, loaded.Primary, display, Math.Clamp(year, 2003, 2200), persona);
        return (ExchangeCardCodec.Sanitize(card) ?? throw new InvalidOperationException("The exchange card failed its own validation"), loaded);
    }

    public async Task<ExchangeCardImportResultDto> ImportAsync(ExchangeCardImportRequest request, CancellationToken cancellationToken = default)
    {
        var text = ElectronNet.Infrastructure.IpcSecret.Decode(request.Text) ?? request.Text;
        var passphrase = ElectronNet.Infrastructure.IpcSecret.Decode(request.Passphrase);
        var result = ExchangeCardCodec.Decode(text, passphrase);
        if (result.Status != CardDecodeStatus.Ok || result.Card is not { } card)
            return new ExchangeCardImportResultDto
            {
                Status = result.Status switch
                {
                    CardDecodeStatus.NeedsPassphrase => "needsPassphrase",
                    CardDecodeStatus.WrongPassphrase => "wrongPassphrase",
                    CardDecodeStatus.UnsupportedVersion => "unsupported",
                    _ => "invalid"
                },
                Updated = false
            };

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var local = await db.SteamUserTable.AsNoTracking().Select(user => user.SteamId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (local.Contains(card.SteamId)) return new ExchangeCardImportResultDto { Status = "self", Updated = false };

        var payload = JsonSerializer.Serialize(card, ExchangeCardCodec.Json);
        var existing = await db.ExchangeCardTable.FirstOrDefaultAsync(row => row.SenderSteamId == card.SteamId && row.GeneratedAt == card.GeneratedAt, cancellationToken).ConfigureAwait(false);
        var earlier = await db.ExchangeCardTable.AnyAsync(row => row.SenderSteamId == card.SteamId && row.GeneratedAt != card.GeneratedAt, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (existing != null) db.ExchangeCardTable.Remove(existing);
        var record = new ExchangeCardRecord
        {
            SenderSteamId = card.SteamId,
            SenderName = card.Name,
            Year = card.Year,
            GeneratedAt = card.GeneratedAt,
            ImportedAt = now,
            Payload = payload
        };
        db.ExchangeCardTable.Add(record);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        circle.Invalidate();
        var friends = await FriendIdsAsync(cancellationToken).ConfigureAwait(false);
        return new ExchangeCardImportResultDto { Status = "ok", Card = ToDto(card, record.Id, now, friends.Contains(card.SteamId)), Updated = earlier };
    }

    public async Task<IReadOnlyList<ExchangeCardDto>> GetCardsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ExchangeCardTable.AsNoTracking().OrderByDescending(row => row.GeneratedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var friends = await FriendIdsAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => (Row: row, Card: Parse(row.Payload)))
            .Where(item => item.Card != null)
            .Select(item => ToDto(item.Card!, item.Row.Id, item.Row.ImportedAt, friends.Contains(item.Row.SenderSteamId)))
            .ToList();
    }

    public async Task<bool> DeleteAsync(ExchangeCardIdRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await db.ExchangeCardTable.Where(row => row.Id == request.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        circle.Invalidate();
        return deleted > 0;
    }

    /// <summary>每个人的全部卡（最新的在前）。存的时候已经校验过，读的时候再过一遍：旧版本存下的卡也按现在的规则裁。</summary>
    internal static async Task<IReadOnlyList<CardPayload>> LoadCardsAsync(AppDbContext db, CancellationToken cancellationToken)
        => (await db.ExchangeCardTable.AsNoTracking().OrderByDescending(row => row.GeneratedAt).Select(row => row.Payload).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(Parse)
            .OfType<CardPayload>()
            .ToList();

    private static CardPayload? Parse(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<CardPayload>(payload, ExchangeCardCodec.Json) is { } card ? ExchangeCardCodec.Sanitize(card) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<HashSet<string>> FriendIdsAsync(CancellationToken cancellationToken)
    {
        var snapshots = await snapshotStore.GetFriendsAsync(cancellationToken).ConfigureAwait(false);
        return snapshots.SelectMany(snapshot => snapshot.Value.Friends).Where(friend => friend.Relationship == 3).Select(friend => friend.SteamId).ToHashSet(StringComparer.Ordinal);
    }

    internal static ExchangeCardDto ToDto(CardPayload card, int? id, long? importedAt, bool isFriend) => new()
    {
        Id = id,
        SteamId = card.SteamId,
        Name = card.Name,
        Year = card.Year,
        GeneratedAt = card.GeneratedAt,
        ImportedAt = importedAt,
        UtcOffsetMinutes = card.UtcOffsetMinutes,
        LifetimeMinutes = card.Totals.LifetimeMinutes,
        YearMinutes = card.Totals.YearMinutes,
        Last7Minutes = card.Totals.Last7Minutes,
        Last30Minutes = card.Totals.Last30Minutes,
        OwnedGames = card.Totals.OwnedGames,
        PlayedGames = card.Totals.PlayedGames,
        CompletedGames = card.Totals.CompletedGames,
        Achievements = card.Totals.Achievements,
        YearAchievements = card.Totals.YearAchievements,
        Months = card.Months,
        Heatmap = card.Heatmap,
        Games = card.Games.Select(game => new ExchangeCardGameDto
        {
            AppId = game.AppId,
            Name = game.Name,
            Minutes = game.Minutes,
            YearMinutes = game.YearMinutes,
            Unlocked = game.Unlocked,
            Total = game.Total,
            LastPlayed = game.LastPlayed
        }).ToList(),
        PersonaCode = card.Persona?.Code,
        PersonaZh = card.Persona?.Zh,
        PersonaEn = card.Persona?.En,
        IsFriend = isFriend
    };

    #region 文件

    private string? DialogOverride()
        => environment.IsDevelopment ? Environment.GetEnvironmentVariable("STEAM_STAT_E2E_DIALOG_PATH") : null;

    /// <summary>卡文件就是卡的文字加一行说明：用记事本打开也知道是什么，导入时只认卡本身。</summary>
    public async Task<bool> SaveFileAsync(ExchangeCardSaveRequest request, CancellationToken cancellationToken = default)
    {
        var code = ExchangeCardCodec.Extract(request.Code);
        if (code == null) return false;
        var path = await PickSaveAsync($"steam-stat-card-{request.Year}.{Extension}").ConfigureAwait(false);
        if (path == null) return false;
        var content = new StringBuilder()
            .AppendLine("Steam Stat 交换卡 / exchange card")
            .AppendLine("在 Steam Stat 的「好友 → 交换卡」导入。Import it in Steam Stat under Friends → Exchange cards.")
            .AppendLine()
            .AppendLine(code)
            .ToString();
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ExchangeCardFileDto> OpenFileAsync(CancellationToken cancellationToken = default)
    {
        var path = DialogOverride() is { Length: > 0 } overridden ? overridden : await PickOpenAsync().ConfigureAwait(false);
        if (path == null) return new ExchangeCardFileDto { Cancelled = true };
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return new ExchangeCardFileDto { Cancelled = false };
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return new ExchangeCardFileDto { Text = ExchangeCardCodec.Extract(text), Cancelled = false };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogInformation("Could not read an exchange card file with {ExceptionType}", exception.GetType().Name);
            return new ExchangeCardFileDto { Cancelled = false };
        }
    }

    private async Task<string?> PickSaveAsync(string name)
    {
        if (DialogOverride() is { Length: > 0 } overridden) return overridden;
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return null;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        try
        {
            var path = await Electron.Dialog.ShowSaveDialogAsync(snapshot.Window, new SaveDialogOptions
            {
                DefaultPath = string.IsNullOrEmpty(documents) ? name : Path.Combine(documents, name),
                Filters = [new FileFilter { Name = "Steam Stat Card", Extensions = [Extension] }]
            }).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Path.HasExtension(path) ? path : $"{path}.{Extension}";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to show the exchange card save dialog");
            return null;
        }
    }

    private async Task<string?> PickOpenAsync()
    {
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return null;
        try
        {
            var files = await Electron.Dialog.ShowOpenDialogAsync(snapshot.Window, new OpenDialogOptions
            {
                DefaultPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Properties = [OpenDialogProperty.openFile],
                Filters = [new FileFilter { Name = "Steam Stat Card", Extensions = [Extension, "txt"] }]
            }).ConfigureAwait(false);
            return files is { Length: > 0 } ? files[0] : null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to show the exchange card open dialog");
            return null;
        }
    }

    #endregion

    #region 双人回顾

    public async Task<DuoReviewDto?> GetDuoReviewAsync(DuoReviewRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ExchangeCardTable.AsNoTracking().Where(row => row.SenderSteamId == request.FriendSteamId)
            .OrderByDescending(row => row.GeneratedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        // 优先用同一年的卡，没有就用最新的一张，界面说明年份不同
        var row = rows.FirstOrDefault(item => item.Year == request.Year) ?? rows.FirstOrDefault();
        if (row == null || Parse(row.Payload) is not { } theirs) return null;

        var (mine, loaded) = await BuildMineAsync(request.SteamIds, request.Year, null, cancellationToken).ConfigureAwait(false);
        var options = loaded.Archive.Options;
        var together = loaded.Together
            .Where(segment => segment.FriendSteamId == request.FriendSteamId && options.LogicalDate(segment.Start).Year == request.Year)
            .ToList();
        long? since = loaded.OfficialSince.TryGetValue(request.FriendSteamId, out var official) ? official
            : loaded.Relationship.Where(change => change.SteamId == request.FriendSteamId && change.Kind == "added").Select(change => (long?)change.Time).LastOrDefault();
        var report = DuoReview.Build(mine, theirs, together, since);
        var names = mine.Games.Concat(theirs.Games).GroupBy(game => game.AppId).ToDictionary(group => group.Key, group => group.First().Name);
        string NameOf(int appId) => names.TryGetValue(appId, out var name) && name.Length > 0 ? name : $"App {appId}";
        DuoGameDto Map(DuoGame game) => new() { AppId = game.AppId, Name = game.Name.Length > 0 ? game.Name : NameOf(game.AppId), Mine = game.Mine, Theirs = game.Theirs };
        return new DuoReviewDto
        {
            Year = report.Year,
            TheirYear = report.TheirYear,
            TheirGeneratedAt = report.TheirGeneratedAt,
            MyName = mine.Name,
            TheirName = loaded.People.ContainsKey(theirs.SteamId) ? loaded.NameOf(theirs.SteamId) : theirs.Name,
            TheirSteamId = theirs.SteamId,
            MyMinutes = report.MyMinutes,
            TheirMinutes = report.TheirMinutes,
            MyMonths = report.MyMonths,
            TheirMonths = report.TheirMonths,
            Shared = report.Shared.Take(12).Select(Map).ToList(),
            OnlyMine = report.OnlyMine.Select(Map).ToList(),
            OnlyTheirs = report.OnlyTheirs.Select(Map).ToList(),
            Schedule = report.Schedule,
            Taste = report.Taste,
            BestCell = report.BestCell,
            MyPeakCell = report.MyPeakCell,
            TheirPeakCell = report.TheirPeakCell,
            BusiestMonth = report.BusiestMonth,
            TogetherSeconds = report.TogetherSeconds,
            ConfirmedSeconds = report.ConfirmedSeconds,
            FirstTogether = report.FirstTogether,
            TogetherGames = report.TogetherGames.Select(game => new DuoTogetherGameDto { AppId = game.AppId, Name = NameOf(game.AppId), Seconds = game.Seconds, ConfirmedSeconds = game.ConfirmedSeconds }).ToList(),
            MyAchievements = report.MyAchievements,
            TheirAchievements = report.TheirAchievements,
            MyPersona = report.MyPersona,
            TheirPersona = report.TheirPersona,
            SameAxes = report.SameAxes,
            Since = report.Since,
            MyHeatmap = report.MyHeatmap,
            TheirHeatmap = report.TheirHeatmap
        };
    }

    #endregion
}
