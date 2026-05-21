using System.Diagnostics;

namespace Flx.Compiler.Build;

internal static class ProcessRunner
{
    public static async Task<BuildResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        bool verbose,
        TextWriter output,
        TextWriter error)
    {
        var argumentList = arguments.ToArray();
        if (verbose)
            output.WriteLine(FormatCommand(fileName, argumentList));

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in argumentList)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new BuildResult(1, "", $"failed to start C compiler '{fileName}': {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stdout))
            output.Write(stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
            error.Write(stderr);

        return new BuildResult(process.ExitCode, stdout, stderr);
    }

    private static string FormatCommand(string fileName, IEnumerable<string> arguments)
    {
        return string.Join(" ", new[] { Quote(fileName) }.Concat(arguments.Select(Quote)));
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
            return "\"\"";

        if (value.Any(char.IsWhiteSpace) || value.Contains('"'))
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        return value;
    }
}
