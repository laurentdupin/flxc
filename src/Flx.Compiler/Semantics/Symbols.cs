using Flx.Compiler.Diagnostics;
using Flx.Compiler.Frontend;
using System.Text.RegularExpressions;

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

internal sealed class ComponentFieldSymbol
{
    public ComponentFieldSymbol(string type, string name, string? defaultValue, SourceLocation location)
    {
        Type = type;
        Name = name;
        DefaultValue = defaultValue;
        Location = location;
    }

    public string Type { get; }
    public string Name { get; }
    public string? DefaultValue { get; }
    public SourceLocation Location { get; }
}

internal sealed class ComponentSymbol
{
    public ComponentSymbol(SourceFile sourceFile, ComponentDeclSyntax syntax, string name, IReadOnlyList<ComponentFieldSymbol> fields)
    {
        SourceFile = sourceFile;
        Syntax = syntax;
        Name = name;
        Fields = fields;
    }

    public SourceFile SourceFile { get; }
    public ComponentDeclSyntax Syntax { get; }
    public string Name { get; }
    public IReadOnlyList<ComponentFieldSymbol> Fields { get; }
}

internal sealed class PrefabFieldSymbol
{
    public PrefabFieldSymbol(ComponentSymbol component, ComponentFieldSymbol field)
    {
        Component = component;
        Field = field;
    }

    public ComponentSymbol Component { get; }
    public ComponentFieldSymbol Field { get; }
}

internal sealed class PrefabSymbol
{
    public PrefabSymbol(SourceFile sourceFile, PrefabDeclSyntax syntax, string name, IReadOnlyList<ComponentSymbol> flattenedComponents)
    {
        SourceFile = sourceFile;
        Syntax = syntax;
        Name = name;
        FlattenedComponents = flattenedComponents;
    }

    public SourceFile SourceFile { get; }
    public PrefabDeclSyntax Syntax { get; }
    public string Name { get; }
    public IReadOnlyList<ComponentSymbol> FlattenedComponents { get; }

    public IEnumerable<PrefabFieldSymbol> Fields =>
        FlattenedComponents.SelectMany(component => component.Fields.Select(componentField => new PrefabFieldSymbol(component, componentField)));
}

internal sealed class FunctionSymbol
{
    public FunctionSymbol(
        ModuleSymbol module,
        SourceFile sourceFile,
        FunctionDeclSyntax syntax,
        string sourceName,
        string mangledName,
        string returnType,
        IReadOnlyList<ParameterSymbol> parameters,
        SourceLocation location)
    {
        Module = module;
        SourceFile = sourceFile;
        Syntax = syntax;
        SourceName = sourceName;
        MangledName = mangledName;
        ReturnType = returnType;
        Parameters = parameters;
        Location = location;
    }

    public ModuleSymbol Module { get; }
    public SourceFile SourceFile { get; }
    public FunctionDeclSyntax Syntax { get; }
    public string SourceName { get; }
    public string MangledName { get; }
    public string ReturnType { get; }
    public IReadOnlyList<ParameterSymbol> Parameters { get; }
    public SourceLocation Location { get; }
    public bool NeedsWorld => Syntax.BodyText.Contains("create ", StringComparison.Ordinal);
}

internal sealed class GlobalVariableSymbol
{
    public GlobalVariableSymbol(
        ModuleSymbol module,
        SourceFile sourceFile,
        GlobalVariableDeclSyntax syntax,
        string type,
        string name,
        string? initializer,
        SourceLocation location)
    {
        Module = module;
        SourceFile = sourceFile;
        Syntax = syntax;
        Type = type;
        Name = name;
        Initializer = initializer;
        Location = location;
    }

    public ModuleSymbol Module { get; }
    public SourceFile SourceFile { get; }
    public GlobalVariableDeclSyntax Syntax { get; }
    public string Type { get; }
    public string Name { get; }
    public string? Initializer { get; }
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
    public List<ComponentSymbol> Components { get; } = [];
    public List<PrefabSymbol> Prefabs { get; } = [];
    public List<GlobalVariableSymbol> Globals { get; } = [];
    public List<FunctionSymbol> Functions { get; } = [];
}

internal sealed class CompilationModel
{
    public List<ModuleSymbol> Modules { get; } = [];
    public FunctionRegistry FunctionRegistry { get; } = new();
    public Dictionary<string, GlobalVariableSymbol> GlobalsByName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ComponentSymbol> ComponentsByName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PrefabSymbol> PrefabsByName { get; } = new(StringComparer.Ordinal);
    public List<ScheduleDeclSyntax> Schedules { get; } = [];
    public ScheduleDeclSyntax? Schedule => Schedules.Count == 1 ? Schedules[0] : null;
    public bool RequiresProgramArguments => FunctionRegistry.AllFunctions.Any(function =>
        ContainsProgramArgumentReference(function.Syntax.BodyText));
    public bool RequiresRuntime => ComponentsByName.Count > 0 ||
                                   PrefabsByName.Count > 0 ||
                                   RequiresProgramArguments ||
                                   FunctionRegistry.AllFunctions.Any(function => function.NeedsWorld ||
                                       function.Parameters.Any(parameter => PrefabsByName.ContainsKey(parameter.Type)) ||
                                       function.Syntax.BodyText.Contains("string", StringComparison.Ordinal) ||
                                       function.Syntax.BodyText.Contains("Array<", StringComparison.Ordinal) ||
                                       function.Syntax.BodyText.Contains("i32 ", StringComparison.Ordinal) ||
                                       function.Syntax.BodyText.Contains("usize ", StringComparison.Ordinal));
    public bool RequiresScheduleBreakSupport => Schedules.Any(schedule => schedule.Steps.OfType<LoopToStepSyntax>().Any()) ||
                                                FunctionRegistry.AllFunctions.Any(function => function.Syntax.BodyText.Contains("breakloop", StringComparison.Ordinal));

    private static bool ContainsProgramArgumentReference(string text)
    {
        return Regex.IsMatch(text, @"\b(argc|argv)\b");
    }
}
