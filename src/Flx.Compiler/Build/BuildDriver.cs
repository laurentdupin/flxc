using Flx.Compiler.Cli;
using Flx.Compiler.Codegen.C;
using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;
using Flx.Compiler.Metadata;
using Flx.Compiler.Packages;
using Flx.Compiler.Preprocessing;
using Flx.Compiler.Semantics;

namespace Flx.Compiler.Build;

internal sealed class BuildDriver
{
    public async Task<int> RunAsync(CommandLineOptions options, TextWriter output, TextWriter error)
    {
        var diagnostics = new DiagnosticBag();
        var packageGraph = LoadPackageGraph(options, diagnostics);
        if (packageGraph is not null)
            ApplyPackageBuildOptions(options, packageGraph);

        var sourceFiles = packageGraph is null
            ? LoadSources(options, diagnostics)
            : LoadPackageSources(packageGraph, diagnostics);

        if (diagnostics.HasErrors)
        {
            diagnostics.PrintTo(error);
            return 1;
        }

        var outputDirectory = DetermineGeneratedDirectory(options, out var shouldDeleteDirectory);
        Directory.CreateDirectory(outputDirectory);

        var detector = new ToolchainDetector();
        ICToolchain? toolchain = null;
        BuildOptions? buildOptions = null;

        if (!options.NoPreprocess)
        {
            (toolchain, buildOptions) = detector.Detect(options, diagnostics);
            if (diagnostics.HasErrors || buildOptions is null)
            {
                diagnostics.PrintTo(error);
                return 1;
            }

            sourceFiles = (await new PreprocessorDriver(new ExternalCPreprocessor()).PreprocessAsync(
                sourceFiles,
                options,
                buildOptions,
                outputDirectory,
                diagnostics,
                output,
                error)).ToList();

            if (diagnostics.HasErrors)
            {
                diagnostics.PrintTo(error);
                return 1;
            }
        }
        else if (options.EmitPreprocessed)
        {
            await EmitOriginalAsPreprocessedAsync(sourceFiles, outputDirectory);
        }

        if (options.EmitPreprocessed)
            return 0;

        var units = ParseSources(sourceFiles, diagnostics);
        var requireSchedule = !options.CompileOnly &&
                              !options.BuildLibrary &&
                              !options.NoMain &&
                              packageGraph?.RootPackage.IsLibrary != true;
        var validateScheduleTargets = !options.CompileOnly;
        ValidatePackageSchedules(packageGraph, units, requireSchedule, diagnostics);

        if (diagnostics.HasErrors)
        {
            diagnostics.PrintTo(error);
            return 1;
        }

        var externalPackages = packageGraph?.BinaryPackages.Select(package => package.Metadata).ToArray() ?? [];
        var model = new SemanticAnalyzer(diagnostics).Analyze(units, requireSchedule, validateScheduleTargets, externalPackages);
        ExportValidator.ValidateLibraryExports(packageGraph, model, diagnostics);

        if (diagnostics.HasErrors)
        {
            diagnostics.PrintTo(error);
            return 1;
        }

        var generation = await GenerateAsync(model, options, packageGraph, outputDirectory, shouldDeleteDirectory);

        if (options.EmitC)
            return 0;

        if (toolchain is null || buildOptions is null)
            (toolchain, buildOptions) = detector.Detect(options, diagnostics);

        if (diagnostics.HasErrors || toolchain is null || buildOptions is null)
        {
            diagnostics.PrintTo(error);
            return 1;
        }

        var result = options.CompileOnly
            ? await CompileObjectsAsync(options, generation, toolchain, buildOptions, output, error)
            : await BuildExecutableAsync(options, generation, toolchain, buildOptions, output, error);

        if (result != 0)
            return result;

        if (generation.ShouldDeleteDirectory)
            TryDeleteDirectory(generation.OutputDirectory);

        return 0;
    }

    private static List<SourceFile> LoadSources(CommandLineOptions options, DiagnosticBag diagnostics)
    {
        var sources = new List<SourceFile>();
        foreach (var input in options.InputFiles)
        {
            if (!File.Exists(input))
            {
                diagnostics.Report("FLX9005", $"input file '{input}' does not exist.");
                continue;
            }

            sources.Add(new SourceFile(input, File.ReadAllText(input)));
        }

        return sources;
    }

