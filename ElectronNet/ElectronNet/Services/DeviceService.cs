using ElectronNet.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ElectronNet.Services;

/// <summary>
/// 管理采集设备记录；当前设备在首次启动时创建并缓存。
/// </summary>
public sealed class DeviceService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<DeviceService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? CurrentDeviceId { get; private set; }

    /// <summary>
    /// 确保当前设备行存在，返回其 ID（Guid "N" 格式）。
    /// </summary>
    public async Task<string> EnsureCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentDeviceId != null) return CurrentDeviceId;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (CurrentDeviceId != null) return CurrentDeviceId;

            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.DeviceTable.FirstOrDefaultAsync(d => d.IsCurrent, cancellationToken);
            var machineName = Environment.MachineName;
            if (device == null)
            {
                device = new Device
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = machineName,
                    IsCurrent = true,
                    CreatedAt = (int)timeProvider.GetUtcNow().ToUnixTimeSeconds()
                };
                db.DeviceTable.Add(device);
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Created current device {DeviceId} ({DeviceName})", device.Id, device.Name);
            }
            else if (device.Name != machineName)
            {
                device.Name = machineName;
                await db.SaveChangesAsync(cancellationToken);
            }

            CurrentDeviceId = device.Id;
            return device.Id;
        }
        finally
        {
            _gate.Release();
        }
    }
}
