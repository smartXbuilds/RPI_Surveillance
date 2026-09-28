using System.ComponentModel;
using System.Diagnostics;

namespace RpiCam.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public string ErrorText => string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : StdErr.Trim();
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Runs external tools without a shell (arguments are passed as a list), so user input such as
/// a Wi-Fi name or password can never be interpreted as shell syntax. Arguments are deliberately
/// not logged because they may contain Wi-Fi passwords.
/// </summary>
public sealed class ProcessRunner(ILogger<ProcessRunner> log) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            startInfo.ArgumentList.Add(a);
        }
        // English tool output so parsing and error texts do not depend on the system locale.
        startInfo.Environment["LC_ALL"] = "C";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            log.LogWarning("Could not start {File}: {Message}", fileName, ex.Message);
            return new ProcessResult(127, "", $"{fileName}: {ex.Message}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            log.LogWarning("{File} timed out after {Seconds}s", fileName, timeout.TotalSeconds);
            return new ProcessResult(124, "", $"{fileName} timed out after {timeout.TotalSeconds:0}s");
        }
    }
}
