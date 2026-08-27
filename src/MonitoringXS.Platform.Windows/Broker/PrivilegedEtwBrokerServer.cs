using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Metrics;

namespace MonitoringXS.Platform.Windows.Broker;

public sealed class PrivilegedEtwBrokerServer : IDisposable, IAsyncDisposable
{
    public const string ServiceName = "MonitoringXS.PrivilegedEtwBroker";
    private readonly EtwPhysicalDiskEventSource _eventSource;
    private readonly BrokerClientAuthorizer _authorizer;
    private readonly BrokerPipeEndpoint _endpoint;
    private readonly SecurityIdentifier _serviceSid;
    private readonly SecurityIdentifier _pipeOwnerSid;
    private readonly Action<string>? _diagnostic;
    private readonly Guid _serviceInstanceId = Guid.NewGuid();
    private bool _disposed;

    public PrivilegedEtwBrokerServer(
        BrokerPipeEndpoint endpoint,
        Action<string>? diagnostic = null)
        : this(
            new EtwPhysicalDiskEventSource(),
            new BrokerClientAuthorizer(endpoint),
            endpoint,
            BrokerPipeSecurity.ResolveServiceSid(ServiceName),
            diagnostic,
            GetCurrentIdentitySid())
    {
    }

    internal PrivilegedEtwBrokerServer(
        EtwPhysicalDiskEventSource eventSource,
        BrokerClientAuthorizer authorizer,
        BrokerPipeEndpoint endpoint,
        SecurityIdentifier serviceSid,
        Action<string>? diagnostic = null,
        SecurityIdentifier? pipeOwnerSid = null)
    {
        _eventSource = eventSource;
        _authorizer = authorizer;
        _endpoint = endpoint;
        _serviceSid = serviceSid;
        _diagnostic = diagnostic;
        _pipeOwnerSid = pipeOwnerSid
            ?? new SecurityIdentifier(WellKnownSidType.LocalServiceSid, domainSid: null);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                Diagnostic($"pipe-create-start name={_endpoint.PipeName}");
                pipe = CreatePipe();
                Diagnostic($"pipe-created name={_endpoint.PipeName}");
                Diagnostic($"listening name={_endpoint.PipeName}");
                await pipe!.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ProcessClientAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // A client idle/request timeout is isolated to this connection.
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or BrokerProtocolException
                or JsonException)
            {
                Diagnostic($"server-exception type={exception.GetType().FullName} message={exception.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _eventSource.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _eventSource.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal static bool IsAllowedCommand(BrokerCommand command) =>
        command is BrokerCommand.HelloRequest
            or BrokerCommand.ReadPhysicalDiskRequest
            or BrokerCommand.ReadNetworkRequest;

    private async Task ProcessClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        BrokerObservedProcess? client = _authorizer.TryReadClient(pipe);
        using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(PrivilegedEtwBrokerProtocol.IdleTimeout);
        BrokerFrame hello = await BrokerFrameCodec.ReadAsync(
            pipe,
            PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
            idle.Token).ConfigureAwait(false);
        if (hello.Command != BrokerCommand.HelloRequest || hello.Payload.Length != 0)
        {
            await SendErrorAsync(pipe, hello.RequestId, BrokerErrorCode.UnknownCommand, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (client is null || !_authorizer.IsClientAllowedForEndpoint(client))
        {
            await SendErrorAsync(pipe, hello.RequestId, BrokerErrorCode.Unauthorized, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await SendAsync(
            pipe,
            BrokerCommand.HelloResponse,
            hello.RequestId,
            new BrokerHelloResponse(
                PrivilegedEtwBrokerProtocol.Version,
                _serviceInstanceId),
            cancellationToken).ConfigureAwait(false);

        BrokerConnectionCounters counters = new();
        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            BrokerFrame request;
            try
            {
                using CancellationTokenSource timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(PrivilegedEtwBrokerProtocol.IdleTimeout);
                request = await BrokerFrameCodec.ReadAsync(
                    pipe,
                    PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }

            if (!IsAllowedCommand(request.Command) || request.Command == BrokerCommand.HelloRequest)
            {
                await SendErrorAsync(
                    pipe,
                    request.RequestId,
                    BrokerErrorCode.UnknownCommand,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            BrokerProcessRequest processRequest;
            try
            {
                processRequest = BrokerJson.Deserialize<BrokerProcessRequest>(request.Payload);
            }
            catch (BrokerProtocolException exception)
            {
                await SendErrorAsync(pipe, request.RequestId, exception.Code, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!_authorizer.AreProcessesAllowed(client, processRequest.Processes))
            {
                await SendErrorAsync(
                    pipe,
                    request.RequestId,
                    BrokerErrorCode.Unauthorized,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (request.Command == BrokerCommand.ReadPhysicalDiskRequest)
            {
                PhysicalDiskEventBatch batch = await _eventSource
                    .ReadBatchAsync(processRequest.Processes, cancellationToken)
                    .ConfigureAwait(false);
                if (!_authorizer.AreProcessesAllowed(client, processRequest.Processes))
                {
                    await SendErrorAsync(
                        pipe,
                        request.RequestId,
                        BrokerErrorCode.Unauthorized,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                PhysicalDiskEventBatch filtered = counters.Filter(batch, processRequest.Processes);
                await SendAsync(
                    pipe,
                    BrokerCommand.PhysicalDiskResponse,
                    request.RequestId,
                    filtered,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            NetworkEventBatch networkBatch = await _eventSource
                .ReadNetworkBatchAsync(processRequest.Processes, cancellationToken)
                .ConfigureAwait(false);
            if (!_authorizer.AreProcessesAllowed(client, processRequest.Processes))
            {
                await SendErrorAsync(
                    pipe,
                    request.RequestId,
                    BrokerErrorCode.Unauthorized,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            NetworkEventBatch filteredNetwork = counters.Filter(networkBatch, processRequest.Processes);
            await SendAsync(
                pipe,
                BrokerCommand.NetworkResponse,
                request.RequestId,
                filteredNetwork,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask SendAsync<T>(
        Stream pipe,
        BrokerCommand command,
        int requestId,
        T response,
        CancellationToken cancellationToken)
    {
        byte[] payload = BrokerJson.Serialize(response);
        if (payload.Length > PrivilegedEtwBrokerProtocol.MaximumResponseBytes)
        {
            await SendErrorAsync(
                pipe,
                requestId,
                BrokerErrorCode.ResourceExhausted,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PrivilegedEtwBrokerProtocol.RequestTimeout);
        await BrokerFrameCodec.WriteAsync(
            pipe,
            command,
            requestId,
            payload,
            PrivilegedEtwBrokerProtocol.MaximumResponseBytes,
            timeout.Token).ConfigureAwait(false);
    }

    private static async ValueTask SendErrorAsync(
        Stream pipe,
        int requestId,
        BrokerErrorCode code,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PrivilegedEtwBrokerProtocol.RequestTimeout);
        await BrokerFrameCodec.WriteAsync(
            pipe,
            BrokerCommand.ErrorResponse,
            requestId,
            BrokerJson.Serialize(new BrokerErrorResponse(code)),
            PrivilegedEtwBrokerProtocol.MaximumResponseBytes,
            timeout.Token).ConfigureAwait(false);
    }

    internal NamedPipeServerStream CreatePipe()
    {
        Diagnostic($"identity-resolved serviceSid={_serviceSid.Value} userSid={_endpoint.UserSid} session={_endpoint.SessionId}");
        PipeSecurity security = BrokerPipeSecurity.Create(
            _endpoint,
            _serviceSid,
            ownerSid: _pipeOwnerSid);
        Diagnostic("acl-created");

        return NamedPipeServerStreamAcl.Create(
            _endpoint.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            PrivilegedEtwBrokerProtocol.PipeBufferBytes,
            PrivilegedEtwBrokerProtocol.PipeBufferBytes,
            security,
            HandleInheritability.None);
    }

    private void Diagnostic(string message) =>
        _diagnostic?.Invoke($"{DateTimeOffset.UtcNow:O} {message}");

    private static SecurityIdentifier GetCurrentIdentitySid() =>
        WindowsIdentity.GetCurrent().User
        ?? throw new UnauthorizedAccessException("The broker service identity SID is unavailable.");
}

internal sealed class BrokerClientAuthorizer
{
    private const string AllowedClientExecutable = "MonitoringXS.App.exe";
    private readonly IBrokerProcessIdentityReader _identityReader;
    private readonly string? _expectedUserSid;
    private readonly int? _expectedSessionId;
    private readonly string? _expectedExecutablePath;
    private readonly Func<string, bool>? _executableValidator;

    public BrokerClientAuthorizer(BrokerPipeEndpoint endpoint)
        : this(
            new WindowsBrokerProcessIdentityReader(),
            endpoint.UserSid,
            endpoint.SessionId,
            BrokerClientExecutableValidator.InstalledApplicationPath,
            BrokerClientExecutableValidator.IsAllowed)
    {
    }

    internal BrokerClientAuthorizer(IBrokerProcessIdentityReader identityReader)
        : this(identityReader, expectedUserSid: null, expectedSessionId: null)
    {
    }

    internal BrokerClientAuthorizer(
        IBrokerProcessIdentityReader identityReader,
        string? expectedUserSid,
        int? expectedSessionId,
        string? expectedExecutablePath = null,
        Func<string, bool>? executableValidator = null)
    {
        _identityReader = identityReader;
        _expectedUserSid = expectedUserSid;
        _expectedSessionId = expectedSessionId;
        _expectedExecutablePath = expectedExecutablePath;
        _executableValidator = executableValidator;
    }

    public BrokerObservedProcess? TryReadClient(NamedPipeServerStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId)
            && processId is > 0 and <= int.MaxValue
            ? _identityReader.TryRead((int)processId)
            : null;

    public static bool IsClientAllowed(BrokerObservedProcess client) =>
        client.SessionId > 0
        && !string.IsNullOrWhiteSpace(client.UserSid)
        && string.Equals(
            client.ExecutableName,
            AllowedClientExecutable,
            StringComparison.OrdinalIgnoreCase);

    public bool IsClientAllowedForEndpoint(BrokerObservedProcess client) =>
        IsClientAllowed(client)
        && (_expectedSessionId is null || client.SessionId == _expectedSessionId)
        && (_expectedUserSid is null
            || string.Equals(client.UserSid, _expectedUserSid, StringComparison.Ordinal))
        && (_expectedExecutablePath is null
            || PathsEqual(client.ExecutablePath, _expectedExecutablePath))
        && (_executableValidator is null
            || client.ExecutablePath is not null && _executableValidator(client.ExecutablePath));

    private static bool PathsEqual(string? left, string right)
    {
        try
        {
            return left is not null
                && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public bool AreProcessesAllowed(
        BrokerObservedProcess client,
        IReadOnlyList<ProcessInstanceId>? requested)
    {
        if (!IsClientAllowed(client)
            || requested is null
            || requested.Count > PrivilegedEtwBrokerProtocol.MaximumProcesses)
        {
            return false;
        }

        HashSet<int> processIds = [];
        foreach (ProcessInstanceId process in requested)
        {
            if (!processIds.Add(process.ProcessId))
            {
                return false;
            }

            BrokerObservedProcess? observed = _identityReader.TryRead(process.ProcessId);
            if (observed is null
                || observed.StartTimeUtc != process.StartTimeUtc
                || observed.SessionId != client.SessionId
                || !string.Equals(observed.UserSid, client.UserSid, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);
}

internal interface IBrokerProcessIdentityReader
{
    BrokerObservedProcess? TryRead(int processId);
}

internal sealed record BrokerObservedProcess(
    int ProcessId,
    DateTimeOffset StartTimeUtc,
    string UserSid,
    int SessionId,
    string ExecutableName,
    string? ExecutablePath = null);

internal sealed class WindowsBrokerProcessIdentityReader : IBrokerProcessIdentityReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    public BrokerObservedProcess? TryRead(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
        {
            return null;
        }

        using SafeProcessHandle process = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (process.IsInvalid
            || !GetProcessTimes(process, out FileTime creation, out _, out _, out _)
            || !ProcessIdToSessionId((uint)processId, out uint sessionId)
            || sessionId > int.MaxValue
            || !OpenProcessToken(process, TokenQuery, out SafeAccessTokenHandle token))
        {
            return null;
        }

        using (token)
        using (WindowsIdentity identity = new(token.DangerousGetHandle()))
        {
            string? userSid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(userSid))
            {
                return null;
            }

            char[] imagePath = new char[32_768];
            int length = imagePath.Length;
            if (!QueryFullProcessImageName(process, 0, imagePath, ref length)
                || length <= 0
                || length > imagePath.Length)
            {
                return null;
            }

            string executablePath = Path.GetFullPath(new string(imagePath, 0, length));
            string executableName = Path.GetFileName(executablePath);
            return new BrokerObservedProcess(
                processId,
                DateTimeOffset.FromFileTime(creation.ToLong()).ToUniversalTime(),
                userSid,
                (int)sessionId,
                executableName,
                executablePath);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _low;
        private readonly uint _high;

        public long ToLong() => ((long)_high << 32) | _low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executableName,
        ref int size);
}

internal sealed class BrokerConnectionCounters
{
    private long _diskEvents;
    private long _diskReadEvents;
    private long _diskWriteEvents;
    private ulong _diskReadBytes;
    private ulong _diskWriteBytes;
    private long _networkEvents;
    private long _sendEvents;
    private long _receiveEvents;
    private long _tcpSendEvents;
    private long _tcpReceiveEvents;
    private long _udpSendEvents;
    private long _udpReceiveEvents;
    private long _ipv4Events;
    private long _ipv6Events;
    private ulong _sendBytes;
    private ulong _receiveBytes;

    public PhysicalDiskEventBatch Filter(
        PhysicalDiskEventBatch batch,
        IReadOnlyList<ProcessInstanceId> allowed)
    {
        Dictionary<int, DateTimeOffset> starts = StartsByProcessId(allowed);
        PhysicalDiskIoEvent[] events = batch.Events
            .Where(item => starts.TryGetValue(item.ProcessId, out DateTimeOffset start)
                && item.TimestampUtc >= start)
            .ToArray();
        foreach (PhysicalDiskIoEvent item in events)
        {
            _diskEvents = SaturatingIncrement(_diskEvents);
            if (item.Operation == PhysicalDiskOperation.Read)
            {
                _diskReadEvents = SaturatingIncrement(_diskReadEvents);
                _diskReadBytes = SaturatingAdd(_diskReadBytes, (ulong)item.TransferSize);
            }
            else
            {
                _diskWriteEvents = SaturatingIncrement(_diskWriteEvents);
                _diskWriteBytes = SaturatingAdd(_diskWriteBytes, (ulong)item.TransferSize);
            }
        }

        return batch with
        {
            Events = events,
            UnattributedEvents = 0,
            EventsObserved = _diskEvents,
            ReadEventsObserved = _diskReadEvents,
            WriteEventsObserved = _diskWriteEvents,
            ReadBytesObserved = _diskReadBytes,
            WriteBytesObserved = _diskWriteBytes,
            MetadataLookupFailures = 0,
            LastSuccessfulEventTimestampUtc = events
                .Select(item => (DateTimeOffset?)item.TimestampUtc)
                .DefaultIfEmpty()
                .Max()
        };
    }

    public NetworkEventBatch Filter(
        NetworkEventBatch batch,
        IReadOnlyList<ProcessInstanceId> allowed)
    {
        Dictionary<int, DateTimeOffset> starts = StartsByProcessId(allowed);
        NetworkTrafficEvent[] events = batch.Events
            .Where(item => starts.TryGetValue(item.ProcessId, out DateTimeOffset start)
                && item.TimestampUtc >= start)
            .ToArray();
        foreach (NetworkTrafficEvent item in events)
        {
            _networkEvents = SaturatingIncrement(_networkEvents);
            if (item.Direction == NetworkDirection.Upload)
            {
                _sendEvents = SaturatingIncrement(_sendEvents);
                _sendBytes = SaturatingAdd(_sendBytes, (ulong)item.TransferSize);
                if (item.Transport == NetworkTransport.Tcp)
                {
                    _tcpSendEvents = SaturatingIncrement(_tcpSendEvents);
                }
                else
                {
                    _udpSendEvents = SaturatingIncrement(_udpSendEvents);
                }
            }
            else
            {
                _receiveEvents = SaturatingIncrement(_receiveEvents);
                _receiveBytes = SaturatingAdd(_receiveBytes, (ulong)item.TransferSize);
                if (item.Transport == NetworkTransport.Tcp)
                {
                    _tcpReceiveEvents = SaturatingIncrement(_tcpReceiveEvents);
                }
                else
                {
                    _udpReceiveEvents = SaturatingIncrement(_udpReceiveEvents);
                }
            }

            if (item.AddressFamily == NetworkAddressFamily.IPv4)
            {
                _ipv4Events = SaturatingIncrement(_ipv4Events);
            }
            else
            {
                _ipv6Events = SaturatingIncrement(_ipv6Events);
            }
        }

        HashSet<int> processIds = allowed.Select(item => item.ProcessId).ToHashSet();
        return batch with
        {
            Events = events,
            UnattributedEvents = 0,
            EventsObserved = _networkEvents,
            ActiveTcpConnectionsByProcess = FilterCounts(batch.ActiveTcpConnectionsByProcess, processIds),
            UdpEndpointsByProcess = FilterCounts(batch.UdpEndpointsByProcess, processIds),
            SendEvents = _sendEvents,
            ReceiveEvents = _receiveEvents,
            TcpSendEvents = _tcpSendEvents,
            TcpReceiveEvents = _tcpReceiveEvents,
            UdpSendEvents = _udpSendEvents,
            UdpReceiveEvents = _udpReceiveEvents,
            IPv4Events = _ipv4Events,
            IPv6Events = _ipv6Events,
            TotalSourceSendBytes = _sendBytes,
            TotalSourceReceiveBytes = _receiveBytes,
            SystemProcessEvents = 0,
            UnknownProcessEvents = 0,
            MetadataLookupFailures = 0,
            LastSuccessfulEventTimestampUtc = events
                .Select(item => (DateTimeOffset?)item.TimestampUtc)
                .DefaultIfEmpty()
                .Max()
        };
    }

    private static Dictionary<int, DateTimeOffset> StartsByProcessId(
        IReadOnlyList<ProcessInstanceId> allowed) =>
        allowed.ToDictionary(item => item.ProcessId, item => item.StartTimeUtc);

    private static Dictionary<int, int>? FilterCounts(
        IReadOnlyDictionary<int, int>? source,
        HashSet<int> processIds) =>
        source is null
            ? null
            : source
                .Where(item => processIds.Contains(item.Key))
                .ToDictionary(item => item.Key, item => item.Value);

    private static long SaturatingIncrement(long value) =>
        value == long.MaxValue ? value : value + 1;

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}
