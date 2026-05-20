using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;

namespace Flx.Compiler.Semantics;

internal sealed class CImportSymbol
{
    public CImportSymbol(string header, string alias, SourceLocation location)
    {
        Header = header;
        Alias = alias;
        Location = location;
    }

    public string Header { get; }
    public string Alias { get; }
    public SourceLocation Location { get; }
}

internal sealed class ParameterSymbol
{
    public ParameterSymbol(string type, string name, SourceLocation location)
    {
        Type = type;
        Name = name;
        Location = location;
    }

    public string Type { get; }
    public string Name { get; }
    public SourceLocation Location { get; }
}

internal sealed class FunctionSymbol
{
    public FunctionSymbol(
        SourceFile sourceFile,
        FunctionDeclSyntax syntax,
        string sourceName,
        string mangledName,
        string returnType,
        IReadOnlyList<ParameterSymbol> parameters,
        SourceLocation location)
    {
        SourceFile = sourceFile;
        Syntax = syntax;
        SourceName = sourceName;
        MangledName = mangledName;
        ReturnType = returnType;
        Parameters = parameters;
        Location = location;
    }

    public SourceFile SourceFile { get; }
    public FunctionDeclSyntax Syntax { get; }
    public string SourceName { get; }
    public string MangledName { get; }
    public string ReturnType { get; }
    public IReadOnlyList<ParameterSymbol> Parameters { get; }
    public SourceLocation Location { get; }
}

internal sealed class ModuleSymbol
{
    public ModuleSymbol(SourceFile sourceFile, CompilationUnitSyntax syntax)
    {
        SourceFile = sourceFile;
        Syntax = syntax;
    }

    public SourceFile SourceFile { get; }
    public CompilationUnitSyntax Syntax { get; }
    public List<CImportSymbol> CImports { get; } = [];
    public Dictionary<string, CImportSymbol> CImportsByAlias { get; } = new(StringComparer.Ordinal);
    public List<FunctionSymbol> Functions { get; } = [];
}

internal sealed class CompilationModel
{
    public List<ModuleSymbol> Modules { get; } = [];
    public FunctionRegistry FunctionRegistry { get; } = new();
    public List<ScheduleDeclSyntax> Schedules { get; } = [];
    public ScheduleDeclSyntax? Schedule => Schedules.Count == 1 ? Schedules[0] : null;
}
