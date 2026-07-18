namespace MonitoringXS.ElevatedHelper;

internal static class Program
{
    private const int UnsupportedRequest = 64;

    public static int Main(string[] args)
    {
        // No privileged operation is exposed until a versioned, authenticated,
        // allow-listed protocol and target safety validation are implemented.
        return UnsupportedRequest;
    }
}
