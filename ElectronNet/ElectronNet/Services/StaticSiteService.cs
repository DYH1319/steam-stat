using System.Text;
using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;
using SteamStat.Core.Memoir;

namespace ElectronNet.Services;

/// <summary>
/// 自包含静态站：预览和导出。页面由 Core 生成（打码在生成数据时就做完），这里只负责选位置和写文件。
/// 文件夹格式带 .nojekyll 和 README，直接推到 GitHub Pages 就是公开主页。
/// </summary>
internal sealed class StaticSiteService(
    MemoirService memoir,
    IDbContextFactory<AppDbContext> dbContextFactory,
    IMainWindowAccessor mainWindowAccessor,
    RevealRegistry revealRegistry,
    IShellLauncher shell,
    AppEnvironment environment,
    TimeProvider timeProvider,
    ILogger<StaticSiteService> logger)
{
    public async Task<StaticSitePreviewDto> PreviewAsync(StaticSiteOptionsDto options, CancellationToken cancellationToken = default)
    {
        var site = await BuildAsync(options, cancellationToken).ConfigureAwait(false);
        return new StaticSitePreviewDto { Html = site.Html, Bytes = Encoding.UTF8.GetByteCount(site.Html), Games = site.Games, Warnings = site.Warnings };
    }

    public async Task<StaticSiteExportResultDto> ExportAsync(StaticSiteExportRequest request, CancellationToken cancellationToken = default)
    {
        var site = await BuildAsync(request.Options, cancellationToken).ConfigureAwait(false);
        var stamp = timeProvider.GetLocalNow().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var target = await PickAsync(request.Format, $"steam-stat-site-{stamp}").ConfigureAwait(false);
        if (target == null) return new StaticSiteExportResultDto { Status = "cancelled" };
        try
        {
            if (request.Format == "folder")
            {
                Directory.CreateDirectory(target);
                await File.WriteAllTextAsync(Path.Combine(target, "index.html"), site.Html, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(target, "site.json"), site.Json, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(target, ".nojekyll"), string.Empty, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(target, "README.md"), Readme(request.Options.Language), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(target, site.Html, cancellationToken).ConfigureAwait(false);
            }
            return new StaticSiteExportResultDto { Status = "saved", ExportId = revealRegistry.Register(target) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to export the static site");
            return new StaticSiteExportResultDto { Status = "failed" };
        }
    }

    public async Task<bool> RevealAsync(string exportId)
    {
        var path = revealRegistry.Resolve(exportId);
        if (path == null || (!File.Exists(path) && !Directory.Exists(path))) return false;
        return await shell.ShowItemInFolderAsync(path).ConfigureAwait(false);
    }

    internal async Task<StaticSiteResult> BuildAsync(StaticSiteOptionsDto options, CancellationToken cancellationToken)
    {
        var loaded = await memoir.LoadAsync(options.SteamIds, cancellationToken).ConfigureAwait(false);
        var sections = options.Sections.Where(StaticSiteBuilder.AllSections.Contains).ToHashSet(StringComparer.Ordinal);
        var settings = new StaticSiteOptions(
            options.Year,
            MemoirJournalService.UserText(options.Title, 80),
            options.Identity,
            MemoirJournalService.UserText(options.Alias, 40),
            sections,
            options.GameNames == "mask",
            options.ExcludedAppIds.ToHashSet(),
            options.Hours == "rounded",
            options.DatePrecision == "month",
            options.Covers,
            options.LifeEvents,
            options.Credit,
            options.Language,
            options.Theme,
            options.Accent);
        var persona = options.Identity == "real" ? await PersonaAsync(loaded, cancellationToken).ConfigureAwait(false) : null;
        return StaticSiteBuilder.Build(loaded.Archive, loaded.Timelines, loaded.Months, loaded.Chapters, settings, persona);
    }

    /// <summary>真名用记录最多的那个账号的昵称。</summary>
    private async Task<string?> PersonaAsync(MemoirService.Loaded loaded, CancellationToken cancellationToken)
    {
        var steamId = loaded.Archive.Insight.Sessions.GroupBy(session => session.SteamId).MaxBy(group => group.Count())?.Key;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var users = await db.SteamUserTable.AsNoTracking().Select(user => new { user.SteamId, user.PersonaName }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return users.FirstOrDefault(user => user.SteamId == steamId)?.PersonaName ?? users.FirstOrDefault()?.PersonaName;
    }

    private async Task<string?> PickAsync(string format, string name)
    {
        if (environment.IsDevelopment && Environment.GetEnvironmentVariable("STEAM_STAT_E2E_DIALOG_PATH") is { Length: > 0 } test)
            return Path.Combine(Path.GetDirectoryName(test) ?? test, format == "folder" ? name : $"{name}.html");
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Availability != MainWindowAvailability.Available || snapshot.Window == null) return null;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        try
        {
            if (format == "folder")
            {
                var folders = await Electron.Dialog.ShowOpenDialogAsync(snapshot.Window, new OpenDialogOptions
                {
                    DefaultPath = documents,
                    Properties = [OpenDialogProperty.openDirectory, OpenDialogProperty.createDirectory]
                }).ConfigureAwait(false);
                if (folders is not { Length: > 0 }) return null;
                var target = Path.Combine(folders[0], name);
                for (var index = 2; Directory.Exists(target) || File.Exists(target); index++) target = Path.Combine(folders[0], $"{name}-{index}");
                return target;
            }
            var file = $"{name}.html";
            var path = await Electron.Dialog.ShowSaveDialogAsync(snapshot.Window, new SaveDialogOptions
            {
                DefaultPath = string.IsNullOrEmpty(documents) ? file : Path.Combine(documents, file),
                Filters = [new FileFilter { Name = "HTML", Extensions = ["html"] }]
            }).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Path.HasExtension(path) ? path : $"{path}.html";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to show the static site location dialog");
            return null;
        }
    }

    internal static string Readme(string language) => language == "en"
        ? """
          # My gaming archive

          A self-contained page generated by Steam Stat. It has no scripts and loads nothing except optional Steam cover images.

          ## Publish on GitHub Pages

          1. Create a repository and push this folder to it (`index.html` at the root).
          2. In the repository settings, open Pages and choose "Deploy from a branch" with the root folder.
          3. The page is published at `https://<user>.github.io/<repository>/`.

          `site.json` holds the same data as the page, already redacted. Friend data is never included.
          """
        : """
          # 我的游戏档案

          由 Steam Stat 生成的自包含页面。没有脚本，除了可选的 Steam 封面，不加载任何外部资源。

          ## 发布到 GitHub Pages

          1. 新建一个仓库，把这个文件夹推上去（`index.html` 在根目录）。
          2. 在仓库设置里打开 Pages，选择「Deploy from a branch」，目录选根目录。
          3. 页面地址是 `https://<用户名>.github.io/<仓库名>/`。

          `site.json` 是页面上的数据，已经按打码规则处理过。好友数据永远不会出现在这里。
          """;
}
