using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flx.Compiler.Packages;
using Flx.Compiler.Semantics;

namespace Flx.Compiler.Metadata;

internal static class PackageMetadataWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static async Task WriteAsync(
        LoadedPackage package,
        CompilationModel model,
        IReadOnlyList<string> publicHeaders,
        string metadataPath)
    {
        var metadata = new PackageMetadata
        {
            Name = package.Name,
            Type = package.Type,
            Headers = publicHeaders.ToList(),
            Symbols = new PackageSymbolsMetadata
            {
                Components = model.ComponentsByFullName.Values
                    .Where(component => IsOwnedBy(component.SourceFile, package))
                    .OrderBy(component => component.FullName, StringComparer.Ordinal)
                    .Select(component => new ComponentMetadata
                    {
                        Name = component.Name,
                        FullName = component.FullName,
                        Fields = component.Fields.Select(field => new ComponentFieldMetadata
                        {
                            Type = field.Type,
                            Name = field.Name,
                            DefaultValue = field.DefaultValue
                        }).ToList()
                    }).ToList(),
                Prefabs = model.PrefabsByFullName.Values
                    .Where(prefab => IsOwnedBy(prefab.SourceFile, package))
                    .OrderBy(prefab => prefab.FullName, StringComparer.Ordinal)
                    .Select(prefab => new PrefabMetadata
                    {
                        Name = prefab.Name,
                        FullName = prefab.FullName,
                        FlattenedComponents = prefab.FlattenedComponents.Select(component => component.FullName).ToList()
                    }).ToList(),
                Functions = model.FunctionRegistry.AllFunctions
                    .Where(function => IsOwnedBy(function.SourceFile, package))
                    .OrderBy(function => function.FullName, StringComparer.Ordinal)
                    .ThenBy(function => function.MangledName, StringComparer.Ordinal)
                    .Select(function => new FunctionMetadata
                    {
                        SourceName = function.SourceName,
                        FullName = function.FullName,
                        MangledName = function.MangledName,
                        ReturnType = function.ReturnType,
                        ReceiverType = function.ReceiverType,
                        Parameters = function.Parameters.Select(parameter => new ParameterMetadata
                        {
                            Type = parameter.Type,
                            Name = parameter.Name
                        }).ToList(),
                        Line = function.Location.Line,
                        Column = function.Location.Column
                    }).ToList()
            }
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(metadataPath))!);
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));
    }

    private static bool IsOwnedBy(Flx.Compiler.Frontend.SourceFile sourceFile, LoadedPackage package)
    {
        return string.Equals(sourceFile.PackageName, package.Name, StringComparison.Ordinal);
    }
}
