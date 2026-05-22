namespace Flx.Compiler.Metadata;

internal sealed class PackageMetadata
{
    public const string CurrentRuntimeAbi = "flx-runtime-v1";
    public const string CurrentCompilerVersion = "0.0.1";

    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Type { get; set; } = "library";
    public string FlxCompilerVersion { get; set; } = "";
    public string RuntimeAbi { get; set; } = "";
    public string AbiHash { get; set; } = "";
    public string SourceRoot { get; set; } = "";
    public List<string> Headers { get; set; } = [];
    public PackageSymbolsMetadata Symbols { get; set; } = new();
    public List<string> HiddenSymbols { get; set; } = [];
}

internal sealed class PackageSymbolsMetadata
{
    public List<ComponentMetadata> Components { get; set; } = [];
    public List<PrefabMetadata> Prefabs { get; set; } = [];
    public List<FunctionMetadata> Functions { get; set; } = [];
}
