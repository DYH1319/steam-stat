using ElectronNET.API;
using ElectronNET.API.Entities;
using ElectronNet.Hosting;
using ElectronNet.Infrastructure;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Environment;

namespace ElectronNet.Services;

/// <summary>把渲染层画好的 SVG 海报存到用户选的位置。保存位置由 Host 弹对话框，渲染层只拿到 exportId。</summary>
internal sealed class PosterExportService(
    IMainWindowAccessor mainWindowAccessor,
    RevealRegistry revealRegistry,
    AppEnvironment environment,
    ILogger<PosterExportService> logger)
{
    public async Task<PosterSaveResultDto> SaveAsync(PosterSaveRequest request, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(request.FileName);
        if (name.Length == 0 || !name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || !request.Svg.TrimStart().StartsWith("<svg", StringComparison.Ordinal))
            return new PosterSaveResultDto { Status = "failed" };
        var target = await PickAsync(name).ConfigureAwait(false);
        if (target == null) return new PosterSaveResultDto { Status = "cancelled" };
        try
        {
            await File.WriteAllTextAsync(target, request.Svg, cancellationToken).ConfigureAwait(false);
            return new PosterSaveResultDto { Status = "saved", ExportId = revealRegistry.Register(target) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to save a poster");
            return new PosterSaveResultDto { Status = "failed" };
        }
    }

    private async Task<string?> PickAsync(string name)
    {
        if (environment.IsDevelopment && Environment.GetEnvironmentVariable("STEAM_STAT_E2E_DIALOG_PATH") is { Length: > 0 } test)
            return Path.Combine(Path.GetDirectoryName(test) ?? test, name);
        var snapshot = await mainWindowAccessor.GetSnapshotAsync().ConfigureAwait(false);
        if (snapshot.Window == null) return null;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var path = await Electron.Dialog.ShowSaveDialogAsync(snapshot.Window, new SaveDialogOptions
        {
            DefaultPath = string.IsNullOrEmpty(documents) ? name : Path.Combine(documents, name),
            Filters = [new FileFilter { Name = "SVG", Extensions = ["svg"] }]
        }).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(path) ? null : Path.HasExtension(path) ? path : $"{path}.svg";
    }
}
