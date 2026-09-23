using ProtoBuf;

namespace SteamStat.Core.Steam.Gateway.Internal;

internal static class ProtobufReshape
{
    public static T To<T>(IExtensible source) where T : IExtensible, new()
    {
        using var stream = new MemoryStream();
        Serializer.NonGeneric.Serialize(stream, source);
        stream.Position = 0;
        return Serializer.Deserialize<T>(stream);
    }
}
