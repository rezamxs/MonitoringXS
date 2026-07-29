using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Broker;

internal static class PrivilegedEtwBrokerProtocol
{
    public const uint Magic = 0x4253584D;
    public const ushort Version = 1;
    public const string PipeNamePrefix = "MonitoringXS.PrivilegedEtwBroker.v1";
    public const int HeaderSize = 16;
    public const int MaximumRequestBytes = 64 * 1024;
    public const int MaximumResponseBytes = 4 * 1024 * 1024;
    public const int MaximumProcesses = 2_048;
    public const int PipeBufferBytes = 64 * 1024;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);
}

internal enum BrokerCommand : ushort
{
    HelloRequest = 1,
    HelloResponse = 2,
    ReadPhysicalDiskRequest = 3,
    PhysicalDiskResponse = 4,
    ReadNetworkRequest = 5,
    NetworkResponse = 6,
    ErrorResponse = 7
}

internal enum BrokerErrorCode
{
    MalformedMessage,
    OversizedMessage,
    ProtocolMismatch,
    UnknownCommand,
    Unauthorized,
    ResourceExhausted,
    ServiceUnavailable
}

internal readonly record struct BrokerFrame(BrokerCommand Command, int RequestId, byte[] Payload);

internal sealed record BrokerHelloResponse(ushort Version, Guid ServiceInstanceId);

internal sealed record BrokerProcessRequest(ProcessInstanceId[] Processes);

internal sealed record BrokerErrorResponse(BrokerErrorCode Code);

internal sealed class BrokerProtocolException(BrokerErrorCode code, string message) : IOException(message)
{
    public BrokerErrorCode Code { get; } = code;
}

internal static class BrokerFrameCodec
{
    public static async ValueTask<BrokerFrame> ReadAsync(
        Stream stream,
        int maximumPayloadBytes,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[PrivilegedEtwBrokerProtocol.HeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != PrivilegedEtwBrokerProtocol.Magic)
        {
            throw new BrokerProtocolException(
                BrokerErrorCode.MalformedMessage,
                "The broker frame magic is invalid.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (version != PrivilegedEtwBrokerProtocol.Version)
        {
            throw new BrokerProtocolException(
                BrokerErrorCode.ProtocolMismatch,
                $"The broker protocol version is incompatible. Client/server expected {PrivilegedEtwBrokerProtocol.Version}; received {version}.");
            }

        BrokerCommand command = (BrokerCommand)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        int requestId = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        if (requestId <= 0 || payloadLength < 0)
        {
            throw new BrokerProtocolException(
                BrokerErrorCode.MalformedMessage,
                "The broker frame header is invalid.");
        }

        if (payloadLength > maximumPayloadBytes)
        {
            throw new BrokerProtocolException(
                BrokerErrorCode.OversizedMessage,
                "The broker frame exceeds the configured size limit.");
        }

        byte[] payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        return new BrokerFrame(command, requestId, payload);
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        BrokerCommand command,
        int requestId,
        ReadOnlyMemory<byte> payload,
        int maximumPayloadBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestId);

        if (payload.Length > maximumPayloadBytes)
        {
            throw new BrokerProtocolException(
                BrokerErrorCode.OversizedMessage,
                "The broker frame exceeds the configured size limit.");
        }

        byte[] header = new byte[PrivilegedEtwBrokerProtocol.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, PrivilegedEtwBrokerProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), PrivilegedEtwBrokerProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)command);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal static class BrokerJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Options)
                ?? throw new BrokerProtocolException(
                    BrokerErrorCode.MalformedMessage,
                    "The broker payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new BrokerProtocolException(
                BrokerErrorCode.MalformedMessage,
                "The broker payload is malformed.") { Source = exception.Source };
        }
    }
}
