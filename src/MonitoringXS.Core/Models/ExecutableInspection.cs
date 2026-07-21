namespace MonitoringXS.Core.Models;

public sealed record ExecutableMetadata(
    string ExecutablePath,
    string? ProductName,
    string? FileDescription,
    string? CompanyName,
    string? FileVersion,
    long FileSizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    bool IsAvailable,
    string? UnavailableReason);

public enum DigitalSignatureStatus
{
    CertificatePresent,
    CertificateNotPresent,
    Unavailable
}

public sealed record DigitalSignatureInfo(
    DigitalSignatureStatus Status,
    string? SignerName,
    string? Subject,
    string? Thumbprint,
    string Reason);

public sealed class ApplicationIconData
{
    private readonly byte[] _content;

    public ApplicationIconData(ReadOnlySpan<byte> content, string contentType, int pixelSize)
    {
        _content = content.ToArray();
        ContentType = contentType;
        PixelSize = pixelSize;
    }

    public ReadOnlyMemory<byte> Content => _content;

    public string ContentType { get; }

    public int PixelSize { get; }
}
