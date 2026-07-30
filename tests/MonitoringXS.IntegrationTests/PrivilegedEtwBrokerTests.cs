using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Broker;
using MonitoringXS.Platform.Windows.Metrics;
using MonitoringXS.PrivilegedBroker;

namespace MonitoringXS.IntegrationTests;

public sealed class PrivilegedEtwBrokerTests
{
    [Fact]
    public void ServiceStatusProbeUsesCompleteNativeSignature()
    {
        var method = typeof(BrokerServiceProbe).GetMethod(
            "QueryServiceStatusEx",
            System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        System.Reflection.ParameterInfo[] parameters = method.GetParameters();
        Assert.Equal(5, parameters.Length);
        Assert.True(parameters[4].IsOut);
        Assert.Equal(typeof(int).MakeByRefType(), parameters[4].ParameterType);
    }

    [Fact]
    public void ProductionServiceAccountIsLocalSystem()
    {
        Assert.Equal("LocalSystem", Program.RequiredServiceAccount);
    }

    [Fact]
    public async Task FrameRoundTripPreservesVersionCommandAndRequest()
    {
        using MemoryStream stream = new();
        byte[] payload = [1, 2, 3];

        await BrokerFrameCodec.WriteAsync(
            stream,
            BrokerCommand.ReadNetworkRequest,
            42,
            payload,
            PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        BrokerFrame result = await BrokerFrameCodec.ReadAsync(
            stream,
            PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
            TestContext.Current.CancellationToken);

        Assert.Equal(BrokerCommand.ReadNetworkRequest, result.Command);
        Assert.Equal(42, result.RequestId);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public async Task ProtocolVersionMismatchIsRejected()
    {
        byte[] header = ValidHeader(BrokerCommand.HelloRequest, payloadLength: 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(4),
            (ushort)(PrivilegedEtwBrokerProtocol.Version + 1));
        using MemoryStream stream = new(header);

        BrokerProtocolException exception = await Assert.ThrowsAsync<BrokerProtocolException>(
            async () => await BrokerFrameCodec.ReadAsync(
                stream,
                PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
                TestContext.Current.CancellationToken));

        Assert.Equal(BrokerErrorCode.ProtocolMismatch, exception.Code);
        Assert.Contains("expected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            (PrivilegedEtwBrokerProtocol.Version + 1).ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OlderProtocolVersionIsRejectedWithBothVersions()
    {
        byte[] header = ValidHeader(BrokerCommand.HelloRequest, payloadLength: 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(4),
            (ushort)(PrivilegedEtwBrokerProtocol.Version - 1));
        using MemoryStream stream = new(header);

        BrokerProtocolException exception = await Assert.ThrowsAsync<BrokerProtocolException>(
            async () => await BrokerFrameCodec.ReadAsync(
                stream,
                PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
                TestContext.Current.CancellationToken));

        Assert.Equal(BrokerErrorCode.ProtocolMismatch, exception.Code);
        Assert.Contains("expected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("received", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedProtocolVersionIsRejectedWithoutUnsupportedFallback()
    {
        byte[] header = ValidHeader(BrokerCommand.HelloRequest, payloadLength: 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), ushort.MaxValue);
        using MemoryStream stream = new(header);

        BrokerProtocolException exception = await Assert.ThrowsAsync<BrokerProtocolException>(
            async () => await BrokerFrameCodec.ReadAsync(
                stream,
                PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
                TestContext.Current.CancellationToken));

        Assert.Equal(BrokerErrorCode.ProtocolMismatch, exception.Code);
        Assert.DoesNotContain("Unsupported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedMessageIsRejectedBeforeAllocation()
    {
        byte[] header = ValidHeader(
            BrokerCommand.ReadNetworkRequest,
            PrivilegedEtwBrokerProtocol.MaximumRequestBytes + 1);
        using MemoryStream stream = new(header);

        BrokerProtocolException exception = await Assert.ThrowsAsync<BrokerProtocolException>(
            async () => await BrokerFrameCodec.ReadAsync(
                stream,
                PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
                TestContext.Current.CancellationToken));

        Assert.Equal(BrokerErrorCode.OversizedMessage, exception.Code);
    }

    [Fact]
    public void UnknownCommandsAndUnknownJsonFieldsAreRejected()
    {
        Assert.False(PrivilegedEtwBrokerServer.IsAllowedCommand((BrokerCommand)ushort.MaxValue));
        Assert.Throws<BrokerProtocolException>(() =>
            BrokerJson.Deserialize<BrokerProcessRequest>(
                """{"Processes":[],"Unexpected":true}"""u8));
    }

    [Fact]
    public void ProcessRequestRoundTripsEmptyAndExactIdentities()
    {
        ProcessInstanceId identity = new(20, DateTimeOffset.UtcNow);

        BrokerProcessRequest empty = BrokerJson.Deserialize<BrokerProcessRequest>(
            BrokerJson.Serialize(new BrokerProcessRequest([])));
        BrokerProcessRequest populated = BrokerJson.Deserialize<BrokerProcessRequest>(
            BrokerJson.Serialize(new BrokerProcessRequest([identity])));

        Assert.Empty(empty.Processes);
        Assert.Equal([identity], populated.Processes);
    }

    [Fact]
    public void MetricEventBatchesRoundTripNonEmptyPayloads()
    {
        DateTimeOffset timestamp = new(2026, 7, 29, 12, 0, 0, TimeSpan.FromHours(3.5));
        PhysicalDiskEventBatch disk = BrokerJson.Deserialize<PhysicalDiskEventBatch>(
            BrokerJson.Serialize(new PhysicalDiskEventBatch(
                [new PhysicalDiskIoEvent(20, 30, timestamp, PhysicalDiskOperation.Write, 4096)],
                MetricAvailability.Available,
                0,
                0,
                0)));
        NetworkEventBatch network = BrokerJson.Deserialize<NetworkEventBatch>(
            BrokerJson.Serialize(new NetworkEventBatch(
                [
                    new NetworkTrafficEvent(
                        20,
                        timestamp,
                        NetworkDirection.Download,
                        NetworkTransport.Tcp,
                        NetworkAddressFamily.IPv4,
                        2048)
                ],
                MetricAvailability.Available,
                NetworkAvailabilityReason.None,
                0,
                0,
                0)));

        PhysicalDiskIoEvent diskEvent = Assert.Single(disk.Events);
        NetworkTrafficEvent networkEvent = Assert.Single(network.Events);
        Assert.Equal(4096, diskEvent.TransferSize);
        Assert.Equal(2048, networkEvent.TransferSize);
        Assert.Equal(TimeSpan.Zero, diskEvent.TimestampUtc.Offset);
        Assert.Equal(TimeSpan.Zero, networkEvent.TimestampUtc.Offset);
    }

    [Fact]
    public void PipeEndpointBindsUserAndSessionAndRejectsMalformedIdentity()
    {
        BrokerPipeEndpoint endpoint = BrokerPipeEndpoint.Create(
            "S-1-5-21-1-2-3-100",
            7);
        BrokerPipeEndpoint otherSession = BrokerPipeEndpoint.Create(
            endpoint.UserSid,
            8);

        Assert.NotEqual(endpoint.PipeName, otherSession.PipeName);
        Assert.Throws<ArgumentException>(() => BrokerPipeEndpoint.Create("not-a-sid", 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => BrokerPipeEndpoint.Create(endpoint.UserSid, 0));
    }

    [Fact]
    public void PipeDaclAllowsOnlyConfiguredUserAndService()
    {
        BrokerPipeEndpoint endpoint = BrokerPipeEndpoint.Create("S-1-5-21-1-2-3-100", 7);
        SecurityIdentifier serviceSid = new("S-1-5-80-123456789-123456789-123456789-123456789-1234");
        PipeSecurity security = BrokerPipeSecurity.Create(endpoint, serviceSid);
        string sddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.All);

        Assert.Contains(endpoint.UserSid, sddl, StringComparison.Ordinal);
        Assert.Contains(serviceSid.Value, sddl, StringComparison.Ordinal);
        Assert.Contains("S-1-5-2", sddl, StringComparison.Ordinal);
        Assert.Equal(
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            BrokerPipeSecurity.ClientAccess);
        Assert.DoesNotContain("S-1-1-0", sddl, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointAuthorizationRejectsWrongUserAndSession()
    {
        BrokerPipeEndpoint endpoint = BrokerPipeEndpoint.Create("S-1-5-21-1-2-3-100", 7);
        BrokerClientAuthorizer authorizer = new(
            new FakeIdentityReader(),
            endpoint.UserSid,
            endpoint.SessionId);

        Assert.False(authorizer.IsClientAllowedForEndpoint(
            new BrokerObservedProcess(10, DateTimeOffset.UtcNow, "S-1-5-21-1-2-3-101", 7, "MonitoringXS.App.exe")));
        Assert.False(authorizer.IsClientAllowedForEndpoint(
            new BrokerObservedProcess(10, DateTimeOffset.UtcNow, endpoint.UserSid, 8, "MonitoringXS.App.exe")));
        Assert.False(authorizer.IsClientAllowedForEndpoint(
            new BrokerObservedProcess(10, DateTimeOffset.UtcNow, endpoint.UserSid, 7, "other.exe")));
    }

    [Fact]
    public async Task MatchingEndpointCompletesVersionOneHandshakeWithoutElevation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        BrokerPipeEndpoint endpoint = BrokerPipeEndpoint.Create(
            identity.User!.Value,
            sessionId: 9999);
        SecurityIdentifier serviceSid = new("S-1-5-80-123456789-123456789-123456789-123456789-1234");
        await using NamedPipeServerStream server = NamedPipeServerStreamAcl.Create(
            endpoint.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            BrokerPipeSecurity.Create(endpoint, serviceSid, setOwner: false),
            HandleInheritability.None);
        Task serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            BrokerFrame hello = await BrokerFrameCodec.ReadAsync(
                server,
                PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
                TestContext.Current.CancellationToken);
            Assert.Equal(BrokerCommand.HelloRequest, hello.Command);
            await BrokerFrameCodec.WriteAsync(
                server,
                BrokerCommand.HelloResponse,
                hello.RequestId,
                BrokerJson.Serialize(new BrokerHelloResponse(
                    PrivilegedEtwBrokerProtocol.Version,
                    Guid.NewGuid())),
                PrivilegedEtwBrokerProtocol.MaximumResponseBytes,
                TestContext.Current.CancellationToken);
            BrokerFrame request = await BrokerFrameCodec.ReadAsync(
                server,
                PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
                TestContext.Current.CancellationToken);
            await BrokerFrameCodec.WriteAsync(
                server,
                BrokerCommand.PhysicalDiskResponse,
                request.RequestId,
                BrokerJson.Serialize(new PhysicalDiskEventBatch(
                    [],
                    MetricAvailability.Unavailable,
                    0,
                    0,
                    0,
                    "test")),
                PrivilegedEtwBrokerProtocol.MaximumResponseBytes,
                TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await using PrivilegedEtwBrokerClient client = new(
            new NamedPipeEtwBrokerTransport(endpoint.PipeName));
        PhysicalDiskEventBatch batch = await client.ReadBatchAsync(
            [],
            TestContext.Current.CancellationToken);
        await serverTask;

        Assert.Equal(MetricAvailability.Unavailable, batch.Availability);
    }

    [Fact]
    public void AuthorizationRequiresExpectedClientSameUserSessionAndExactLifetime()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        FakeIdentityReader identities = new(
            new BrokerObservedProcess(10, start, "S-1-5-21-1", 2, "MonitoringXS.App.exe"),
            new BrokerObservedProcess(20, start, "S-1-5-21-1", 2, "child.exe"));
        BrokerClientAuthorizer authorizer = new(identities);
        BrokerObservedProcess client = identities.TryRead(10)!;

        Assert.True(BrokerClientAuthorizer.IsClientAllowed(client));
        Assert.True(authorizer.AreProcessesAllowed(client, [new ProcessInstanceId(20, start)]));
        Assert.False(authorizer.AreProcessesAllowed(
            client with { ExecutableName = "other.exe" },
            [new ProcessInstanceId(20, start)]));
        Assert.False(authorizer.AreProcessesAllowed(
            client,
            [new ProcessInstanceId(20, start.AddTicks(1))]));

        identities.Processes[20] = identities.Processes[20] with { UserSid = "S-1-5-21-2" };
        Assert.False(authorizer.AreProcessesAllowed(client, [new ProcessInstanceId(20, start)]));
        identities.Processes[20] = identities.Processes[20] with
        {
            UserSid = client.UserSid,
            SessionId = client.SessionId + 1
        };
        Assert.False(authorizer.AreProcessesAllowed(client, [new ProcessInstanceId(20, start)]));
    }

    [Fact]
    public void AuthorizationRejectsDuplicatePidsAndOversizedRequests()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        BrokerObservedProcess client =
            new(10, start, "S-1-5-21-1", 2, "MonitoringXS.App.exe");
        BrokerObservedProcess target =
            new(20, start, client.UserSid, client.SessionId, "child.exe");
        BrokerClientAuthorizer authorizer = new(new FakeIdentityReader(client, target));

        Assert.False(authorizer.AreProcessesAllowed(
            client,
            [
                new ProcessInstanceId(20, start),
                new ProcessInstanceId(20, start.AddTicks(1))
            ]));
        Assert.False(authorizer.AreProcessesAllowed(
            client,
            Enumerable.Range(1, PrivilegedEtwBrokerProtocol.MaximumProcesses + 1)
                .Select(processId => new ProcessInstanceId(processId, start))
                .ToArray()));
    }

    [Fact]
    public void BrokerFilteringPreventsCrossProcessAndPreStartLeakage()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        ProcessInstanceId allowed = new(20, start);
        BrokerConnectionCounters counters = new();
        NetworkEventBatch network = new(
            [
                new NetworkTrafficEvent(
                    allowed.ProcessId,
                    start.AddSeconds(1),
                    NetworkDirection.Download,
                    NetworkTransport.Tcp,
                    NetworkAddressFamily.IPv4,
                    100),
                new NetworkTrafficEvent(
                    21,
                    start.AddSeconds(1),
                    NetworkDirection.Upload,
                    NetworkTransport.Udp,
                    NetworkAddressFamily.IPv6,
                    200),
                new NetworkTrafficEvent(
                    allowed.ProcessId,
                    start.AddSeconds(-1),
                    NetworkDirection.Upload,
                    NetworkTransport.Tcp,
                    NetworkAddressFamily.IPv4,
                    300)
            ],
            MetricAvailability.Available,
            NetworkAvailabilityReason.None,
            0,
            0,
            99,
            ActiveTcpConnectionsByProcess: new Dictionary<int, int>
            {
                [20] = 1,
                [21] = 2
            });

        NetworkEventBatch filtered = counters.Filter(network, [allowed]);

        NetworkTrafficEvent retained = Assert.Single(filtered.Events);
        Assert.Equal(100, retained.TransferSize);
        Assert.Equal(1, filtered.EventsObserved);
        Assert.Equal(100UL, filtered.TotalSourceReceiveBytes);
        Assert.Equal(0UL, filtered.TotalSourceSendBytes);
        Assert.Equal(0, filtered.UnattributedEvents);
        Assert.Equal(1, Assert.Single(filtered.ActiveTcpConnectionsByProcess!).Value);
    }

    [Fact]
    public async Task BrokerUnavailableReturnsUnavailableWithoutFakeZero()
    {
        await using PrivilegedEtwBrokerClient client =
            new(new ThrowingTransport(new IOException("offline")));
        ProcessInstanceId process = new(20, DateTimeOffset.UtcNow);

        PhysicalDiskEventBatch disk = await client.ReadBatchAsync(
            [process],
            TestContext.Current.CancellationToken);
        NetworkEventBatch network = await client.ReadNetworkBatchAsync(
            [process],
            TestContext.Current.CancellationToken);

        Assert.Equal(MetricAvailability.Unavailable, disk.Availability);
        Assert.Empty(disk.Events);
        Assert.Equal(MetricAvailability.Unavailable, network.Availability);
        Assert.Empty(network.Events);
    }

    [Fact]
    public async Task PipeConnectDeniedReturnsUnavailableWithExactDiagnostic()
    {
        await using PrivilegedEtwBrokerClient client =
            new(new ThrowingTransport(new BrokerConnectionException(
                "Named pipe connect was denied before protocol handshake.",
                new UnauthorizedAccessException())));

        PhysicalDiskEventBatch batch = await client.ReadBatchAsync(
            [],
            TestContext.Current.CancellationToken);

        Assert.Equal(MetricAvailability.Unavailable, batch.Availability);
        Assert.Contains("before protocol handshake", batch.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconnectAndBrokerRestartMarkFirstRecoveredBatchPartial()
    {
        PhysicalDiskEventBatch available = new(
            [],
            MetricAvailability.Available,
            0,
            0,
            0);
        SequenceTransport transport = new(
            new BrokerTransportResponse(
                BrokerCommand.ErrorResponse,
                BrokerJson.Serialize(new BrokerErrorResponse(BrokerErrorCode.ServiceUnavailable)),
                false),
            new BrokerTransportResponse(
                BrokerCommand.PhysicalDiskResponse,
                BrokerJson.Serialize(available),
                true));
        await using PrivilegedEtwBrokerClient client = new(transport);
        ProcessInstanceId process = new(20, DateTimeOffset.UtcNow);

        PhysicalDiskEventBatch first = await client.ReadBatchAsync(
            [process],
            TestContext.Current.CancellationToken);
        PhysicalDiskEventBatch recovered = await client.ReadBatchAsync(
            [process],
            TestContext.Current.CancellationToken);

        Assert.Equal(MetricAvailability.Unavailable, first.Availability);
        Assert.Equal(MetricAvailability.Partial, recovered.Availability);
        Assert.Contains("reconnected", recovered.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndDisposalIsIdempotent()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        CancellingTransport transport = new();
        PrivilegedEtwBrokerClient client = new(transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.ReadBatchAsync(
                [new ProcessInstanceId(20, DateTimeOffset.UtcNow)],
                cancellation.Token));
        client.Dispose();
        client.Dispose();

        Assert.True(transport.Disposed);
    }

    [Fact]
    public void ResourceBoundsAreFixedAndFinite()
    {
        Assert.Equal(2_048, PrivilegedEtwBrokerProtocol.MaximumProcesses);
        Assert.Equal(64 * 1024, PrivilegedEtwBrokerProtocol.MaximumRequestBytes);
        Assert.Equal(4 * 1024 * 1024, PrivilegedEtwBrokerProtocol.MaximumResponseBytes);
        Assert.Equal(16_384, EtwPhysicalDiskEventSource.EventQueueCapacity);
        Assert.Equal(16_384, EtwPhysicalDiskEventSource.NetworkEventQueueCapacity);
    }

    private static byte[] ValidHeader(BrokerCommand command, int payloadLength)
    {
        byte[] header = new byte[PrivilegedEtwBrokerProtocol.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, PrivilegedEtwBrokerProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(4),
            PrivilegedEtwBrokerProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)command);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), payloadLength);
        return header;
    }

    private sealed class FakeIdentityReader(params BrokerObservedProcess[] processes)
        : IBrokerProcessIdentityReader
    {
        public Dictionary<int, BrokerObservedProcess> Processes { get; } =
            processes.ToDictionary(item => item.ProcessId);

        public BrokerObservedProcess? TryRead(int processId) =>
            Processes.GetValueOrDefault(processId);
    }

    private sealed class ThrowingTransport(Exception exception) : IPrivilegedEtwBrokerTransport
    {
        public ValueTask<BrokerTransportResponse> ExchangeAsync(
            BrokerCommand command,
            byte[] payload,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<BrokerTransportResponse>(exception);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SequenceTransport(params BrokerTransportResponse[] responses)
        : IPrivilegedEtwBrokerTransport
    {
        private readonly Queue<BrokerTransportResponse> _responses = new(responses);

        public ValueTask<BrokerTransportResponse> ExchangeAsync(
            BrokerCommand command,
            byte[] payload,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_responses.Dequeue());

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellingTransport : IPrivilegedEtwBrokerTransport
    {
        public bool Disposed { get; private set; }

        public ValueTask<BrokerTransportResponse> ExchangeAsync(
            BrokerCommand command,
            byte[] payload,
            CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<BrokerTransportResponse>(cancellationToken);

        public void Dispose()
        {
            Disposed = true;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
