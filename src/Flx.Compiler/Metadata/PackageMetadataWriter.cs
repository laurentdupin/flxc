using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
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
        var components = model.ComponentsByFullName.Values
            .Where(component => IsOwnedBy(component.SourceFile, package) && component.IsExported)
            .OrderBy(component => component.FullName, StringComparer.Ordinal)
            .Select(component => new ComponentMetadata
            {
                Source = RelativeSourcePath(component.SourceFile, package),
                Name = component.Name,
                FullName = component.FullName,
                Line = component.Syntax.NameLocation.Line,
                Column = component.Syntax.NameLocation.Column,
                Fields = component.Fields.Select(field => new ComponentFieldMetadata
                {
                    Type = field.Type,
                    Name = field.Name,
                    DefaultValue = field.DefaultValue
                }).ToList()
            }).ToList();

        var prefabs = model.PrefabsByFullName.Values
            .Where(prefab => IsOwnedBy(prefab.SourceFile, package) && prefab.IsExported)
            .OrderBy(prefab => prefab.FullName, StringComparer.Ordinal)
            .Select(prefab => new PrefabMetadata
            {
                Source = RelativeSourcePath(prefab.SourceFile, package),
                Name = prefab.Name,
                FullName = prefab.FullName,
                Line = prefab.Syntax.NameLocation.Line,
                Column = prefab.Syntax.NameLocation.Column,
                FlattenedComponents = prefab.FlattenedComponents.Select(component => component.FullName).ToList()
            }).ToList();

        var functions = model.FunctionRegistry.AllFunctions
            .Where(function => IsOwnedBy(function.SourceFile, package) && function.IsExported)
            .OrderBy(function => function.FullName, StringComparer.Ordinal)
            .ThenBy(function => function.MangledName, StringComparer.Ordinal)
            .Select(function => new FunctionMetadata
            {
                Source = RelativeSourcePath(function.SourceFile, package),
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
            }).ToList();

        var hiddenSymbols = model.ComponentsByFullName.Values
            .Where(component => IsOwnedBy(component.SourceFile, package) && !component.IsExported)
            .Select(component => component.FullName)
            .Concat(model.PrefabsByFullName.Values
                .Where(prefab => IsOwnedBy(prefab.SourceFile, package) && !prefab.IsExported)
                .Select(prefab => prefab.FullName))
            .Concat(model.FunctionRegistry.AllFunctions
                .Where(function => IsOwnedBy(function.SourceFile, package) && !function.IsExported)
                .Select(function => function.FullName))
            .Order(StringComparer.Ordinal)
            .ToList();

        var metadata = new PackageMetadata
        {
            Name = package.Name,
            Version = package.Version,
            Type = package.Type,
            FlxCompilerVersion = PackageMetadata.CurrentCompilerVersion,
            RuntimeAbi = PackageMetadata.CurrentRuntimeAbi,
            SourceRoot = package.RootDirectory,
            Headers = publicHeaders.ToList(),
            Symbols = new PackageSymbolsMetadata
            {
                Components = components,
                Prefabs = prefabs,
                Functions = functions
            },
            HiddenSymbols = hiddenSymbols
        };
        metadata.AbiHash = ComputeAbiHash(metadata);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(metadataPath))!);
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));
    }

    private static bool IsOwnedBy(Flx.Compiler.Frontend.SourceFile sourceFile, LoadedPackage package)
    {
        return string.Equals(sourceFile.PackageName, package.Name, StringComparison.Ordinal);
    }

    private static string RelativeSourcePath(Flx.Compiler.Frontend.SourceFile sourceFile, LoadedPackage package)
    {
        return Path.GetRelativePath(package.RootDirectory, sourceFile.FullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string ComputeAbiHash(PackageMetadata metadata)
    {
        var builder = new StringBuilder();
        builder.AppendLine(metadata.RuntimeAbi);

        foreach (var component in metadata.Symbols.Components.OrderBy(component => component.FullName, StringComparer.Ordinal))
        {
            builder.Append("component ").Append(component.FullName).AppendLine();
            foreach (var field in component.Fields.OrderBy(field => field.Name, StringComparer.Ordinal))
                builder.Append("  field ").Append(field.Type).Append(' ').Append(field.Name).AppendLine();
        }

        foreach (var prefab in metadata.Symbols.Prefabs.OrderBy(prefab => prefab.FullName, StringComparer.Ordinal))
        {
            builder.Append("prefab ").Append(prefab.FullName).AppendLine();
            foreach (var component in prefab.FlattenedComponents.Order(StringComparer.Ordinal))
                builder.Append("  flatten ").Append(component).AppendLine();
        }

        foreach (var function in metadata.Symbols.Functions.OrderBy(function => function.FullName, StringComparer.Ordinal).ThenBy(function => function.MangledName, StringComparer.Ordinal))
        {
            builder.Append("function ").Append(function.FullName)
                .Append(" -> ").Append(function.MangledName)
                .Append(" : ").Append(function.ReturnType)
                .AppendLine();
            if (!string.IsNullOrWhiteSpace(function.ReceiverType))
                builder.Append("  receiver ").Append(function.ReceiverType).AppendLine();

            foreach (var parameter in function.Parameters)
                builder.Append("  param ").Append(parameter.Type).Append(' ').Append(parameter.Name).AppendLine();
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
