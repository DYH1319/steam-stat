using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamStat.Contracts.Ipc;

namespace GenerateIpcContracts;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 1 || args[0] is not ("--write" or "--check"))
        {
            Console.Error.WriteLine("Usage: GenerateIpcContracts --write|--check [--core-root <dir>] [--frontend-types <file>]");
            return 2;
        }

        string? coreRoot = null;
        string? frontendTypes = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--core-root" when i + 1 < args.Length:
                    coreRoot = args[++i];
                    break;
                case "--frontend-types" when i + 1 < args.Length:
                    frontendTypes = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
                    return 2;
            }
        }

        try
        {
            var repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
            return args[0] == "--write"
                ? IpcContractGenerator.Write(repositoryRoot, coreRoot, frontendTypes)
                : IpcContractGenerator.Check(repositoryRoot, coreRoot, frontendTypes);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        // 纯后端仓的根标记是 SteamStat.slnx；UI 仓的根标记是 package.json
        var directory = new DirectoryInfo(startDirectory);
        while (directory != null
               && !File.Exists(Path.Combine(directory.FullName, "SteamStat.slnx"))
               && !File.Exists(Path.Combine(directory.FullName, "package.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

public static class IpcContractGenerator
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly NullabilityInfoContext Nullability = new();
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IReadOnlyDictionary<string, string> Generate(string repositoryRoot, string? coreRoot = null, string? frontendTypesPath = null)
    {
        coreRoot = Path.GetFullPath(coreRoot ?? ResolveCoreRoot(repositoryRoot));
        var endpoints = ValidateAndSortEndpoints();
        var types = CollectContractTypes(endpoints);
        var outputs = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [Path.Combine(coreRoot, "ElectronNet", "ElectronNet", "Resources", "preload.mjs")] = GeneratePreload(endpoints),
            [Path.Combine(coreRoot, "ElectronNet", "ElectronNet.Tests", "Snapshots", "ipc-contracts.snapshot.json")] = GenerateSnapshot(endpoints, types)
        };
        // 前端类型文件：显式 --frontend-types 指定，或仓库内 apps/steam-stat 存在时走默认路径；
        // 纯后端仓库（无前端目录）自动跳过该输出
        frontendTypesPath ??= DefaultFrontendTypesPath(repositoryRoot);
        if (frontendTypesPath != null)
            outputs[Path.GetFullPath(frontendTypesPath)] = GenerateTypeScript(endpoints, types);
        return outputs;
    }

    private static string ResolveCoreRoot(string repositoryRoot)
    {
        // 后端目录在本仓库内（公开仓）或 vendor/core submodule 内（私有 UI 仓）
        if (Directory.Exists(Path.Combine(repositoryRoot, "ElectronNet")))
            return repositoryRoot;
        var vendored = Path.Combine(repositoryRoot, "vendor", "core");
        if (Directory.Exists(Path.Combine(vendored, "ElectronNet")))
            return vendored;
        throw new DirectoryNotFoundException($"Cannot locate ElectronNet under '{repositoryRoot}' or its vendor/core submodule; pass --core-root.");
    }

    private static string? DefaultFrontendTypesPath(string repositoryRoot)
        => Directory.Exists(Path.Combine(repositoryRoot, "apps", "steam-stat"))
            ? Path.Combine(repositoryRoot, "apps", "steam-stat", "src", "types", "ipc.d.ts")
            : null;

    public static int Write(string repositoryRoot, string? coreRoot = null, string? frontendTypesPath = null)
    {
        foreach (var output in Generate(repositoryRoot, coreRoot, frontendTypesPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output.Key)!);
            File.WriteAllText(output.Key, output.Value, Utf8WithoutBom);
            Console.WriteLine($"Generated {Path.GetRelativePath(repositoryRoot, output.Key).Replace('\\', '/')}");
        }
        return 0;
    }

    public static int Check(string repositoryRoot, string? coreRoot = null, string? frontendTypesPath = null)
    {
        var stale = Generate(repositoryRoot, coreRoot, frontendTypesPath)
            .Where(output => !File.Exists(output.Key) || File.ReadAllText(output.Key) != output.Value)
            .Select(output => Path.GetRelativePath(repositoryRoot, output.Key).Replace('\\', '/'))
            .ToArray();
        if (stale.Length == 0)
        {
            Console.WriteLine("IPC generated files are up to date.");
            return 0;
        }

        foreach (var file in stale) Console.Error.WriteLine($"Out of date: {file}");
        Console.Error.WriteLine("Run: dotnet run --project tools/GenerateIpcContracts -- --write");
        return 1;
    }

    private static IIpcEndpointDescriptor[] ValidateAndSortEndpoints()
    {
        var endpoints = IpcCatalog.All.OrderBy(endpoint => endpoint.ApiMethod, StringComparer.Ordinal).ToArray();
        var duplicateMethod = endpoints.GroupBy(endpoint => endpoint.ApiMethod, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateMethod != null) throw new InvalidOperationException($"Duplicate IPC API method: {duplicateMethod.Key}");

        var duplicateChannel = endpoints.GroupBy(endpoint => (endpoint.Channel, endpoint.Direction))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateChannel != null)
            throw new InvalidOperationException($"Duplicate IPC channel/direction: {duplicateChannel.Key}");

        foreach (var endpoint in endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint.Channel) || string.IsNullOrWhiteSpace(endpoint.ApiMethod))
                throw new InvalidOperationException("IPC channel and API method must not be empty.");
            if (endpoint.Direction == IpcDirection.HostToRendererEvent && string.IsNullOrWhiteSpace(endpoint.RemoveApiMethod))
                throw new InvalidOperationException($"Event {endpoint.ApiMethod} must define a remove-listener API method.");
        }
        return endpoints;
    }

    private static string GeneratePreload(IEnumerable<IIpcEndpointDescriptor> endpoints)
    {
        var lines = new List<string>
        {
            "// <auto-generated />",
            "\"use strict\";",
            "",
            "const { contextBridge, ipcRenderer } = require(\"electron\");",
            "",
            "contextBridge.exposeInMainWorld(\"electron\", {"
        };
        foreach (var endpoint in endpoints)
        {
            var hasRequest = endpoint.RequestType != null;
            switch (endpoint.Direction)
            {
                case IpcDirection.Invoke:
                    lines.Add($"  {endpoint.ApiMethod}: ({(hasRequest ? "param" : string.Empty)}) => ipcRenderer.invoke(\"{endpoint.Channel}\"{(hasRequest ? ", param" : string.Empty)}),");
                    break;
                case IpcDirection.Send:
                    lines.Add($"  {endpoint.ApiMethod}: ({(hasRequest ? RequestParameterName(endpoint.RequestType!) : string.Empty)}) => ipcRenderer.send(\"{endpoint.Channel}\"{(hasRequest ? $", {RequestParameterName(endpoint.RequestType!)}" : string.Empty)}),");
                    break;
                case IpcDirection.HostToRendererEvent:
                    var callback = endpoint.ResponseType == null ? "callback()" : "callback(data)";
                    lines.Add($"  {endpoint.ApiMethod}: (callback) => ipcRenderer.on(\"{endpoint.Channel}\", (_event, data) => {callback}),");
                    lines.Add($"  {endpoint.RemoveApiMethod}: () => ipcRenderer.removeAllListeners(\"{endpoint.Channel}\"),");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        lines.Add("});");
        return string.Join('\n', lines) + "\n";
    }

    private static string GenerateTypeScript(
        IEnumerable<IIpcEndpointDescriptor> endpoints,
        IReadOnlyCollection<Type> types)
    {
        var lines = new List<string>
        {
            "// <auto-generated />",
            "interface Window {",
            "  electron: ElectronAPI",
            "}",
            "",
            "interface ElectronAPI {"
        };
        foreach (var endpoint in endpoints)
        {
            var request = endpoint.RequestType == null
                ? string.Empty
                : $"param{(endpoint.AllowsEmptyRequest ? "?" : string.Empty)}: {FormatType(endpoint.RequestType)}";
            switch (endpoint.Direction)
            {
                case IpcDirection.Invoke:
                    var response = FormatType(endpoint.ResponseType!);
                    if (endpoint.ResponseNullable) response += " | null";
                    lines.Add($"  {endpoint.ApiMethod}: ({request}) => Promise<{response}>");
                    break;
                case IpcDirection.Send:
                    lines.Add($"  {endpoint.ApiMethod}: ({request}) => void");
                    break;
                case IpcDirection.HostToRendererEvent:
                    var callback = endpoint.ResponseType == null
                        ? "callback: () => void"
                        : $"callback: (data: {FormatType(endpoint.ResponseType)}) => void";
                    lines.Add($"  {endpoint.ApiMethod}: ({callback}) => void");
                    lines.Add($"  {endpoint.RemoveApiMethod}: () => void");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        lines.Add("}");

        foreach (var type in types.OrderBy(GetTypeScriptName, StringComparer.Ordinal))
        {
            lines.Add(string.Empty);
            lines.Add($"interface {GetTypeScriptName(type)} {{");
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .OrderBy(property => CamelCase(property.Name), StringComparer.Ordinal))
            {
                var optional = property.IsDefined(typeof(IpcOptionalAttribute));
                lines.Add($"  {CamelCase(property.Name)}{(optional ? "?" : string.Empty)}: {FormatPropertyType(property)}");
            }
            lines.Add("}");
        }
        return string.Join('\n', lines) + "\n";
    }

    private static string GenerateSnapshot(
        IEnumerable<IIpcEndpointDescriptor> endpoints,
        IReadOnlyCollection<Type> types)
    {
        var endpointRows = endpoints.Select(endpoint => new
        {
            apiMethod = endpoint.ApiMethod,
            channel = endpoint.Channel,
            direction = CamelCase(endpoint.Direction.ToString()),
            request = endpoint.RequestType == null ? null : FormatType(endpoint.RequestType),
            response = endpoint.ResponseType == null
                ? null
                : FormatType(endpoint.ResponseType) + (endpoint.ResponseNullable ? " | null" : string.Empty),
            allowsEmptyRequest = endpoint.AllowsEmptyRequest,
            removeApiMethod = endpoint.RemoveApiMethod
        }).ToArray();
        var typeRows = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var type in types.OrderBy(GetTypeScriptName, StringComparer.Ordinal))
        {
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = CamelCase(property.Name) + (property.IsDefined(typeof(IpcOptionalAttribute)) ? "?" : string.Empty);
                properties[name] = FormatPropertyType(property);
            }
            typeRows[GetTypeScriptName(type)] = properties;
        }
        var json = JsonSerializer.Serialize(new { endpoints = endpointRows, types = typeRows }, SnapshotJsonOptions);
        return json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static IReadOnlyCollection<Type> CollectContractTypes(IEnumerable<IIpcEndpointDescriptor> endpoints)
    {
        var result = new HashSet<Type>();
        foreach (var endpoint in endpoints)
        {
            CollectType(endpoint.RequestType, result);
            CollectType(endpoint.ResponseType, result);
        }
        return result;
    }

    private static void CollectType(Type? type, ISet<Type> result)
    {
        if (type == null) return;
        type = Nullable.GetUnderlyingType(type) ?? type;
        var typeUnion = type.GetCustomAttribute<IpcUnionAttribute>();
        if (typeUnion != null)
        {
            foreach (var unionType in typeUnion.Types) CollectType(unionType, result);
            return;
        }
        if (IsPrimitive(type)) return;
        if (TryGetDictionaryValue(type, out var value))
        {
            CollectType(value, result);
            return;
        }
        if (TryGetCollectionElement(type, out var element))
        {
            CollectType(element, result);
            return;
        }
        if (type == typeof(object)) return;
        if (!result.Add(type)) return;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var union = property.GetCustomAttribute<IpcUnionAttribute>();
            if (union != null)
                foreach (var unionType in union.Types) CollectType(unionType, result);
            else
                CollectType(property.PropertyType, result);
        }
    }

    private static string FormatPropertyType(PropertyInfo property)
    {
        var union = property.GetCustomAttribute<IpcUnionAttribute>();
        var formatted = union == null
            ? FormatType(property.PropertyType, property)
            : string.Join(" | ", union.Types.Select(type => FormatType(type)).Order(StringComparer.Ordinal));
        var nullable = Nullable.GetUnderlyingType(property.PropertyType) != null
                       || Nullability.Create(property).ReadState == NullabilityState.Nullable;
        return nullable ? formatted + " | null" : formatted;
    }

    private static string FormatType(Type type, PropertyInfo? property = null)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        var typeUnion = type.GetCustomAttribute<IpcUnionAttribute>();
        if (typeUnion != null)
            return string.Join(" | ", typeUnion.Types.Select(unionType => FormatType(unionType)).Order(StringComparer.Ordinal));
        var values = property?.GetCustomAttribute<IpcStringValuesAttribute>();
        if (values != null)
            return string.Join(" | ", values.Values.Select(value => $"'{value.Replace("'", "\\'")}'"));
        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid)) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(float) || type == typeof(double)
            || type == typeof(decimal)) return "number";
        if (type == typeof(long) || type == typeof(ulong))
        {
            if (property?.IsDefined(typeof(IpcNumberAttribute)) == true) return "number";
            throw new NotSupportedException($"64-bit property {property?.DeclaringType?.Name}.{property?.Name} must explicitly declare its wire representation.");
        }
        if (type.IsEnum)
            return string.Join(" | ", Enum.GetNames(type).Select(name => $"'{CamelCase(name)}'"));
        if (TryGetDictionaryValue(type, out var value)) return $"Record<string, {FormatType(value)}>";
        if (TryGetCollectionElement(type, out var element)) return $"{FormatType(element)}[]";
        if (type == typeof(object))
            throw new NotSupportedException("object requires an IpcUnion attribute; generating any is forbidden.");
        if (type.IsGenericType)
            throw new NotSupportedException($"Unsupported generic IPC type: {type}");
        if (type.Namespace != typeof(IpcCatalog).Namespace)
            throw new NotSupportedException($"IPC DTO type must belong to SteamStat.Contracts: {type}");
        return GetTypeScriptName(type);
    }

    private static bool IsPrimitive(Type type) => type == typeof(string) || type == typeof(char) || type == typeof(Guid)
        || type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short)
        || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(float)
        || type == typeof(double) || type == typeof(decimal) || type == typeof(long) || type == typeof(ulong)
        || type.IsEnum || type == typeof(IpcNoRequest) || type == typeof(IpcNoPayload);

    private static bool TryGetCollectionElement(Type type, out Type element)
    {
        if (type.IsArray)
        {
            element = type.GetElementType()!;
            return true;
        }
        var collection = type.GetInterfaces().Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() is var definition
                && (definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyCollection<>)
                    || definition == typeof(IReadOnlyList<>) || definition == typeof(ICollection<>)
                    || definition == typeof(IList<>) || definition == typeof(List<>)));
        element = collection?.GetGenericArguments()[0]!;
        return collection != null;
    }

    private static bool TryGetDictionaryValue(Type type, out Type value)
    {
        var dictionary = type.GetInterfaces().Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() is var definition
                && (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)
                    || definition == typeof(Dictionary<,>)));
        if (dictionary == null)
        {
            value = null!;
            return false;
        }
        var arguments = dictionary.GetGenericArguments();
        if (arguments[0] != typeof(string))
            throw new NotSupportedException($"IPC dictionaries require string keys: {type}");
        value = arguments[1];
        return true;
    }

    private static string GetTypeScriptName(Type type)
        => type.GetCustomAttribute<IpcTypeNameAttribute>()?.Name
           ?? (type.Name.EndsWith("Dto", StringComparison.Ordinal) ? type.Name[..^3] : type.Name);

    private static string RequestParameterName(Type type) => type == typeof(string) ? "value" : "param";

    private static string CamelCase(string value)
        => string.IsNullOrEmpty(value) || char.IsLower(value[0]) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
