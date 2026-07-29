using System.IO.Pipes;
using System.Security.Principal;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;

namespace MonitoringXS.Platform.Windows.Broker;

public sealed class PrivilegedEtwBrokerClient :
    IPhysicalDiskEventSource,
    INetworkEventSource,
    IDisposable,
    IAsyncDisposable
{
    private readonly IPrivilegedEtwBrokerTransport _transport;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private bool _disposed;

    public PrivilegedEtwBrokerClient()
        : this(new NamedPipeEtwBrokerTransport())
    {
    }

    internal PrivilegedEtwBrokerClient(IPrivilegedEtwBrokerTransport transport)
    {
        _transport = transport;
    }

    public async ValueTask<PhysicalDiskEventBatch> ReadBatchAsync(
        IReadOnlyList<ProcessInstanceId> processes,
        CancellationToken cancellationToken)
    {
        try
        {
            BrokerTransportResponse response = await ExchangeAsync(
                BrokerCommand.ReadPhysicalDiskRequest,
                BrokerCommand.PhysicalDiskResponse,
                processes,
                cancellationToken).ConfigureAwait(false);
            PhysicalDiskEventBatch batch = BrokerJson.Deserialize<PhysicalDiskEventBatch>(response.Payload);
            return response.Reconnected && batch.Availability == MetricAvailability.Available
                ? batch with
                {
                    Availability = MetricAvailability.Partial,
                    Detail = "The privileged ETW broker reconnected; this interval is a lower bound."
                }
                : batch;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedBrokerFailure(exception))
        {
            return PhysicalDiskUnavailable(exception);
        }
    }

    public async ValueTask<NetworkEventBatch> ReadNetworkBatchAsync(
        IReadOnlyList<ProcessInstanceId> processes,
        CancellationToken cancellationToken)
    {
        try
        {
            BrokerTransportResponse response = await ExchangeAsync(
                BrokerCommand.ReadNetworkRequest,
                BrokerCommand.NetworkResponse,
                processes,
                cancellationToken).ConfigureAwait(false);
            NetworkEventBatch batch = BrokerJson.Deserialize<NetworkEventBatch>(response.Payload);
            return response.Reconnected && batch.Availability == MetricAvailability.Available
                ? batch with
                {
                    Availability = MetricAvailability.Partial,
                    Reason = NetworkAvailabilityReason.CollectorError,
                    Detail = "The privileged ETW broker reconnected; this interval is a lower bound.",
                    CollectorStatus = MetricAvailability.Partial
                }
                : batch;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedBrokerFailure(exception))
        {
            return NetworkUnavailable(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.Dispose();
        _requestGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _transport.DisposeAsync().ConfigureAwait(false);
        _requestGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async ValueTask<BrokerTransportResponse> ExchangeAsync(
        BrokerCommand requestCommand,
        BrokerCommand responseCommand,
        IReadOnlyList<ProcessInstanceId> processes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (processes.Count > PrivilegedEtwBrokerProtocol.MaximumProcesses)
        {
            throw new BrokerProtocolException(
                BrokerErrorCode.ResourceExhausted,
                "The process request exceeds the configured limit.");
        }

        ProcessInstanceId[] distinct = processes.Distinct().ToArray();
        if (distinct.Length != processes.Count)
        {
            throw new BrokerProtocolException(
                BrokerErrorCode.MalformedMessage,
                "The process request contains duplicate identities.");
        }

        byte[] payload = BrokerJson.Serialize(new BrokerProcessRequest(distinct));
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BrokerTransportResponse response = await _transport
                .ExchangeAsync(requestCommand, payload, cancellationToken)
                .ConfigureAwait(false);
            if (response.Command == BrokerCommand.ErrorResponse)
            {
                BrokerErrorResponse error = BrokerJson.Deserialize<BrokerErrorResponse>(response.Payload);
                throw new BrokerProtocolException(error.Code, ErrorDetail(error.Code));
            }

            if (response.Command != responseCommand)
            {
                throw new BrokerProtocolException(
                    BrokerErrorCode.MalformedMessage,
                    "The broker returned an unexpected response.");
            }

            return response;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static PhysicalDiskEventBatch PhysicalDiskUnavailable(Exception exception)
    {
        (MetricAvailability availability, string detail) = Classify(exception);
        return new PhysicalDiskEventBatch(
            [],
            availability,
            0,
            0,
            0,
            detail,
            SessionStartFailures: 1,
            AccessDeniedFailures: availability == MetricAvailability.AccessDenied ? 1 : 0);
    }

    private static NetworkEventBatch NetworkUnavailable(Exception exception)
    {
        (MetricAvailability availability, string detail) = Classify(exception);
        return new NetworkEventBatch(
            [],
            availability,
            availability == MetricAvailability.AccessDenied
                ? NetworkAvailabilityReason.AccessDenied
                : NetworkAvailabilityReason.CollectorError,
            0,
            0,
            0,
            detail,
            SessionStartFailures: 1,
            AccessDeniedFailures: availability == MetricAvailability.AccessDenied ? 1 : 0,
            CollectorStatus: availability);
    }

    private static (MetricAvailability Availability, string Detail) Classify(Exception exception) =>
        exception is BrokerConnectionException
            ? (MetricAvailability.Unavailable, exception.Message)
            : exception is BrokerProtocolException { Code: BrokerErrorCode.Unauthorized }
            ? (MetricAvailability.AccessDenied, "The privileged ETW broker denied this local client.")
            : exception is BrokerProtocolException { Code: BrokerErrorCode.ProtocolMismatch }
                ? (MetricAvailability.Unsupported, exception.Message)
                : exception is UnauthorizedAccessException
                    ? (MetricAvailability.Unavailable, "Named pipe connect was denied before protocol handshake.")
                : (MetricAvailability.Unavailable, "The privileged ETW broker is unavailable.");

    private static bool IsExpectedBrokerFailure(Exception exception) =>
        exception is BrokerProtocolException
            or IOException
            or TimeoutException
            or UnauthorizedAccessException
            or OperationCanceledException;

    private static string ErrorDetail(BrokerErrorCode code) => code switch
    {
        BrokerErrorCode.Unauthorized => "The privileged ETW broker denied this local client.",
        BrokerErrorCode.ProtocolMismatch => "The privileged ETW broker protocol version is incompatible.",
        BrokerErrorCode.OversizedMessage => "The privileged ETW broker rejected an oversized message.",
        BrokerErrorCode.ResourceExhausted => "The privileged ETW broker reached a configured resource limit.",
        _ => "The privileged ETW broker rejected the request."
    };
}

internal readonly record struct BrokerTransportResponse(
    BrokerCommand Command,
    byte[] Payload,
    bool Reconnected);

internal interface IPrivilegedEtwBrokerTransport : IDisposable, IAsyncDisposable
{
    ValueTask<BrokerTransportResponse> ExchangeAsync(
        BrokerCommand command,
        byte[] payload,
        CancellationToken cancellationToken);
}

internal sealed class NamedPipeEtwBrokerTransport : IPrivilegedEtwBrokerTransport
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private Guid? _serviceInstanceId;
    private int _nextRequestId;
    private bool _hadFailure;
    private bool _disposed;

    public NamedPipeEtwBrokerTransport()
        : this(BrokerPipeEndpoint.ForCurrentProcess().PipeName)
    {
    }

    internal NamedPipeEtwBrokerTransport(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    public async ValueTask<BrokerTransportResponse> ExchangeAsync(
        BrokerCommand command,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            bool reconnected = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            int requestId = NextRequestId();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PrivilegedEtwBrokerProtocol.RequestTimeout);
            await BrokerFrameCodec.WriteAsync(
                _pipe!,
                command,
                requestId,
                payload,
                PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
                timeout.Token).ConfigureAwait(false);
            BrokerFrame response = await BrokerFrameCodec.ReadAsync(
                _pipe!,
                PrivilegedEtwBrokerProtocol.MaximumResponseBytes,
                timeout.Token).ConfigureAwait(false);
            if (response.RequestId != requestId)
            {
                throw new BrokerProtocolException(
                    BrokerErrorCode.MalformedMessage,
                    "The broker response identifier does not match the request.");
            }

            _hadFailure = false;
            return new BrokerTransportResponse(response.Command, response.Payload, reconnected);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Disconnect();
            _hadFailure = true;
            throw new TimeoutException("The privileged ETW broker request timed out.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or BrokerProtocolException)
        {
            Disconnect();
            _hadFailure = true;
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disconnect();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe?.IsConnected == true)
        {
            return false;
        }

        Disconnect();
        NamedPipeClientStream pipe = new(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification,
            HandleInheritability.None);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PrivilegedEtwBrokerProtocol.ConnectTimeout);
            try
            {
                await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new BrokerConnectionException(
                    BrokerServiceProbe.ConnectionFailureDetail(),
                    exception);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BrokerConnectionException(
                    BrokerServiceProbe.ConnectionFailureDetail(),
                    new TimeoutException());
            }
            catch (IOException exception)
            {
                throw new BrokerConnectionException(
                    BrokerServiceProbe.ConnectionFailureDetail(),
                    exception);
            }
            int requestId = NextRequestId();
            await BrokerFrameCodec.WriteAsync(
                pipe,
                BrokerCommand.HelloRequest,
                requestId,
                ReadOnlyMemory<byte>.Empty,
                PrivilegedEtwBrokerProtocol.MaximumRequestBytes,
                timeout.Token).ConfigureAwait(false);
            BrokerFrame response = await BrokerFrameCodec.ReadAsync(
                pipe,
                PrivilegedEtwBrokerProtocol.MaximumResponseBytes,
                timeout.Token).ConfigureAwait(false);
            if (response.RequestId != requestId || response.Command != BrokerCommand.HelloResponse)
            {
                throw new BrokerProtocolException(
                    BrokerErrorCode.ProtocolMismatch,
                    "The privileged ETW broker handshake failed.");
            }

            BrokerHelloResponse hello = BrokerJson.Deserialize<BrokerHelloResponse>(response.Payload);
            if (hello.Version != PrivilegedEtwBrokerProtocol.Version)
            {
                throw new BrokerProtocolException(
                    BrokerErrorCode.ProtocolMismatch,
                    $"The privileged ETW broker protocol version is incompatible. Client version {PrivilegedEtwBrokerProtocol.Version}; server version {hello.Version}.");
            }

            bool restarted = _serviceInstanceId is not null
                && _serviceInstanceId != hello.ServiceInstanceId;
            _serviceInstanceId = hello.ServiceInstanceId;
            _pipe = pipe;
            return restarted || _hadFailure;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private int NextRequestId()
    {
        int requestId = Interlocked.Increment(ref _nextRequestId);
        if (requestId > 0)
        {
            return requestId;
        }

        Interlocked.Exchange(ref _nextRequestId, 1);
        return 1;
    }

    private void Disconnect()
    {
        _pipe?.Dispose();
        _pipe = null;
    }
}

internal sealed class BrokerConnectionException(string message, Exception innerException)
    : IOException(message, innerException);
