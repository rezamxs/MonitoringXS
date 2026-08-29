using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace MonitoringXS.Platform.Windows.Broker;

internal static class BrokerClientExecutableValidator
{
    private const string ApplicationExecutable = "MonitoringXS.App.exe";
    private const string BrokerExecutable = "MonitoringXS.PrivilegedBroker.exe";
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static string InstalledApplicationPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Monitoring XS",
        "App",
        ApplicationExecutable);

#if DEBUG
    internal const bool RequiresTrustedPublisher = false;
#else
    internal const bool RequiresTrustedPublisher = true;
#endif

    internal static bool IsAllowed(string executablePath)
    {
        if (!File.Exists(executablePath) || !HasProtectedAcl(executablePath))
        {
            return false;
        }

        return !RequiresTrustedPublisher || HasTrustedBrokerPublisher(executablePath);
    }

    private static bool HasProtectedAcl(string executablePath)
    {
        try
        {
            FileSecurity fileSecurity = new FileInfo(executablePath).GetAccessControl();
            DirectorySecurity directorySecurity = new FileInfo(executablePath).Directory!.GetAccessControl();
            return !HasUntrustedWriter(fileSecurity) && !HasUntrustedWriter(directorySecurity);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or SystemException)
        {
            return false;
        }
    }

    private static bool HasUntrustedWriter(FileSystemSecurity security)
    {
        const FileSystemRights writeRights = FileSystemRights.Write
            | FileSystemRights.Delete
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || (rule.FileSystemRights & writeRights) == 0
                || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
            {
                continue;
            }

            string sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (sid != TrustedInstallerSid
                && !new SecurityIdentifier(sid).IsWellKnown(WellKnownSidType.LocalSystemSid)
                && !new SecurityIdentifier(sid).IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasTrustedBrokerPublisher(string applicationPath)
    {
        string brokerPath = Path.Combine(AppContext.BaseDirectory, BrokerExecutable);
        return VerifyAuthenticodeTrust(applicationPath)
            && VerifyAuthenticodeTrust(brokerPath)
            && string.Equals(ReadPublisher(applicationPath), ReadPublisher(brokerPath), StringComparison.Ordinal);
    }

    private static string? ReadPublisher(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using X509Certificate2 certificate2 = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return certificate2.SubjectName.Name;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static bool VerifyAuthenticodeTrust(string path)
    {
        WinTrustFileInfo fileInfo = new(path);
        nint fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            WinTrustData data = new(fileInfoPointer);
            return WinVerifyTrust(0, WinTrustActionGenericVerifyV2, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct WinTrustFileInfo
    {
        private readonly uint _size;
        [MarshalAs(UnmanagedType.LPWStr)] private readonly string _filePath;
        private readonly nint _fileHandle;
        private readonly nint _knownSubject;

        public WinTrustFileInfo(string filePath)
        {
            _size = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            _filePath = filePath;
            _fileHandle = 0;
            _knownSubject = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct WinTrustData
    {
        private readonly uint _size;
        private readonly nint _policyCallbackData;
        private readonly nint _sipClientData;
        private readonly uint _uiChoice;
        private readonly uint _revocationChecks;
        private readonly uint _unionChoice;
        private readonly nint _fileInfo;
        private readonly uint _stateAction;
        private readonly nint _stateData;
        private readonly nint _urlReference;
        private readonly uint _providerFlags;
        private readonly uint _uiContext;

        public WinTrustData(nint fileInfo)
        {
            _size = (uint)Marshal.SizeOf<WinTrustData>();
            _policyCallbackData = 0;
            _sipClientData = 0;
            _uiChoice = 2; // WTD_UI_NONE
            _revocationChecks = 0;
            _unionChoice = 1; // WTD_CHOICE_FILE
            _fileInfo = fileInfo;
            _stateAction = 0;
            _stateData = 0;
            _urlReference = 0;
            _providerFlags = 0x1000; // WTD_CACHE_ONLY_URL_RETRIEVAL
            _uiContext = 0;
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        nint windowHandle,
        [In] Guid actionId,
        ref WinTrustData trustData);
}
