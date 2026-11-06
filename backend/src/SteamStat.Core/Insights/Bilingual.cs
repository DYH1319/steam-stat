namespace SteamStat.Core.Insights;

/// <summary>
/// 一段两种语言都写好的文字。周刊生成后冻结，之后切换界面语言不能重新生成，所以两份都要存。
/// </summary>
public sealed record Bilingual(string Zh, string En);
