using SysProcess = System.Diagnostics.Process;

namespace Dispatch.Claude.Process;

internal static class ProcessTreeKiller
{
    public static async Task ShutdownAsync(SysProcess process, TimeSpan grace, CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }

            using var graceCts = new CancellationTokenSource(grace);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(graceCts.Token, cancellationToken);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
            }
        }
        catch
        {
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
