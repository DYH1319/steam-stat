using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;

namespace ElectronNet.Infrastructure;

internal sealed class IpcRequestBinder(ILogger<IpcRequestBinder> logger)
{
    private static readonly NullabilityInfoContext Nullability = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal T Bind<T>(object? value, IIpcEndpointDescriptor endpoint)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        try
        {
            if (value == null)
            {
                if (!endpoint.AllowsEmptyRequest)
                    throw new IpcRequestBindingException("A request payload is required.");
                var empty = Activator.CreateInstance<T>();
                return empty is null ? default! : empty;
            }

            T? request;
            if (value is T typed)
                request = typed;
            else
                request = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions);
            if (request == null) throw new IpcRequestBindingException("The request payload could not be bound.");
            Validate(request, typeof(T), null);
            return request;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException
                                          or IpcRequestBindingException or TargetInvocationException)
        {
            logger.LogWarning(
                exception,
                "Rejected IPC request for {Endpoint} ({CorrelationId})",
                endpoint.ApiMethod,
                correlationId);
            throw new IpcRequestBindingException(
                $"Invalid request for endpoint '{endpoint.ApiMethod}' (correlation id: {correlationId}).",
                exception);
        }
    }

    private static void Validate(object value, Type declaredType, PropertyInfo? sourceProperty)
    {
        if (value is string text)
        {
            var maximum = sourceProperty?.GetCustomAttribute<IpcMaxLengthAttribute>()?.Length;
            if (maximum.HasValue && text.Length > maximum.Value)
                throw new IpcRequestBindingException($"{sourceProperty?.Name ?? "Value"} exceeds {maximum.Value} characters.");
            var allowed = sourceProperty?.GetCustomAttribute<IpcStringValuesAttribute>();
            if (allowed != null && !allowed.Values.Contains(text, StringComparer.Ordinal))
                throw new IpcRequestBindingException($"{sourceProperty!.Name} has an unsupported value.");
            return;
        }

        var numberRange = sourceProperty?.GetCustomAttribute<IpcRangeAttribute>();
        if (numberRange != null && value is IConvertible convertible)
        {
            var number = convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
            if (number < numberRange.Minimum || number > numberRange.Maximum)
                throw new IpcRequestBindingException($"{sourceProperty!.Name} is outside the allowed range.");
        }

        if (value is IEnumerable sequence and not string)
        {
            var count = 0;
            foreach (var item in sequence)
            {
                if (++count > 1000) throw new IpcRequestBindingException("A request collection exceeds 1000 items.");
                if (item != null) Validate(item, item.GetType(), sourceProperty);
            }
            return;
        }

        declaredType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (declaredType.IsPrimitive || declaredType.IsEnum || declaredType == typeof(decimal)) return;
        foreach (var property in declaredType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propertyValue = property.GetValue(value);
            if (propertyValue == null
                && !property.IsDefined(typeof(IpcOptionalAttribute))
                && Nullability.Create(property).ReadState == NullabilityState.NotNull)
                throw new IpcRequestBindingException($"{property.Name} is required.");
            if (propertyValue != null) Validate(propertyValue, property.PropertyType, property);
        }
    }
}

internal sealed class IpcRequestBindingException : ArgumentException
{
    internal IpcRequestBindingException(string message) : base(message)
    {
    }

    internal IpcRequestBindingException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
