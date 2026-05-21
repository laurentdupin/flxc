namespace Flx.Compiler.Build;

internal sealed class BuildResult
{
    public BuildResult(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int ExitCode { get; }
    public string StandardOutput { get; }
    public string StandardError { get; }
    public bool Success => ExitCode == 0;
}
