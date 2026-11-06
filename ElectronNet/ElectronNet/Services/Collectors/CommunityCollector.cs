using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using SteamStat.Core.Archives;
using SteamStat.Core.Archives.Web;
using SteamStat.Core.Sessions;
using SteamStat.Core.Settings;

namespace ElectronNet.Services.Collectors;

/// <summary>社区足迹入库。从页面上消失的内容保留，标记 MissingSince：这正是这份档案的价值。</summary>
internal sealed class CommunityArchive(
    IDbContextFactory<AppDbContext> dbContextFactory,
    TimeProvider timeProvider)
{
    public async Task ApplyReviewsAsync(string steamId, IReadOnlyList<CommunityReview> reviews, CancellationToken cancellationToken = default)
    {
        if (reviews.Count == 0) return;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = reviews.Select(review => review.ReviewId).ToList();
        var existing = await db.CommunityReviewTable.Where(row => row.SteamId == steamId && ids.Contains(row.ReviewId)).ToDictionaryAsync(row => row.ReviewId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach (var review in reviews)
        {
            if (!existing.TryGetValue(review.ReviewId, out var row))
            {
                row = new CommunityReviewRecord { SteamId = steamId, ReviewId = review.ReviewId, FirstSeenAt = now };
                db.CommunityReviewTable.Add(row);
                existing[review.ReviewId] = row;
            }
            row.AppId = review.AppId;
            row.Recommended = review.Recommended;
            row.HoursTotal = review.HoursTotal;
            row.HoursAtReview = review.HoursAtReview ?? row.HoursAtReview;
            row.PostedAt = review.PostedAt ?? row.PostedAt;
            row.EditedAt = review.EditedAt ?? row.EditedAt;
            row.Text = review.Text;
            row.Helpful = review.Helpful;
            row.Funny = review.Funny;
            row.LastSeenAt = now;
            row.MissingSince = null;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyFilesAsync(string steamId, string kind, IReadOnlyList<CommunityFile> files, CancellationToken cancellationToken = default)
    {
        if (files.Count == 0) return;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = files.Select(file => file.PublishedFileId).ToList();
        var existing = await db.CommunityFileTable.Where(row => row.SteamId == steamId && row.Kind == kind && ids.Contains(row.PublishedFileId)).ToDictionaryAsync(row => row.PublishedFileId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            if (!existing.TryGetValue(file.PublishedFileId, out var row))
            {
                row = new CommunityFileRecord { SteamId = steamId, Kind = kind, PublishedFileId = file.PublishedFileId, FirstSeenAt = now };
                db.CommunityFileTable.Add(row);
                existing[file.PublishedFileId] = row;
            }
            row.AppId = file.AppId;
            row.Title = file.Title;
            row.Description = file.Description ?? row.Description;
            row.Author = file.Author ?? row.Author;
            row.PreviewUrl = file.PreviewUrl ?? row.PreviewUrl;
            row.LastSeenAt = now;
            row.MissingSince = null;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyCommentsAsync(string steamId, IReadOnlyList<CommunityComment> comments, CancellationToken cancellationToken = default)
    {
        if (comments.Count == 0) return;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = comments.Select(comment => comment.CommentId).ToList();
        var existing = await db.CommunityCommentTable.Where(row => row.SteamId == steamId && ids.Contains(row.CommentId)).ToDictionaryAsync(row => row.CommentId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach (var comment in comments)
        {
            if (!existing.TryGetValue(comment.CommentId, out var row))
            {
                row = new CommunityCommentRecord { SteamId = steamId, CommentId = comment.CommentId, FirstSeenAt = now };
                db.CommunityCommentTable.Add(row);
                existing[comment.CommentId] = row;
            }
            row.AuthorSteamId = comment.AuthorSteamId;
            row.AuthorName = comment.AuthorName;
            row.PostedAt = comment.PostedAt;
            row.Text = comment.Text;
            row.LastSeenAt = now;
            row.MissingSince = null;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 这一轮完整翻完之后，没再看到的就是消失了。只有翻完才能这么判断：
    /// 中途失败时什么都不标，否则网络抖一下所有评测都成了「已删除」。
    /// </summary>
    public async Task MarkMissingAsync(string steamId, string kind, long sweepStartedAt, long? notBefore, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        switch (kind)
        {
            case WebCaptureKinds.Reviews:
                foreach (var row in await db.CommunityReviewTable.Where(row => row.SteamId == steamId && row.LastSeenAt < sweepStartedAt && row.MissingSince == null).ToListAsync(cancellationToken).ConfigureAwait(false))
                    row.MissingSince = now;
                break;
            case WebCaptureKinds.Comments:
                // 留言只翻了最新的一部分，比这更早的没有看过，不能算消失
                foreach (var row in await db.CommunityCommentTable.Where(row => row.SteamId == steamId && row.LastSeenAt < sweepStartedAt && row.MissingSince == null && (notBefore == null || row.PostedAt >= notBefore)).ToListAsync(cancellationToken).ConfigureAwait(false))
                    row.MissingSince = now;
                break;
            default:
                foreach (var row in await db.CommunityFileTable.Where(row => row.SteamId == steamId && row.Kind == kind && row.LastSeenAt < sweepStartedAt && row.MissingSince == null).ToListAsync(cancellationToken).ConfigureAwait(false))
                    row.MissingSince = now;
                break;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>重新解析时只入库，不判断消失：旧页面不代表现在的状态。</summary>
    public async Task<(WebPageState State, int Items, string? WalletCurrency)> ReparseAsync(WebCaptureRecord capture, string content, CancellationToken cancellationToken = default)
    {
        var capturedAt = DateTimeOffset.FromUnixTimeSeconds(capture.CapturedAt);
        switch (capture.Kind)
        {
            case WebCaptureKinds.Reviews:
                var reviews = CommunityParsers.ParseReviews(content, capturedAt);
                if (reviews.State == WebPageState.Ok) await ApplyReviewsAsync(capture.SteamId, reviews.Reviews, cancellationToken).ConfigureAwait(false);
                return (reviews.State, reviews.Reviews.Count, null);
            case WebCaptureKinds.Comments:
                var comments = CommunityParsers.ParseComments(content);
                if (comments.State == WebPageState.Ok) await ApplyCommentsAsync(capture.SteamId, comments.Comments, cancellationToken).ConfigureAwait(false);
                return (comments.State, comments.Comments.Count, null);
            default:
                var files = CommunityParsers.ParseFiles(content);
                if (files.State == WebPageState.Ok) await ApplyFilesAsync(capture.SteamId, capture.Kind, files.Files, cancellationToken).ConfigureAwait(false);
                return (files.State, files.Files.Count, null);
        }
    }
}

/// <summary>
/// 社区足迹：评测、指南、工坊收藏、资料留言。页面结构会变，所以每种内容都先存原始页面。
/// 一种内容失败不影响其他种类，但失败原因要报出来。
/// </summary>
internal sealed class CommunityCollector(
    ISteamArchiveGateway gateway,
    SteamWebFetcher fetcher,
    WebArchiveStore store,
    CommunityArchive archive,
    ISteamSessionAccessor sessions,
    ISteamAccountIdentityProvider identity,
    ISettingsStore settingsStore,
    TimeProvider timeProvider) : IArchiveCollector
{
    private const int MaxPages = 60;
    private const int MaxCommentPages = 40;
    private const int CommentPageSize = 50;
    private const int ReviewsPerPage = 10;
    private const int FilesPerPage = 30;

    internal TimeSpan Spacing { get; set; } = TimeSpan.FromSeconds(3);

    public string Id => "community";

    public TimeSpan Interval => TimeSpan.FromDays(7);

    public async Task<CollectorOutcome> CollectAsync(CancellationToken cancellationToken)
    {
        if (settingsStore.GetSettings().Archive?.WebCaptureEnabled == false) return CollectorOutcome.Skipped;
        var accounts = ArchiveAccounts.LoggedIn(sessions, identity);
        if (accounts.Count == 0) return CollectorOutcome.Skipped;

        var total = 0;
        string? error = null;
        foreach (var account in accounts)
        {
            var session = await gateway.GetWebSessionAsync(account.AccountName, cancellationToken).ConfigureAwait(false);
            if (session == null) continue;
            var steamId = account.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var step in new Func<SteamWebSession, string, CancellationToken, Task<(int Items, string? Error)>>[] { CollectReviewsAsync, CollectGuidesAsync, CollectFavoritesAsync, CollectCommentsAsync })
            {
                var (items, failure) = await step(session, steamId, cancellationToken).ConfigureAwait(false);
                total += items;
                error ??= failure;
                if (failure == CollectorErrors.SignedOut) return CollectorOutcome.Failed(failure);
            }
        }
        return error == null ? CollectorOutcome.Ok(total) : CollectorOutcome.Failed(error);
    }

    private async Task<(int Items, string? Error)> CollectReviewsAsync(SteamWebSession session, string steamId, CancellationToken cancellationToken)
    {
        var sweep = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var items = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            var fetched = await fetcher.GetAsync(session, SteamWebAllowlist.Reviews(session.SteamId, page), cancellationToken).ConfigureAwait(false);
            if (fetched.Status != WebFetchStatus.Ok) return (items, BillingCollector.ToError(fetched.Status));
            var parsed = CommunityParsers.ParseReviews(fetched.Body!, timeProvider.GetUtcNow());
            if (parsed.State == WebPageState.SignedOut) return (items, CollectorErrors.SignedOut);
            await store.SaveAsync(steamId, WebCaptureKinds.Reviews, page, fetched.Body!, CommunityParsers.ParserVersion, Label(parsed.State), parsed.Reviews.Count, cancellationToken).ConfigureAwait(false);
            if (parsed.State == WebPageState.LayoutChanged) return (items, CollectorErrors.Layout);
            await archive.ApplyReviewsAsync(steamId, parsed.Reviews, cancellationToken).ConfigureAwait(false);
            items += parsed.Reviews.Count;
            if (parsed.Reviews.Count == 0 || page * ReviewsPerPage >= parsed.Total)
            {
                await Finish(steamId, WebCaptureKinds.Reviews, sweep, null, items, cancellationToken).ConfigureAwait(false);
                return (items, null);
            }
            await Task.Delay(Spacing, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        return (items, null);
    }

    private Task<(int Items, string? Error)> CollectGuidesAsync(SteamWebSession session, string steamId, CancellationToken cancellationToken)
        => CollectFilesAsync(session, steamId, WebCaptureKinds.Guides, page => SteamWebAllowlist.Guides(session.SteamId, page), cancellationToken);

    private Task<(int Items, string? Error)> CollectFavoritesAsync(SteamWebSession session, string steamId, CancellationToken cancellationToken)
        => CollectFilesAsync(session, steamId, WebCaptureKinds.Favorites, page => SteamWebAllowlist.Favorites(session.SteamId, page), cancellationToken);

    private async Task<(int Items, string? Error)> CollectFilesAsync(SteamWebSession session, string steamId, string kind, Func<int, Uri> address, CancellationToken cancellationToken)
    {
        var sweep = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var items = 0;
        for (var page = 1; page <= MaxPages; page++)
        {
            var fetched = await fetcher.GetAsync(session, address(page), cancellationToken).ConfigureAwait(false);
            if (fetched.Status != WebFetchStatus.Ok) return (items, BillingCollector.ToError(fetched.Status));
            var parsed = CommunityParsers.ParseFiles(fetched.Body!);
            if (parsed.State == WebPageState.SignedOut) return (items, CollectorErrors.SignedOut);
            await store.SaveAsync(steamId, kind, page, fetched.Body!, CommunityParsers.ParserVersion, Label(parsed.State), parsed.Files.Count, cancellationToken).ConfigureAwait(false);
            if (parsed.State == WebPageState.LayoutChanged) return (items, CollectorErrors.Layout);
            await archive.ApplyFilesAsync(steamId, kind, parsed.Files, cancellationToken).ConfigureAwait(false);
            items += parsed.Files.Count;
            if (parsed.Files.Count == 0 || page * FilesPerPage >= parsed.Total)
            {
                await Finish(steamId, kind, sweep, null, items, cancellationToken).ConfigureAwait(false);
                return (items, null);
            }
            await Task.Delay(Spacing, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        return (items, null);
    }

    private async Task<(int Items, string? Error)> CollectCommentsAsync(SteamWebSession session, string steamId, CancellationToken cancellationToken)
    {
        var sweep = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var items = 0;
        long? oldest = null;
        for (var page = 0; page < MaxCommentPages; page++)
        {
            var fetched = await fetcher.GetAsync(session, SteamWebAllowlist.Comments(session.SteamId, page * CommentPageSize, CommentPageSize), cancellationToken).ConfigureAwait(false);
            if (fetched.Status != WebFetchStatus.Ok) return (items, BillingCollector.ToError(fetched.Status));
            var parsed = CommunityParsers.ParseComments(fetched.Body!);
            await store.SaveAsync(steamId, WebCaptureKinds.Comments, page, fetched.Body!, CommunityParsers.ParserVersion, Label(parsed.State), parsed.Comments.Count, cancellationToken).ConfigureAwait(false);
            if (parsed.State != WebPageState.Ok) return (items, CollectorErrors.Layout);
            await archive.ApplyCommentsAsync(steamId, parsed.Comments, cancellationToken).ConfigureAwait(false);
            items += parsed.Comments.Count;
            if (parsed.Comments.Count > 0) oldest = Math.Min(oldest ?? long.MaxValue, parsed.Comments.Min(comment => comment.PostedAt));
            var reachedEnd = parsed.Comments.Count == 0 || (page + 1) * CommentPageSize >= parsed.Total;
            if (reachedEnd || page + 1 == MaxCommentPages)
            {
                await Finish(steamId, WebCaptureKinds.Comments, sweep, reachedEnd ? null : oldest, items, cancellationToken).ConfigureAwait(false);
                return (items, null);
            }
            await Task.Delay(Spacing, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        return (items, null);
    }

    /// <summary>
    /// 一条都没看到时不判断消失：资料设为私密或登录失效时页面也是空的，
    /// 这时把全部内容标成已删除是错的。代价是用户真的删光时不会标记。
    /// </summary>
    private async Task Finish(string steamId, string kind, long sweep, long? notBefore, int seen, CancellationToken cancellationToken)
    {
        if (seen > 0) await archive.MarkMissingAsync(steamId, kind, sweep, notBefore, cancellationToken).ConfigureAwait(false);
        await store.PruneAsync(steamId, kind, cancellationToken).ConfigureAwait(false);
    }

    private static string Label(WebPageState state) => state == WebPageState.Ok ? "ok" : "layout";
}
