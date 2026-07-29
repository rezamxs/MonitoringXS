namespace MonitoringXS.App;

internal static class LiveRefreshLoop
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> refreshAsync,
        TimeSpan interval,
        Action<Exception> onFault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refreshAsync);
        ArgumentNullException.ThrowIfNull(onFault);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        using PeriodicTimer timer = new(interval);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await refreshAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                onFault(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}
