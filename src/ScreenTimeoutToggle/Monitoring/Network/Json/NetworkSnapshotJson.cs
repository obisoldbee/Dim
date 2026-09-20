using System.Text.Json;
using System.Text.Json.Serialization;
using OBDim.Monitoring.Network.Models;

namespace OBDim.Monitoring.Network.Json;

/// <summary>
/// (De)serialization between the C# snapshot model and the frozen contract v1 wire form.
/// Enums serialize as their exact contract strings via explicit bidirectional maps —
/// no naming-policy inference that could silently drift from the schema enums.
/// Null unknowns are always written as JSON null (DefaultIgnoreCondition is Never).
/// </summary>
public static class NetworkSnapshotJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new ContractEnumConverter<SnapshotOrigin>(new Dictionary<SnapshotOrigin, string>
        {
            [SnapshotOrigin.Host] = "host",
            [SnapshotOrigin.Fixture] = "fixture",
        }));
        options.Converters.Add(new ContractEnumConverter<NetworkCoverage>(new Dictionary<NetworkCoverage, string>
        {
            [NetworkCoverage.Active] = "active",
            [NetworkCoverage.Partial] = "partial",
            [NetworkCoverage.Stopped] = "stopped",
            [NetworkCoverage.Starting] = "starting",
            [NetworkCoverage.Denied] = "denied",
            [NetworkCoverage.Disconnected] = "disconnected",
        }));
        options.Converters.Add(new ContractEnumConverter<InterfaceKind>(new Dictionary<InterfaceKind, string>
        {
            [InterfaceKind.Physical] = "physical",
            [InterfaceKind.Tunnel] = "tunnel",
            [InterfaceKind.Loopback] = "loopback",
            [InterfaceKind.Unknown] = "unknown",
        }));
        options.Converters.Add(new ContractEnumConverter<ConnectionDirection>(new Dictionary<ConnectionDirection, string>
        {
            [ConnectionDirection.Up] = "up",
            [ConnectionDirection.Down] = "down",
        }));
        options.Converters.Add(new ContractEnumConverter<ConnectionInitiator>(new Dictionary<ConnectionInitiator, string>
        {
            [ConnectionInitiator.Local] = "local",
            [ConnectionInitiator.Remote] = "remote",
            [ConnectionInitiator.Unknown] = "unknown",
        }));
        options.Converters.Add(new ContractEnumConverter<DomainSource>(new Dictionary<DomainSource, string>
        {
            [DomainSource.System] = "system",
            [DomainSource.Proxy] = "proxy",
            [DomainSource.Unknown] = "unknown",
        }));
        options.Converters.Add(new ContractEnumConverter<NetworkProtocol>(new Dictionary<NetworkProtocol, string>
        {
            [NetworkProtocol.Tcp] = "TCP",
            [NetworkProtocol.Udp] = "UDP",
        }));
        return options;
    }

    public static string Serialize(NetworkSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);

    public static byte[] SerializeToUtf8Bytes(NetworkSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(snapshot, Options);

    public static NetworkSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<NetworkSnapshot>(json, Options)
        ?? throw new JsonException("snapshot deserialized to null");
}

/// <summary>An enum converter driven by an explicit value-to-string map, both directions.</summary>
internal sealed class ContractEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private readonly IReadOnlyDictionary<T, string> _toText;
    private readonly IReadOnlyDictionary<string, T> _fromText;

    public ContractEnumConverter(IReadOnlyDictionary<T, string> map)
    {
        _toText = map;
        _fromText = map.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is not null && _fromText.TryGetValue(text, out var value)) return value;
        throw new JsonException($"'{text}' is not a valid {typeof(T).Name} contract value");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!_toText.TryGetValue(value, out var text))
            throw new JsonException($"{value} has no contract string for {typeof(T).Name}");
        writer.WriteStringValue(text);
    }
}
