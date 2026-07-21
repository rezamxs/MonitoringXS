using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MonitoringXS.Core.Abstractions;
using MonitoringXS.Core.Models;
using MonitoringXS.Platform.Windows.Caching;

namespace MonitoringXS.Platform.Windows.Security;

public sealed class DigitalSignatureInspector : IDigitalSignatureInspector
{
    public const int DefaultCapacity = 256;

    private readonly BoundedLruCache<FileCacheKey, DigitalSignatureInfo> _cache;
    private readonly Func<string, DigitalSignatureInfo> _inspector;

    public DigitalSignatureInspector()
        : this(InspectFile, DefaultCapacity)
    {
    }

    public DigitalSignatureInspector(Func<string, DigitalSignatureInfo> inspector, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _inspector = inspector;
        _cache = new BoundedLruCache<FileCacheKey, DigitalSignatureInfo>(capacity);
    }

    public int CachedItemCount => _cache.Count;

    public int Capacity => _cache.Capacity;

    public ValueTask<DigitalSignatureInfo> InspectAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        FileCacheKey key = FileCacheKey.Create(executablePath);
        if (_cache.TryGetValue(key, out DigitalSignatureInfo? cached))
        {
            return ValueTask.FromResult(cached!);
        }

        DigitalSignatureInfo result = _inspector(executablePath);
        _cache.Set(key, result);
        return ValueTask.FromResult(result);
    }

    private static DigitalSignatureInfo InspectFile(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // Required to extract an embedded Authenticode certificate from a signed PE file.
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using X509Certificate2 certificate2 = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            string? signer = NullIfWhitespace(certificate2.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
            return new DigitalSignatureInfo(
                DigitalSignatureStatus.CertificatePresent,
                signer,
                NullIfWhitespace(certificate2.Subject),
                NullIfWhitespace(certificate2.Thumbprint),
                "An embedded Authenticode signer certificate is present; trust validation is not implied.");
        }
        catch (CryptographicException)
        {
            return new DigitalSignatureInfo(
                DigitalSignatureStatus.CertificateNotPresent,
                null,
                null,
                null,
                "No embedded Authenticode signer certificate was found.");
        }
        catch (Exception exception) when (exception is ArgumentException
            or UnauthorizedAccessException
            or NotSupportedException
            or IOException
            or System.Security.SecurityException)
        {
            return new DigitalSignatureInfo(
                DigitalSignatureStatus.Unavailable,
                null,
                null,
                null,
                $"Signature inspection unavailable ({exception.GetType().Name}).");
        }
    }

    private static string? NullIfWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
