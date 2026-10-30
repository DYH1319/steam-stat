using System.Text.Json;
using System.Text.RegularExpressions;
using SteamKit2;
using SteamStat.Core.Features.Apps.Contracts;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal static partial class SteamAppArtworkMapper
{
    public static SteamAppArtwork? FromStore(uint appId, JsonElement data)
    {
        var capsule = Urls("capsule_imagev5", "capsule_image");
        var header = Urls("header_image");
        return capsule.Length + header.Length == 0 ? null : new SteamAppArtwork(capsule, header, [], []);

        string[] Urls(params string[] fields) => fields
            .Select(field => data.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.String
                ? ToUrl(appId, element.GetString()) : null)
            .Where(url => url != null).Cast<string>().Distinct().ToArray();
    }

    public static SteamAppArtwork Map(uint appId, string language, KeyValue? common)
    {
        var full = common?["library_assets_full"];
        return new SteamAppArtwork(
            Images(common?["small_capsule"]),
            Images(common?["header_image"]).Concat(Library("library_header")).Distinct().ToArray(),
            Library("library_capsule"),
            Library("library_hero"));

        string[] Library(string name) => Images(full?[name]?["image"])
            .Concat(Images(full?[name]?["image2x"])).Distinct().ToArray();

        string[] Images(KeyValue? node)
        {
            if (node == null) return [];
            var values = new[] { node[language].AsString(), node["english"].AsString(), node.AsString() };
            return values.Select(value => ToUrl(appId, value))
                .Where(url => url != null).Cast<string>().Distinct().ToArray();
        }
    }

    private static string? ToUrl(uint appId, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (AssetPath().IsMatch(path))
            return $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/{path}";
        if (Uri.TryCreate(path, UriKind.Absolute, out var url)
            && url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0 && url.IsDefaultPort
            && url.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase))
            return url.AbsoluteUri;
        return null;
    }

    [GeneratedRegex(@"^(?:[a-fA-F0-9]{40}/)?[a-zA-Z0-9_-]+\.(?:jpg|png|webp)$")]
    private static partial Regex AssetPath();
}
