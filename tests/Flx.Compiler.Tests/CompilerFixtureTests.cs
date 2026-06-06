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

    [Fact]
    public async Task NumericComponentFields_EmitDirectFieldStorage()
    {
        using var output = TemporaryOutputDirectory.Create();

        var result = await RunCompilerAsync(output.Path, "numeric_component_fields.flx");

        AssertSuccess(result);

        var runtimeSource = await File.ReadAllTextAsync(Path.Combine(output.Path, "flx_runtime.g.c"));
        Assert.Contains(".x = 3;", runtimeSource);
        Assert.Contains(".y = 4;", runtimeSource);
        Assert.Contains(".energy = 5;", runtimeSource);

        var moduleSource = await File.ReadAllTextAsync(Path.Combine(output.Path, "numeric_component_fields.flx.g.c"));
        Assert.Contains(".x = agent.ptr->AgentData.x + i;", moduleSource);
        Assert.Contains(".y = agent.ptr->AgentData.y + (i * 2);", moduleSource);
        Assert.Contains(".energy = agent.ptr->AgentData.energy + (usize)i;", moduleSource);
        Assert.Contains("agent.ptr->AgentData.x", moduleSource);
        Assert.Contains("agent.ptr->AgentData.y", moduleSource);
        Assert.Contains("agent.ptr->AgentData.energy", moduleSource);
    }

    [Fact]
    public async Task MutatingCurrentPrefabFields_CanRunParallel()
    {
        using var output = TemporaryOutputDirectory.Create();

        var result = await RunCompilerAsync(output.Path, "parallel_mutating_fields.flx");

        AssertSuccess(result);

        using var metadata = ReadSingleMetadata(output.Path);
        var move = FindFunction(metadata, "Move");

        Assert.True(move.GetProperty("parallelizable").GetBoolean());

        var mainSource = await File.ReadAllTextAsync(Path.Combine(output.Path, "flx_main.g.c"));
        Assert.Contains("flx_parallel_for(", mainSource);

        var moduleSource = await File.ReadAllTextAsync(Path.Combine(output.Path, "parallel_mutating_fields.flx.g.c"));
        Assert.Contains("agent.ptr->AgentData.x = agent.ptr->AgentData.x + agent.ptr->AgentData.velocity;", moduleSource);
    }

    [Fact]
    public async Task ExplainSchedule_ReportsParallelAndSerialReasons()
    {
        using var parallelOutput = TemporaryOutputDirectory.Create();
        var parallel = await RunCompilerAsync(
            parallelOutput.Path,
            "parallel_zombies.flx",
            emitC: false,
            "--explain-schedule");

        AssertSuccess(parallel);
        Assert.Contains("Schedule explanation:", parallel.Output);
        Assert.Contains("run CreateZombies: serial", parallel.Output);
        Assert.Contains("CreateZombies: function creates objects", parallel.Output);
        Assert.Contains("run Greet: parallel", parallel.Output);
        Assert.Contains("Greet: parallelizable", parallel.Output);
        Assert.False(File.Exists(Path.Combine(parallelOutput.Path, "flx_main.g.c")), parallel.Output);

        using var serialOutput = TemporaryOutputDirectory.Create();
        var serial = await RunCompilerAsync(
            serialOutput.Path,
            "parallel_zombies_unannotated_printf.flx",
            emitC: false,
            "--explain-schedule");

        AssertSuccess(serial);
        Assert.Contains("run Greet: serial", serial.Output);
        Assert.Contains("Greet: calls external function 'stdio.printf' that is not marked parallel", serial.Output);

        using var mutatingOutput = TemporaryOutputDirectory.Create();
        var mutating = await RunCompilerAsync(
            mutatingOutput.Path,
            "parallel_mutating_fields.flx",
            emitC: false,
            "--explain-schedule");

        AssertSuccess(mutating);
        Assert.Contains("run Move: parallel", mutating.Output);
        Assert.Contains("Move: parallelizable", mutating.Output);
    }

    [Fact]
    public async Task TaskDeclaration_EmitsMetadataWithoutCBody()
    {
        using var output = TemporaryOutputDirectory.Create();

        var result = await RunCompilerAsync(output.Path, "task_declaration.flx", true, "--no-main");

        AssertSuccess(result);

        using var metadata = ReadSingleMetadata(output.Path);
        var task = Assert.Single(metadata.RootElement.GetProperty("tasks").EnumerateArray());

        Assert.Equal("LoadFile", task.GetProperty("sourceName").GetString());
        Assert.Equal("LoadFile", task.GetProperty("fullName").GetString());
        Assert.Equal("Buffer", task.GetProperty("returnType").GetString());

        var parameters = task.GetProperty("parameters").EnumerateArray().ToArray();
        var parameter = Assert.Single(parameters);
        Assert.Equal("string", parameter.GetProperty("type").GetString());
        Assert.Equal("path", parameter.GetProperty("name").GetString());

        Assert.Equal(
            new[] { "blocking_io", "asset_decode" },
            task.GetProperty("effects").EnumerateArray().Select(effect => effect.GetString()!).ToArray());

        var moduleSource = await File.ReadAllTextAsync(Path.Combine(output.Path, "task_declaration.flx.g.c"));
        Assert.DoesNotContain("LoadFile", moduleSource);
    }

    [Theory]
    [InlineData("task_mutates_world.flx", "FLX0802")]
    [InlineData("task_prefab_parameter.flx", "FLX0803")]
    public async Task InvalidTaskDeclarations_EmitExpectedDiagnostics(string fixtureName, string diagnosticCode)
    {
        using var output = TemporaryOutputDirectory.Create();

        var result = await RunCompilerAsync(output.Path, fixtureName, true, "--no-main");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnosticCode, result.Output);
    }

    [Fact]
    public async Task ScheduledTask_EmitsTaskSpecificDiagnostic()
    {
        using var output = TemporaryOutputDirectory.Create();

        var result = await RunCompilerAsync(output.Path, "task_scheduled.flx");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("FLX0804", result.Output);
        Assert.DoesNotContain("FLX0101", result.Output);
    }

    private static async Task<CompilerResult> RunCompilerAsync(
        string outputDirectory,
        string fixtureName,
        bool emitC = true,
        params string[] extraArgs)
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
        if (emitC)
            startInfo.ArgumentList.Add("--emit-c");
        startInfo.ArgumentList.Add("--no-preprocess");
        startInfo.ArgumentList.Add("--obj-dir");
        startInfo.ArgumentList.Add(outputDirectory);
        foreach (var extraArg in extraArgs)
            startInfo.ArgumentList.Add(extraArg);
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
