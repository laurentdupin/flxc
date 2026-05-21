using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;
using Flx.Compiler.Packages;

namespace Flx.Compiler.Semantics;

internal static class ExportValidator
{
    public static void ValidateLibraryExports(
        PackageGraph? packageGraph,
        CompilationModel model,
        DiagnosticBag diagnostics)
    {
        if (packageGraph?.RootPackage.IsLibrary != true)
            return;

        var package = packageGraph.RootPackage;

        foreach (var global in model.GlobalsByFullName.Values.Where(global => IsOwnedByRootPackage(global.SourceFile, package) && global.IsExported))
            diagnostics.Report("FLX0600", $"exported global variable '{global.FullName}' is not implemented yet.", global.Location);

        foreach (var component in model.ComponentsByFullName.Values.Where(component => IsOwnedByRootPackage(component.SourceFile, package) && component.IsExported))
        {
            foreach (var field in component.Fields)
            {
                if (!IsExportableType(field.Type, model, out var hiddenType))
                {
                    diagnostics.Report(
                        "FLX0604",
                        $"exported component '{component.FullName}' uses non-exported field type '{hiddenType}'.",
                        field.Location);
                }
            }
        }

        foreach (var prefab in model.PrefabsByFullName.Values.Where(prefab => IsOwnedByRootPackage(prefab.SourceFile, package) && prefab.IsExported))
        {
            foreach (var component in prefab.FlattenedComponents)
            {
                if (!component.IsExported)
                {
                    diagnostics.Report(
                        "FLX0601",
                        $"exported prefab '{prefab.FullName}' uses non-exported component '{component.FullName}'.",
                        prefab.Syntax.Location);
                }
            }
        }

        foreach (var function in model.FunctionRegistry.AllFunctions.Where(function => IsOwnedByRootPackage(function.SourceFile, package) && function.IsExported))
        {
            if (!IsExportableType(function.ReturnType, model, out var hiddenReturnType))
            {
                diagnostics.Report(
                    "FLX0602",
                    $"exported function '{function.FullName}' uses non-exported return type '{hiddenReturnType}'.",
                    function.Location);
            }

            foreach (var parameter in function.Parameters)
            {
                if (!IsExportableType(parameter.Type, model, out var hiddenParameterType))
                {
                    diagnostics.Report(
                        "FLX0602",
                        $"exported function '{function.FullName}' uses non-exported parameter type '{hiddenParameterType}'.",
                        parameter.Location);
                }
            }
        }
    }

    internal static bool IsOwnedByRootPackage(SourceFile sourceFile, LoadedPackage package)
    {
        return string.Equals(sourceFile.PackageName, package.Name, StringComparison.Ordinal);
    }

    private static bool IsExportableType(string typeName, CompilationModel model, out string hiddenType)
    {
        hiddenType = typeName;

        if (typeName is "void" or "i32" or "usize" or "string" or "Array<string>")
            return true;

        if (model.ComponentsByFullName.TryGetValue(typeName, out var component))
            return component.IsExported;

        if (model.PrefabsByFullName.TryGetValue(typeName, out var prefab))
            return prefab.IsExported;

        return true;
    }
}
