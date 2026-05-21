using System.Text;
using Flx.Compiler.Semantics;

namespace Flx.Compiler.Codegen.C;

internal static class CTypeNames
{
    public static string SafeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "_";

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if ((i == 0 && (char.IsLetter(c) || c == '_')) || (i > 0 && (char.IsLetterOrDigit(c) || c == '_')))
                builder.Append(c);
            else
                builder.Append('_');
        }

        return builder.ToString();
    }

    public static string MapType(string sourceType, CompilationModel? model, ModuleSymbol? module = null)
    {
        if (sourceType == "string")
            return "flx_string";

        if (sourceType == "Array<string>")
            return "flx_array_string";

        if (sourceType == "i32")
            return "int";

        if (sourceType == "usize")
            return "size_t";

        if (sourceType == "void")
            return "void";

        if (model is not null && model.PrefabsByFullName.ContainsKey(sourceType))
            return ViewType(sourceType);

        var dotIndex = sourceType.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex > 0)
        {
            var alias = sourceType[..dotIndex];
            var member = sourceType[(dotIndex + 1)..];
            if (module is null || module.CImportsByAlias.ContainsKey(alias))
                return member;

            return sourceType;
        }

        return sourceType;
    }

    public static string ComponentType(string componentName) => "flx_" + SafeIdentifier(componentName);
    public static string PrefabType(string prefabName) => "flx_" + SafeIdentifier(prefabName);
    public static string ViewType(string prefabName) => "flx_" + SafeIdentifier(prefabName) + "View";
    public static string CreateFunction(string prefabName) => "flx_world_create_" + SafeIdentifier(prefabName);
    public static string GetFunction(string prefabName) => "flx_world_get_" + SafeIdentifier(prefabName);
    public static string CountField(string prefabName) => SafeIdentifier(prefabName).ToLowerInvariant() + "_count";
    public static string CapacityField(string prefabName) => SafeIdentifier(prefabName).ToLowerInvariant() + "_capacity";
    public static string StorageField(string prefabName) => SafeIdentifier(prefabName).ToLowerInvariant() + "s";

    public static string FormatFunctionParameters(FunctionSymbol function, CompilationModel model)
    {
        var parameters = function.Parameters.Select(parameter => $"{MapType(parameter.Type, model, function.Module)} {parameter.Name}").ToList();

        if (function.NeedsWorld)
            parameters.Insert(0, "flx_world *world");

        return parameters.Count == 0 ? "void" : string.Join(", ", parameters);
    }

    public static string FormatExternPrototype(FunctionSymbol function, CompilationModel model)
    {
        return $"extern {MapType(function.ReturnType, model, function.Module)} {function.MangledName}({FormatFunctionParameters(function, model)});";
    }
}
