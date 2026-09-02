using System.Reflection;
using System.Reflection.Emit;
using ElectronNet;
using ElectronNet.Services;
using FluentAssertions;
using SteamStat.Core.Features.Friends;
using SteamStat.Core.Features.Library;
using SteamStat.Core.Features.Login;
using Microsoft.Extensions.Logging;
using SteamStat.Core.Events;
using SteamStat.Core.Features;
using SteamStat.Core.Platform;
using SteamStat.Core.Sessions;

namespace SteamStat.Architecture.Tests;

[TestFixture]
public sealed class M5BoundaryTests
{
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    [Test]
    public void Core_DoesNotReferenceWindowsOrElectronApis()
    {
        var core = typeof(ISecretStore).Assembly;
        core.GetReferencedAssemblies().Select(reference => reference.Name).Should()
            .NotContain(name => name == "ElectronNET.API"
                                || name == "System.ServiceProcess.ServiceController"
                                || name == "System.Security.Cryptography.ProtectedData");

        var featureSurface = new[]
        {
            typeof(IAppNameResolver), typeof(IAppMetadataWriter), typeof(ILanguageProvider),
            typeof(IRichPresenceResolver), typeof(IFriendStatusRecorder), typeof(ISteamLoginTokenStore)
        };
        featureSurface.Should().OnlyContain(type => type.Assembly == core && type.IsInterface);

        new[]
        {
            typeof(SteamLoginService), typeof(SteamLibraryService), typeof(SteamOwnedGame),
            typeof(SteamFriendsService), typeof(SteamFriendData), typeof(SteamFriendInfo),
            typeof(SteamRichPresenceResolver), typeof(SteamRichPresenceHandler),
            typeof(PersonaStateRichPresenceHandler), typeof(SteamLevelsHandler)
        }.Should().OnlyContain(type => type.Assembly == core,
            "experimental feature business implementations and public feature models belong to Core");
        featureSurface.SelectMany(GetPublicSurfaceTypes).Select(type => type.Namespace ?? string.Empty).Should()
            .NotContain(ns => ns.StartsWith("ElectronNet", StringComparison.Ordinal)
                              || ns.Contains("Windows", StringComparison.OrdinalIgnoreCase)
                              || ns.Contains("Persistence", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void SessionBoundary_DoesNotExposeCancellationTokenSource()
    {
        new[] { typeof(ISteamSessionAccessor), typeof(ISteamSession) }
            .SelectMany(GetPublicSurfaceTypes)
            .Should().NotContain(typeof(CancellationTokenSource));
    }

    [Test]
    public void FriendsAndLibrary_ConstructorsMatchGuideDependencySetsExactly()
    {
        ConstructorParameterTypes<SteamFriendsService>().Should().Equal(
            typeof(ISteamSessionAccessor),
            typeof(IAppNameResolver),
            typeof(IRichPresenceResolver),
            typeof(IFriendStatusRecorder),
            typeof(IEventBus),
            typeof(TimeProvider),
            typeof(ILogger<SteamFriendsService>));

        ConstructorParameterTypes<SteamLibraryService>().Should().Equal(
            typeof(ISteamSessionAccessor),
            typeof(IAppNameResolver),
            typeof(IAppMetadataWriter),
            typeof(ILanguageProvider),
            typeof(IHttpClientFactory),
            typeof(TimeProvider),
            typeof(ILogger<SteamLibraryService>));

        ConstructorParameterTypes<SteamLoginService>().Should().Equal(
            typeof(IEventBus),
            typeof(ISteamLoginTokenStore),
            typeof(ISecretStore),
            typeof(TimeProvider),
            typeof(ILogger<SteamLoginService>));
    }

    [Test]
    public void FeatureImplementations_AreInstanceOwnedAndDisposableWhereTheyOwnState()
    {
        typeof(SteamAppMetadataService).Should().Implement<IAppNameResolver>().And.Implement<IAppMetadataWriter>().And.Implement<IDisposable>();
        typeof(SteamRichPresenceResolver).Should().Implement<IRichPresenceResolver>().And.Implement<IDisposable>();
        typeof(FriendStatusRecordService).Should().Implement<IFriendStatusRecorder>().And.Implement<IDisposable>();
        typeof(SteamLanguageProvider).Should().Implement<ILanguageProvider>();

        foreach (var type in new[] { typeof(SteamAppMetadataService), typeof(SteamRichPresenceResolver), typeof(FriendStatusRecordService) })
        {
            type.IsAbstract.Should().BeFalse();
            type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => !field.IsLiteral).Should().BeEmpty($"{type.Name} cache/lifecycle state must be instance-owned");
        }

        ConstructorParameterTypes<FriendStatusRecordService>().Should().Contain(typeof(TimeProvider));
    }

    [Test]
    public void LoginLifecycle_TypesOwnCancellationSubscriptionsAndBoundedStopContract()
    {
        var nested = typeof(SteamLoginService).GetNestedTypes(BindingFlags.NonPublic);
        var authenticator = nested.Single(type => type.Name == "IpcAuthenticator");
        authenticator.Should().Implement<IDisposable>();
        authenticator.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Should().Contain(field => field.FieldType == typeof(CancellationTokenSource));

        var session = nested.Single(type => type.Name == "SteamSession");
        session.Should().Implement<IAsyncDisposable>();
        session.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Should().Contain(field => typeof(IEnumerable<IDisposable>).IsAssignableFrom(field.FieldType));
        session.GetMethod("StopAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetParameters().Select(parameter => parameter.ParameterType).Should().Equal(typeof(CancellationToken));

        typeof(SteamLoginService).GetMethod("InstallSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            .Should().NotBeNull("same-account replacement has one explicit installation path");
    }

    [Test]
    public void DisconnectedCallback_UsesIdentityRemovalBeforePublishingEnded()
    {
        var candidate = AllMethods(typeof(SteamLoginService))
            .Select(method => (Method: method, Members: ReferencedMembers(method).ToList()))
            .Single(item => item.Members.OfType<ConstructorInfo>().Any(member => member.DeclaringType == typeof(SteamSessionEnded))
                            && item.Members.Any(member => member.Name == "Remove"
                                && member.DeclaringType?.IsGenericType == true
                                && member.DeclaringType.GetGenericTypeDefinition() == typeof(ICollection<>)));

        var removeIndex = candidate.Members.FindIndex(member => member.Name == "Remove"
            && member.DeclaringType?.IsGenericType == true
            && member.DeclaringType.GetGenericTypeDefinition() == typeof(ICollection<>));
        var endedIndex = candidate.Members.FindIndex(member => member is ConstructorInfo constructor
            && constructor.DeclaringType == typeof(SteamSessionEnded));
        removeIndex.Should().BeGreaterThanOrEqualTo(0);
        endedIndex.Should().BeGreaterThan(removeIndex, "an old or explicitly logged-out session must not publish an ended event");
    }

    [Test]
    public void ReconnectState_AccessesAreSynchronized()
    {
        var reconnectState = typeof(SteamLoginService).GetNestedTypes(BindingFlags.NonPublic)
            .Single(type => type.Name == "ReconnectState");
        reconnectState.IsNestedPrivate.Should().BeTrue("reconnect state must not escape the login manager");

        var accessors = AllMethods(typeof(SteamLoginService))
            .Select(method => (Method: method, Members: ReferencedMembers(method).ToList()))
            .Where(item => item.Members.OfType<FieldInfo>().Any(field => field.DeclaringType == reconnectState))
            .Where(item => !item.Members.OfType<ConstructorInfo>().Any(constructor => constructor.DeclaringType == reconnectState))
            .ToList();
        accessors.Should().NotBeEmpty();
        foreach (var accessor in accessors)
        {
            accessor.Members.OfType<MethodBase>().Should().Contain(method =>
                method.DeclaringType == typeof(Monitor) && method.Name == nameof(Monitor.Enter),
                $"{accessor.Method.DeclaringType?.Name}.{accessor.Method.Name} touches reconnect state");
        }
    }

    [Test]
    public void WindowsPlatform_DoesNotReferenceHostOrFeatureImplementations()
    {
        typeof(Microsoft.Extensions.DependencyInjection.SteamStatWindowsServiceCollectionExtensions).Assembly
            .GetReferencedAssemblies().Select(reference => reference.Name).Should()
            .NotContain(name => name != null && name.StartsWith("Electron", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void RetiredStaticFacades_AreNotCompiledByHost()
    {
        var compiledTypes = typeof(AppDbContext).Assembly.GetTypes().Select(type => type.FullName).ToHashSet();
        compiledTypes.Should().NotContain(new[]
        {
            "ElectronNet.Services.LocalRegService", "ElectronNet.Services.LocalProcessService",
            "ElectronNet.Services.TokenProtectionService", "ElectronNet.Helpers.HttpClientProvider",
            "ElectronNet.Services.SettingService"
        });
    }

    private static Type[] ConstructorParameterTypes<T>()
        => typeof(T).GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single()
            .GetParameters().Select(parameter => parameter.ParameterType).ToArray();

    private static IEnumerable<Type> GetPublicSurfaceTypes(Type type)
    {
        yield return type;
        foreach (var property in type.GetProperties()) yield return property.PropertyType;
        foreach (var method in type.GetMethods())
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters()) yield return parameter.ParameterType;
        }
    }

    private static IEnumerable<MethodInfo> AllMethods(Type root)
    {
        foreach (var method in root.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            yield return method;
        foreach (var nested in root.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var method in AllMethods(nested)) yield return method;
    }

    private static IEnumerable<MemberInfo> ReferencedMembers(MethodInfo method)
    {
        var body = method.GetMethodBody();
        var bytes = body?.GetILAsByteArray();
        if (bytes == null) yield break;

        for (var index = 0; index < bytes.Length;)
        {
            var first = bytes[index++];
            var value = first == 0xfe ? (short)(0xfe00 | bytes[index++]) : first;
            var opCode = OpCodesByValue[value];
            var operandIndex = index;
            index += OperandSize(opCode.OperandType, bytes, operandIndex);
            if (opCode.OperandType is not (OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok)) continue;

            var token = BitConverter.ToInt32(bytes, operandIndex);
            MemberInfo? member = null;
            try
            {
                member = method.Module.ResolveMember(
                    token,
                    method.DeclaringType?.GetGenericArguments(),
                    method.GetGenericArguments());
            }
            catch (ArgumentException)
            {
            }
            if (member != null) yield return member;
        }
    }

    private static int OperandSize(OperandType operandType, byte[] bytes, int index) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod
            or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + BitConverter.ToInt32(bytes, index) * 4,
        _ => throw new ArgumentOutOfRangeException(nameof(operandType), operandType, null)
    };
}
