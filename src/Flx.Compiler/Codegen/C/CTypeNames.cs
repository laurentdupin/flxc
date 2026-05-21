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

    public static string MapType(string sourceType, CompilationModel model)
    {
        if (sourceType == "string")
            return "flx_string";

        if (sourceType is "usize" or "i32" or "void")
            return sourceType;

        if (model.PrefabsByName.ContainsKey(sourceType))
            return ViewType(sourceType);

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
        var parameters = function.Parameters.Select(parameter => $"{MapType(parameter.Type, model)} {parameter.Name}").ToList();

        if (function.NeedsWorld)
            parameters.Insert(0, "flx_world *world");

        return parameters.Count == 0 ? "void" : string.Join(", ", parameters);
    }

    public static string FormatExternPrototype(FunctionSymbol function, CompilationModel model)
    {
        return $"extern {MapType(function.ReturnType, model)} {function.MangledName}({FormatFunctionParameters(function, model)});";
    }
}
