using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flx.Compiler.Diagnostics;
using Flx.Compiler.Metadata;

namespace Flx.Compiler.Packages;

internal sealed class PackageLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Dictionary<string, LoadedPackage> _packagesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedPackage> _packagesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _manifestNamesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedBinaryPackage> _binaryPackagesByMetadataPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedBinaryPackage> _binaryPackagesByName = new(StringComparer.Ordinal);
    private readonly List<LoadedPackage> _orderedPackages = [];
    private readonly List<LoadedBinaryPackage> _binaryPackages = [];
    private readonly Stack<string> _loadStack = new();
    private readonly bool _validateBinaryArtifacts;

    public PackageLoader(bool validateBinaryArtifacts = true)
    {
        _validateBinaryArtifacts = validateBinaryArtifacts;
    }

    public PackageGraph? Load(string manifestPath, DiagnosticBag diagnostics)
    {
        var root = LoadRecursive(Path.GetFullPath(manifestPath), diagnostics);
        return diagnostics.HasErrors || root is null
            ? null
            : new PackageGraph(root, _orderedPackages, _binaryPackages);
    }

    private LoadedPackage? LoadRecursive(string manifestPath, DiagnosticBag diagnostics)
    {
        manifestPath = Path.GetFullPath(manifestPath);

        if (_packagesByPath.TryGetValue(manifestPath, out var existing))
            return existing;

        if (!File.Exists(manifestPath))
        {
            diagnostics.Report("FLX0502", $"dependency package not found: {manifestPath}");
            return null;
        }

        if (_loadStack.Contains(manifestPath, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Report("FLX0501", $"package dependency cycle detected: {FormatCycle(manifestPath)}");
            return null;
        }

        _loadStack.Push(manifestPath);

        var manifest = ReadManifest(manifestPath, diagnostics);
        if (manifest is null)
        {
            _loadStack.Pop();
            return null;
        }

        var rootDirectory = Path.GetDirectoryName(manifestPath)!;
        ValidateManifest(manifest, manifestPath, diagnostics);
        if (!string.IsNullOrWhiteSpace(manifest.Name))
            _manifestNamesByPath[manifestPath] = manifest.Name;

        var dependencies = new List<LoadedPackage>();
        foreach (var dependency in manifest.Dependencies)
        {
            var hasSourcePath = !string.IsNullOrWhiteSpace(dependency.Path);
            var hasMetadataPath = !string.IsNullOrWhiteSpace(dependency.Metadata);

            if (hasSourcePath && hasMetadataPath)
            {
                diagnostics.Report("FLX0500", $"dependency '{dependency.Name}' in '{manifestPath}' cannot specify both 'path' and 'metadata'.");
                continue;
            }

            if (!hasSourcePath && !hasMetadataPath)
            {
                diagnostics.Report("FLX0502", $"dependency package path or metadata is missing in '{manifestPath}'.");
                continue;
            }

            if (hasSourcePath)
            {
                var dependencyPath = Path.GetFullPath(Path.Combine(rootDirectory, dependency.Path));
                var loadedDependency = LoadRecursive(dependencyPath, diagnostics);
                if (loadedDependency is not null)
                {
                    if (!string.IsNullOrWhiteSpace(dependency.Version) &&
                        !string.Equals(dependency.Version, loadedDependency.Version, StringComparison.Ordinal))
                    {
                        diagnostics.Report("FLX0614", $"package '{loadedDependency.Name}' version '{loadedDependency.Version}' does not match requested version '{dependency.Version}'.");
                    }

                    dependencies.Add(loadedDependency);
                }
                continue;
            }

            LoadBinaryDependency(dependency, rootDirectory, manifestPath, diagnostics);
        }

        var sources = ExpandSources(manifest.Sources, rootDirectory, manifestPath, diagnostics);
        var includeDirs = manifest.CIncludeDirs
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(dir => Path.GetFullPath(Path.Combine(rootDirectory, dir)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var package = new LoadedPackage(
            manifest.Name,
            manifest.Version,
            manifest.Type,
            manifestPath,
            rootDirectory,
            sources,
            includeDirs,
            manifest.CLibraries.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            manifest.Defines.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        if ((_packagesByName.TryGetValue(package.Name, out var existingName) &&
             !existingName.ManifestPath.Equals(package.ManifestPath, StringComparison.OrdinalIgnoreCase)) ||
            _binaryPackagesByName.ContainsKey(package.Name))
        {
            diagnostics.Report("FLX0506", $"duplicate package name '{package.Name}'.");
        }

        _packagesByPath.Add(package.ManifestPath, package);
        if (!_packagesByName.ContainsKey(package.Name))
            _packagesByName.Add(package.Name, package);

        _orderedPackages.Add(package);
        _loadStack.Pop();
        return package;
    }

    private LoadedBinaryPackage? LoadBinaryDependency(
        PackageDependencyManifest dependency,
        string rootDirectory,
        string manifestPath,
        DiagnosticBag diagnostics)
    {
        var metadataPath = Path.GetFullPath(Path.Combine(rootDirectory, dependency.Metadata));
        if (_binaryPackagesByMetadataPath.TryGetValue(metadataPath, out var existing))
            return existing;

        if (!File.Exists(metadataPath))
        {
            diagnostics.Report("FLX0508", $"binary package metadata not found: {metadataPath}");
            return null;
        }

        var metadata = PackageMetadataReader.Read(metadataPath, out var error);
        if (metadata is null)
        {
            diagnostics.Report("FLX0508", $"failed to read binary package metadata '{metadataPath}': {error}");
            return null;
        }

        var declaredName = string.IsNullOrWhiteSpace(dependency.Name) ? metadata.Name : dependency.Name;
        if (!string.Equals(declaredName, metadata.Name, StringComparison.Ordinal))
        {
            diagnostics.Report("FLX0611", $"binary package dependency '{declaredName}' references metadata for package '{metadata.Name}'.");
        }

        if (!string.Equals(metadata.RuntimeAbi, PackageMetadata.CurrentRuntimeAbi, StringComparison.Ordinal))
        {
            diagnostics.Report("FLX0610", $"binary package '{metadata.Name}' was built with incompatible FLX runtime ABI '{metadata.RuntimeAbi}'. Expected '{PackageMetadata.CurrentRuntimeAbi}'.");
        }

        if (!string.IsNullOrWhiteSpace(dependency.Version) &&
            !string.Equals(dependency.Version, metadata.Version, StringComparison.Ordinal))
        {
            diagnostics.Report("FLX0614", $"binary package '{metadata.Name}' version '{metadata.Version}' does not match requested version '{dependency.Version}'.");
        }

        var includeDirs = dependency.IncludeDirs
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Select(dir => Path.GetFullPath(Path.Combine(rootDirectory, dir)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var libraries = dependency.Libraries
            .Where(library => !string.IsNullOrWhiteSpace(library))
            .Select(library => Path.GetFullPath(Path.Combine(rootDirectory, library)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var header in metadata.Headers.Where(header => !string.IsNullOrWhiteSpace(header)))
        {
            if (_validateBinaryArtifacts && !HeaderExists(header, includeDirs))
                diagnostics.Report("FLX0612", $"binary package '{metadata.Name}' references missing public header '{header}'.");
        }

        foreach (var library in libraries)
        {
            if (_validateBinaryArtifacts && !File.Exists(library))
                diagnostics.Report("FLX0613", $"binary package '{metadata.Name}' references missing library '{library}'.");
        }

        var binaryPackage = new LoadedBinaryPackage(metadata.Name, metadata.Version, metadataPath, includeDirs, libraries, metadata);

        if (_packagesByName.ContainsKey(binaryPackage.Name) ||
            (_binaryPackagesByName.TryGetValue(binaryPackage.Name, out var existingName) &&
             !existingName.MetadataPath.Equals(binaryPackage.MetadataPath, StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Report("FLX0506", $"duplicate package name '{binaryPackage.Name}'.");
        }

        _binaryPackagesByMetadataPath.Add(binaryPackage.MetadataPath, binaryPackage);
        if (!_binaryPackagesByName.ContainsKey(binaryPackage.Name))
            _binaryPackagesByName.Add(binaryPackage.Name, binaryPackage);

        _binaryPackages.Add(binaryPackage);
        return binaryPackage;
    }

    private static PackageManifest? ReadManifest(string manifestPath, DiagnosticBag diagnostics)
    {
        try
        {
            return JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (Exception ex)
        {
            diagnostics.Report("FLX0500", $"failed to read package manifest '{manifestPath}': {ex.Message}");
            return null;
        }
    }

    private static bool HeaderExists(string header, IReadOnlyList<string> includeDirs)
    {
        if (Path.IsPathRooted(header) && File.Exists(header))
            return true;

        return includeDirs.Any(includeDir => File.Exists(Path.Combine(includeDir, header)));
    }

    private static void ValidateManifest(PackageManifest manifest, string manifestPath, DiagnosticBag diagnostics)
    {
        if (string.IsNullOrWhiteSpace(manifest.Name))
            diagnostics.Report("FLX0500", $"package manifest '{manifestPath}' is missing required field 'name'.");

        if (!manifest.Type.Equals("exe", StringComparison.OrdinalIgnoreCase) &&
            !manifest.Type.Equals("library", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Report("FLX0500", $"package '{manifest.Name}' has unknown type '{manifest.Type}'. Expected 'exe' or 'library'.");
        }

        if (manifest.Sources.Count == 0)
            diagnostics.Report("FLX0500", $"package '{manifest.Name}' must list at least one source pattern.");
    }

    private static IReadOnlyList<string> ExpandSources(
        IReadOnlyList<string> patterns,
        string rootDirectory,
        string manifestPath,
        DiagnosticBag diagnostics)
    {
        var results = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in patterns.Where(pattern => !string.IsNullOrWhiteSpace(pattern)))
        {
            var normalizedPattern = NormalizePath(pattern);
            var searchRoot = DetermineSearchRoot(rootDirectory, normalizedPattern);
            if (!Directory.Exists(searchRoot))
                continue;

            var regex = GlobToRegex(normalizedPattern);
            foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                var relative = NormalizePath(Path.GetRelativePath(rootDirectory, file));
                if (regex.IsMatch(relative))
                    results.Add(Path.GetFullPath(file));
            }
        }

        if (results.Count == 0)
            diagnostics.Report("FLX0500", $"package manifest '{manifestPath}' did not match any source files.");

        return results.ToArray();
    }

    private static string DetermineSearchRoot(string rootDirectory, string normalizedPattern)
    {
        var wildcardIndex = normalizedPattern.IndexOf('*', StringComparison.Ordinal);
        if (wildcardIndex < 0)
        {
            var exact = Path.GetFullPath(Path.Combine(rootDirectory, normalizedPattern));
            return File.Exists(exact) ? Path.GetDirectoryName(exact)! : exact;
        }

        var prefix = normalizedPattern[..wildcardIndex];
        var lastSlash = prefix.LastIndexOf('/');
        var rootRelative = lastSlash < 0 ? "" : prefix[..lastSlash];
        return Path.GetFullPath(Path.Combine(rootDirectory, rootRelative));
    }

    private static Regex GlobToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var current = pattern[i];
            if (current == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }

                continue;
            }

            if (current == '?')
            {
                builder.Append("[^/]");
                continue;
            }

            builder.Append(Regex.Escape(current.ToString()));
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private string FormatCycle(string repeatedPath)
    {
        var paths = _loadStack.Reverse().Concat([repeatedPath]).ToArray();
        return string.Join(" -> ", paths.Select(path =>
            _manifestNamesByPath.TryGetValue(path, out var name) ? name : Path.GetFileNameWithoutExtension(path)));
    }
}
