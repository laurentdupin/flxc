namespace Flx.Compiler.Metadata;

internal sealed class PackageMetadata
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "library";
    public List<string> Headers { get; set; } = [];
    public PackageSymbolsMetadata Symbols { get; set; } = new();
}

internal sealed class PackageSymbolsMetadata
{
    public List<ComponentMetadata> Components { get; set; } = [];
    public List<PrefabMetadata> Prefabs { get; set; } = [];
    public List<FunctionMetadata> Functions { get; set; } = [];
}