    private static PackageGraph? LoadPackageGraph(CommandLineOptions options, DiagnosticBag diagnostics)
    {
        if (string.IsNullOrWhiteSpace(options.PackagePath))
            return null;

        var packageGraph = new PackageLoader().Load(options.PackagePath, diagnostics);
        if (options.BuildLibrary && packageGraph?.RootPackage.IsLibrary == false)
            diagnostics.Report("FLX0509", $"--build-library requires a library package, but '{packageGraph.RootPackage.Name}' is '{packageGraph.RootPackage.Type}'.");

        if (packageGraph?.RootPackage.IsLibrary == true &&
            !options.BuildLibrary &&
            !options.EmitC &&
            !options.EmitPreprocessed &&
            !options.CompileOnly)
        {
            diagnostics.Report("FLX0507", $"library package '{packageGraph.RootPackage.Name}' cannot be built as an executable.");
        }

        return packageGraph;
    }

    private static List<SourceFile> LoadPackageSources(PackageGraph packageGraph, DiagnosticBag diagnostics)
    {
        var sources = new List<SourceFile>();
        foreach (var package in packageGraph.SourceOrder)
        {
            foreach (var sourcePath in package.SourcePaths)
            {
                if (!File.Exists(sourcePath))
                {
                    diagnostics.Report("FLX9005", $"input file '{sourcePath}' does not exist.");
                    continue;
                }

                sources.Add(new SourceFile(
                    sourcePath,
                    File.ReadAllText(sourcePath),
                    packageName: package.Name,
                    packageRoot: package.RootDirectory));
            }
        }

        return sources;
    }

