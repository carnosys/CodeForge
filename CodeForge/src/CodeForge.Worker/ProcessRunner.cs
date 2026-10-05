namespace CodeForge.Worker;



using System.Diagnostics;




public class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
    string filename,
    IEnumerable<string> arguments,
    string workingdir,
    TimeSpan timeout,
    CancellationToken stoppingtoken)
    {

        var startInfo = new ProcessStartInfo
        {
            FileName = filename,
            WorkingDirectory = workingdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false

        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();

        Task<string> stdOut = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErr = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource timeoutCts = new CancellationTokenSource(timeout);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
          stoppingtoken,
          timeoutCts.Token
        );

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }

        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);

            string cancelledStdout = await stdOut;
            string cancelledStderr = await stdErr;

            if (stoppingtoken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult
            {

                ExitCode = -1,
                StandardOutput = cancelledStdout,
                StandardError = cancelledStderr,
                TimeOut = true
            };
        }

        string stdout = await stdOut;
        string stderr = await stdErr;

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout,
            StandardError = stderr,
            TimeOut = false
        };
    }
}



public record ProcessResult
{
    public int ExitCode;
    public string? StandardOutput;
    public string? StandardError;
    public bool TimeOut;
}
