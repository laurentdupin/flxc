using System.Text.Json.Serialization;
using Flx.Compiler.Metadata;

namespace Flx.Compiler.Packages;

internal sealed class PackageManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "exe";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("sources")]
    public List<string> Sources { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public List<PackageDependencyManifest> Dependencies { get; set; } = [];

    [JsonPropertyName("cIncludeDirs")]
    public List<string> CIncludeDirs { get; set; } = [];

    [JsonPropertyName("cLibraries")]
    public List<string> CLibraries { get; set; } = [];

    [JsonPropertyName("defines")]
    public List<string> Defines { get; set; } = [];
}

internal sealed class PackageDependencyManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("metadata")]
    public string Metadata { get; set; } = "";

    [JsonPropertyName("includeDirs")]
    public List<string> IncludeDirs { get; set; } = [];

    [JsonPropertyName("libraries")]
    public List<string> Libraries { get; set; } = [];

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

internal sealed class PackageGraph
{
    public PackageGraph(
        LoadedPackage rootPackage,
        IReadOnlyList<LoadedPackage> packages,
        IReadOnlyList<LoadedBinaryPackage> binaryPackages)
    {
        RootPackage = rootPackage;
        Packages = packages;
        BinaryPackages = binaryPackages;
    }

    public LoadedPackage RootPackage { get; }
    public IReadOnlyList<LoadedPackage> Packages { get; }
    public IReadOnlyList<LoadedBinaryPackage> BinaryPackages { get; }

    public IEnumerable<LoadedPackage> SourceOrder => Packages;
}

internal sealed class LoadedBinaryPackage
{
    public LoadedBinaryPackage(
        string name,
        string version,
        string metadataPath,
        IReadOnlyList<string> includeDirs,
        IReadOnlyList<string> libraries,
        PackageMetadata metadata)
    {
        Name = name;
        Version = version;
        MetadataPath = metadataPath;
        IncludeDirs = includeDirs;
        Libraries = libraries;
        Metadata = metadata;
    }

    public string Name { get; }
    public string Version { get; }
    public string MetadataPath { get; }
    public IReadOnlyList<string> IncludeDirs { get; }
    public IReadOnlyList<string> Libraries { get; }
    public PackageMetadata Metadata { get; }
}

internal sealed class LoadedPackage
{
    public LoadedPackage(
        string name,
        string version,
        string type,
        string manifestPath,
        string rootDirectory,
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string> cIncludeDirs,
        IReadOnlyList<string> cLibraries,
        IReadOnlyList<string> defines)
    {
        Name = name;
        Version = version;
        Type = type;
        ManifestPath = manifestPath;
        RootDirectory = rootDirectory;
        SourcePaths = sourcePaths;
        CIncludeDirs = cIncludeDirs;
        CLibraries = cLibraries;
        Defines = defines;
    }

    public string Name { get; }
    public string Version { get; }
    public string Type { get; }
    public string ManifestPath { get; }
    public string RootDirectory { get; }
    public IReadOnlyList<string> SourcePaths { get; }
    public IReadOnlyList<string> CIncludeDirs { get; }
    public IReadOnlyList<string> CLibraries { get; }
    public IReadOnlyList<string> Defines { get; }
    public bool IsLibrary => Type.Equals("library", StringComparison.OrdinalIgnoreCase);
    public bool IsExecutable => Type.Equals("exe", StringComparison.OrdinalIgnoreCase);
}