    private static void ApplyPackageBuildOptions(CommandLineOptions options, PackageGraph packageGraph)
    {
        foreach (var package in packageGraph.Packages)
        {
            AddDistinct(options.IncludeDirs, package.CIncludeDirs, StringComparer.OrdinalIgnoreCase);
            AddDistinct(options.Libraries, package.CLibraries, StringComparer.Ordinal);
            AddDistinct(options.Defines, package.Defines, StringComparer.Ordinal);
        }

        foreach (var package in packageGraph.BinaryPackages)
        {
            AddDistinct(options.IncludeDirs, package.IncludeDirs, StringComparer.OrdinalIgnoreCase);
            AddDistinct(options.Libraries, package.Libraries, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void ValidatePackageSchedules(
        PackageGraph? packageGraph,
        IReadOnlyList<CompilationUnitSyntax> units,
        bool requireSchedule,
        DiagnosticBag diagnostics)
    {
        if (packageGraph is null)
            return;

        var packagesByName = packageGraph.Packages.ToDictionary(package => package.Name, StringComparer.Ordinal);
        var scheduleCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var unit in units)
        {
            if (unit.Schedules.Count == 0)
                continue;

            var packageName = unit.Source.PackageName;
            if (packageName is null || !packagesByName.TryGetValue(packageName, out var package))
                continue;

            scheduleCounts[packageName] = scheduleCounts.GetValueOrDefault(packageName) + unit.Schedules.Count;

            if (!ReferenceEquals(package, packageGraph.RootPackage))
            {
                foreach (var schedule in unit.Schedules)
                    diagnostics.Report("FLX0505", $"schedule block found in dependency package '{package.Name}'.", schedule.Location);
                continue;
            }

            if (package.IsLibrary)
            {
                foreach (var schedule in unit.Schedules)
                    diagnostics.Report("FLX0503", $"library package '{package.Name}' cannot contain a schedule block.", schedule.Location);
            }
        }

        if (packageGraph.RootPackage.IsExecutable && requireSchedule)
        {
            var rootScheduleCount = scheduleCounts.GetValueOrDefault(packageGraph.RootPackage.Name);
            if (rootScheduleCount != 1)
                diagnostics.Report("FLX0504", $"executable package '{packageGraph.RootPackage.Name}' must contain exactly one schedule block.");
        }
    }

    private static List<CompilationUnitSyntax> ParseSources(IEnumerable<SourceFile> sourceFiles, DiagnosticBag diagnostics)
    {
        var units = new List<CompilationUnitSyntax>();
        foreach (var source in sourceFiles)
        {
            var lexer = new Lexer(source, diagnostics);
            var tokens = lexer.Lex();
            var parser = new Parser(source, tokens, diagnostics);
            units.Add(parser.ParseCompilationUnit());
        }

        return units;
    }

    private static async Task EmitOriginalAsPreprocessedAsync(IReadOnlyList<SourceFile> sourceFiles, string outputDirectory)
    {
        var preprocessedDirectory = Path.Combine(outputDirectory, "preprocessed");
        Directory.CreateDirectory(preprocessedDirectory);

        foreach (var source in sourceFiles)
        {
            var outputPath = Path.Combine(preprocessedDirectory, Path.GetFileName(source.DisplayPath) + ".pp.flx");
            await File.WriteAllTextAsync(outputPath, source.Text);
        }
    }

    private static async Task<GenerationResult> GenerateAsync(
        CompilationModel model,
        CommandLineOptions options,
        PackageGraph? packageGraph,
        string outputDirectory,
        bool shouldDeleteDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var cGenerator = new CGenerator();
        var headerGenerator = new CHeaderGenerator();
        var generatedSources = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string umbrellaHeaderFileName = "flx_program.g.h";

        if (model.RequiresRuntime)
        {
            var runtime = new CRuntimeGenerator();
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "flx_runtime.g.h"), runtime.GenerateHeader(model));
            var runtimeSource = Path.Combine(outputDirectory, "flx_runtime.g.c");
            await File.WriteAllTextAsync(runtimeSource, runtime.GenerateSource(model));
            generatedSources.Add(runtimeSource);
        }

        var moduleHeaders = new List<ModuleHeader>();
        foreach (var module in model.Modules)
        {
            var headerFileName = GeneratedRelativePath(module.SourceFile, ".g.h", usedNames);
            var headerPath = Path.Combine(outputDirectory, headerFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(headerPath)!);
            var runtimeHeaderIncludePath = RelativeIncludePath(outputDirectory, headerFileName, "flx_runtime.g.h");
            await File.WriteAllTextAsync(headerPath, headerGenerator.Generate(module, model, headerFileName, runtimeHeaderIncludePath));
            moduleHeaders.Add(new ModuleHeader(module, headerFileName));
        }

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, umbrellaHeaderFileName),
            new CUmbrellaHeaderGenerator().Generate(model, moduleHeaders));

        foreach (var moduleHeader in moduleHeaders)
        {
            var module = moduleHeader.Module;
            var cFileName = GeneratedRelativePath(module.SourceFile, ".g.c", usedNames);
            var cPath = Path.Combine(outputDirectory, cFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(cPath)!);
            var umbrellaIncludePath = RelativeIncludePath(outputDirectory, cFileName, umbrellaHeaderFileName);
            await File.WriteAllTextAsync(cPath, cGenerator.Generate(module, model, options.AbsoluteLineDirectives, umbrellaIncludePath));
            generatedSources.Add(cPath);

            var metadataPath = Path.Combine(outputDirectory, GeneratedRelativePath(module.SourceFile, ".meta.json", usedNames));
            await MetadataWriter.WriteAsync(module, cPath, metadataPath);
        }

        string? mainSource = null;
        if (!options.CompileOnly && !options.NoMain && model.Schedule is not null)
        {
            mainSource = Path.Combine(outputDirectory, "flx_main.g.c");
            await File.WriteAllTextAsync(mainSource, new CMainGenerator().Generate(model, umbrellaHeaderFileName));
            generatedSources.Add(mainSource);
        }

        if (!string.IsNullOrWhiteSpace(options.GeneratedListPath))
            await WriteGeneratedListAsync(options.GeneratedListPath, generatedSources);

        if (options.BuildLibrary && packageGraph is not null)
            await EmitLibraryPackageArtifactsAsync(packageGraph.RootPackage, model, outputDirectory, options);

        return new GenerationResult(outputDirectory, shouldDeleteDirectory, generatedSources, mainSource);
    }

    private static async Task EmitLibraryPackageArtifactsAsync(
        LoadedPackage package,
        CompilationModel model,
        string outputDirectory,
        CommandLineOptions options)
    {
        var publicIncludeDir = Path.GetFullPath(options.PublicIncludeDir ?? Path.Combine(outputDirectory, "include"));
        Directory.CreateDirectory(publicIncludeDir);

        var publicHeaders = new List<string>();

        var runtimeHeaderPath = Path.Combine(outputDirectory, "flx_runtime.g.h");
        if (File.Exists(runtimeHeaderPath))
        {
            var runtime = new CRuntimeGenerator();
            await File.WriteAllTextAsync(
                Path.Combine(publicIncludeDir, "flx_runtime.g.h"),
                runtime.GenerateHeader(
                    model,
                    component => ExportValidator.IsOwnedByRootPackage(component.SourceFile, package) && component.IsExported,
                    prefab => ExportValidator.IsOwnedByRootPackage(prefab.SourceFile, package) && prefab.IsExported));
        }

        var packageHeaderName = "flx_" + CTypeNames.SafeIdentifier(package.Name) + ".g.h";
        await File.WriteAllTextAsync(
            Path.Combine(publicIncludeDir, packageHeaderName),
            GeneratePackageHeader(package, model));
        publicHeaders.Add(packageHeaderName);

        var metadataPath = Path.GetFullPath(options.MetadataOutputPath ?? Path.Combine(outputDirectory, package.Name + ".flxmeta.json"));
        await PackageMetadataWriter.WriteAsync(package, model, publicHeaders, metadataPath);
    }

    private static string GeneratePackageHeader(LoadedPackage package, CompilationModel model)
    {
        var builder = new System.Text.StringBuilder();
        var guard = "FLX_" + CTypeNames.SafeIdentifier(package.Name).ToUpperInvariant() + "_G_H";
        var exportedFunctions = model.FunctionRegistry.AllFunctions
            .Where(function => ExportValidator.IsOwnedByRootPackage(function.SourceFile, package) && function.IsExported)
            .OrderBy(function => function.FullName, StringComparer.Ordinal)
            .ThenBy(function => function.MangledName, StringComparer.Ordinal)
            .ToArray();
        var cHeaders = exportedFunctions
            .SelectMany(function => function.Module.CImports.Select(import => import.Header))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var usesSizeT = !model.RequiresRuntime && exportedFunctions.Any(function =>
            function.ReturnType == "usize" ||
            function.Parameters.Any(parameter => parameter.Type == "usize"));

        builder.AppendLine("/* Generated by flxc. Do not edit. */");
        builder.AppendLine($"/* Package: {package.Name} */");
        builder.AppendLine();
        builder.AppendLine($"#ifndef {guard}");
        builder.AppendLine($"#define {guard}");
        builder.AppendLine();
        if (model.RequiresRuntime)
        {
            builder.AppendLine("#include \"flx_runtime.g.h\"");
            builder.AppendLine();
        }
        else if (usesSizeT)
        {
            builder.AppendLine("#include <stddef.h>");
            builder.AppendLine();
        }

        foreach (var header in cHeaders)
            builder.AppendLine($"#include <{header}>");

        if (cHeaders.Length > 0)
            builder.AppendLine();

        foreach (var function in exportedFunctions)
            builder.AppendLine(CTypeNames.FormatExternPrototype(function, model));

        if (exportedFunctions.Length > 0)
            builder.AppendLine();

        builder.AppendLine($"#endif /* {guard} */");
        return builder.ToString();
    }

    private static async Task WriteGeneratedListAsync(string generatedListPath, IReadOnlyList<string> generatedSources)
    {
        var fullPath = Path.GetFullPath(generatedListPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var lines = generatedSources
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase);

        await File.WriteAllLinesAsync(fullPath, lines);
    }

    private static string DetermineGeneratedDirectory(CommandLineOptions options, out bool shouldDeleteDirectory)
    {
        if (!string.IsNullOrWhiteSpace(options.ObjDir))
        {
            shouldDeleteDirectory = false;
            return Path.GetFullPath(options.ObjDir);
        }

        if (options.EmitC || options.EmitPreprocessed || options.CompileOnly || options.KeepC || options.KeepPreprocessed)
        {
            shouldDeleteDirectory = false;
            return Path.GetFullPath(Path.Combine("build", "flxc"));
        }

        shouldDeleteDirectory = true;
        return Path.Combine(Path.GetTempPath(), "flxc", Guid.NewGuid().ToString("N"));
    }

    private static async Task<int> CompileObjectsAsync(
        CommandLineOptions options,
        GenerationResult generation,
        ICToolchain toolchain,
        BuildOptions buildOptions,
        TextWriter output,
        TextWriter error)
    {
        for (var i = 0; i < generation.GeneratedSources.Count; i++)
        {
            var source = generation.GeneratedSources[i];
            var outputPath = DetermineObjectOutputPath(options, source);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

            var result = await toolchain.CompileObjectAsync(source, outputPath, buildOptions, output, error);
            if (!result.Success)
                return result.ExitCode;
        }

        return 0;
    }

    private static async Task<int> BuildExecutableAsync(
        CommandLineOptions options,
        GenerationResult generation,
        ICToolchain toolchain,
        BuildOptions buildOptions,
        TextWriter output,
        TextWriter error)
    {
        var outputPath = options.OutputPath ?? DefaultExecutableName();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var result = await toolchain.CompileAndLinkAsync(generation.GeneratedSources, outputPath, buildOptions, output, error);
        return result.Success ? 0 : result.ExitCode;
    }

    private static string DetermineObjectOutputPath(CommandLineOptions options, string sourcePath)
    {
        if (options.OutputPath is not null &&
            options.InputFiles.Count == 1 &&
            !Path.GetFileName(sourcePath).Equals("flx_runtime.g.c", StringComparison.OrdinalIgnoreCase))
        {
            return options.OutputPath;
        }

        var extension = OperatingSystem.IsWindows() ? ".obj" : ".o";
        var fileName = Path.GetFileNameWithoutExtension(sourcePath) + extension;
        return Path.Combine(options.ObjDir is null ? Path.Combine("build", "flxc") : options.ObjDir, fileName);
    }

    private static string DefaultExecutableName() => OperatingSystem.IsWindows() ? "a.exe" : "a.out";

    private static string UniqueFileName(string fileName, HashSet<string> usedNames)
    {
        if (usedNames.Add(fileName))
            return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 1;
        while (true)
        {
            var candidate = $"{stem}.{index}{extension}";
            if (usedNames.Add(candidate))
                return candidate;
            index++;
        }
    }

    private static string GeneratedRelativePath(SourceFile sourceFile, string suffix, HashSet<string> usedNames)
    {
        if (!string.IsNullOrWhiteSpace(sourceFile.PackageName) &&
            !string.IsNullOrWhiteSpace(sourceFile.PackageRoot))
        {
            var packageDirectory = CTypeNames.SafeIdentifier(sourceFile.PackageName);
            var relativeSource = Path.GetRelativePath(sourceFile.PackageRoot, sourceFile.FullPath);
            return Path.Combine(packageDirectory, relativeSource + suffix);
        }

        return UniqueFileName(Path.GetFileName(sourceFile.DisplayPath) + suffix, usedNames);
    }

    private static string RelativeIncludePath(string outputDirectory, string includingRelativeFile, string targetRelativeFile)
    {
        var includingDirectory = Path.GetDirectoryName(includingRelativeFile);
        var fromDirectory = string.IsNullOrWhiteSpace(includingDirectory)
            ? outputDirectory
            : Path.Combine(outputDirectory, includingDirectory);
        var targetPath = Path.Combine(outputDirectory, targetRelativeFile);

        return Path.GetRelativePath(fromDirectory, targetPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static void AddDistinct<T>(List<T> target, IEnumerable<T> values, IEqualityComparer<T> comparer)
    {
        var seen = new HashSet<T>(target, comparer);
        foreach (var value in values)
        {
            if (seen.Add(value))
                target.Add(value);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Intermediate cleanup failure should not fail a successful build.
        }
    }

    private sealed record GenerationResult(
        string OutputDirectory,
        bool ShouldDeleteDirectory,
        IReadOnlyList<string> GeneratedSources,
        string? MainSource);
}
