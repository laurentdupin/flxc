using System.Diagnostics;
using System.Text.Json;

namespace Flx.Compiler.Tests;

public sealed class CompilerFixtureTests
{
    [Fact]
    public async Task ParallelZombies_EmitsCMetadataAndParallelMain()
    {
        using var output = TemporaryOutputDirectory.Create();

        var result = await RunCompilerAsync(output.Path, "parallel_zombies.flx");

        AssertSuccess(result);
        Assert.True(File.Exists(Path.Combine(output.Path, "parallel_zombies.flx.g.c")), result.Output);
        Assert.True(File.Exists(Path.Combine(output.Path, "flx_main.g.c")), result.Output);

        using var metadata = ReadSingleMetadata(output.Path);
        var greet = FindFunction(metadata, "Greet");
        var createZombies = FindFunction(metadata, "CreateZombies");

        Assert.True(greet.GetProperty("parallelizable").GetBoolean());
        Assert.Null(GetParallelReason(greet));
        Assert.False(createZombies.GetProperty("parallelizable").GetBoolean());
        Assert.Equal("function creates objects", GetParallelReason(createZombies));

        var mainSource = await File.ReadAllTextAsync(Path.Combine(output.Path, "flx_main.g.c"));
        Assert.Contains("flx_parallel_for(", mainSource);
    }

    [Fact]
    public async Task UnannotatedPrintf_EmitsSerialMetadataAndSerialMain()
    {
        using var output = TemporaryOutputDirectory.Create();

        var result = await RunCompilerAsync(output.Path, "parallel_zombies_unannotated_printf.flx");

        AssertSuccess(result);

        using var metadata = ReadSingleMetadata(output.Path);
        var greet = FindFunction(metadata, "Greet");

        Assert.False(greet.GetProperty("parallelizable").GetBoolean());
        Assert.Equal(
            "calls external function 'stdio.printf' that is not marked parallel",
            GetParallelReason(greet));

        var mainSource = await File.ReadAllTextAsync(Path.Combine(output.Path, "flx_main.g.c"));
        Assert.DoesNotContain("flx_parallel_for(", mainSource);
    }

    [Theory]
    [InlineData("parallel_invalid_alias.flx", "FLX0702")]
    [InlineData("parallel_invalid_target.flx", "FLX0701")]
    public async Task InvalidParallelDeclarations_EmitExpectedDiagnostics(string fixtureName, string diagnosticCode)
    {
        using var output = TemporaryOutputDirectory.Create();

        var result = await RunCompilerAsync(output.Path, fixtureName);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnosticCode, result.Output);
    }

    [Fact]
    public async Task UnsupportedConcat_EmitsExpectedDiagnostic()
    {
        using var output = TemporaryOutputDirectory.Create();

        var result = await RunCompilerAsync(output.Path, "unsupported_concat.flx");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("FLX0703", result.Output);
    }

    private static async Task<CompilerResult> RunCompilerAsync(string outputDirectory, string fixtureName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var compilerAssembly = FindCompilerAssembly(repositoryRoot);
        var fixturePath = Path.Combine(repositoryRoot, "tests", "fixtures", fixtureName);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(compilerAssembly);
        startInfo.ArgumentList.Add("--emit-c");
        startInfo.ArgumentList.Add("--no-preprocess");
        startInfo.ArgumentList.Add("--obj-dir");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add(fixturePath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start flxc process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException("flxc did not exit within 30 seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new CompilerResult(process.ExitCode, stdout, stderr);
    }

    private static void AssertSuccess(CompilerResult result)
    {
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static JsonDocument ReadSingleMetadata(string outputDirectory)
    {
        var metadataPath = Assert.Single(Directory.GetFiles(outputDirectory, "*.meta.json"));
        return JsonDocument.Parse(File.ReadAllText(metadataPath));
    }

    private static JsonElement FindFunction(JsonDocument metadata, string sourceName)
    {
        foreach (var function in metadata.RootElement.GetProperty("functions").EnumerateArray())
        {
            if (function.GetProperty("sourceName").GetString() == sourceName)
                return function;
        }

        throw new InvalidOperationException($"Function '{sourceName}' was not found in generated metadata.");
    }

    private static string? GetParallelReason(JsonElement function)
    {
        return function.TryGetProperty("parallelReason", out var reason)
            ? reason.GetString()
            : null;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "flxc.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    private static string FindCompilerAssembly(string repositoryRoot)
    {
        var copiedAssembly = Path.Combine(AppContext.BaseDirectory, "flxc.dll");
        if (File.Exists(copiedAssembly))
            return copiedAssembly;

        var candidates = Directory
            .GetFiles(Path.Combine(repositoryRoot, "src", "Flx.Compiler", "bin"), "flxc.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();

        return candidates.Length > 0
            ? candidates[0]
            : throw new FileNotFoundException("Could not locate built flxc.dll.", copiedAssembly);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private sealed class TemporaryOutputDirectory : IDisposable
    {
        private TemporaryOutputDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryOutputDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "flxc-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryOutputDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed record CompilerResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string Output => StandardOutput + Environment.NewLine + StandardError;
    }
}
