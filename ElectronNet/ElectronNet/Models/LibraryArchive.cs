namespace ElectronNet.Models;

/// <summary>
/// 游戏入库记录：PackageId 为 Steam 许可证 package（时间 = 许可证创建时间）；
/// PackageId = 0 表示本软件首次在游戏库中观测到该应用的时间（无许可证数据时的回退）。
/// 无应用的 package 以 AppId = 0 占位，标记为已解析避免重复请求 PICS。
/// </summary>
public class LibraryAcquisition
{
    public int Id { get; init; }

    public string AccountName { get; init; } = string.Empty;

    public long AppId { get; init; }

    public long PackageId { get; init; }

    public long AcquiredAt { get; set; }
}

/// <summary>
/// 用户录入的游戏档案信息（状态 / 通关日期 / 评分 / 购买价格 / 备注）
/// </summary>
public class LibraryGameMeta
{
    public int Id { get; init; }

    public string AccountName { get; init; } = string.Empty;

    public long AppId { get; init; }

    public string? Status { get; set; }

    public long? CompletedAt { get; set; }

    public int? Rating { get; set; }

    public double? Price { get; set; }

    public string? Currency { get; set; }

    public string? Note { get; set; }

    public long UpdatedAt { get; set; }
}
