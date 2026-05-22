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

internal sealed class ParallelExternalSymbol
{
    public ParallelExternalSymbol(string alias, string name, SourceLocation location)
    {
        Alias = alias;
        Name = name;
        Location = location;
    }

    public string Alias { get; }
    public string Name { get; }
    public string FullName => Alias + "." + Name;
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
    public ComponentSymbol(
        SourceFile sourceFile,
        ComponentDeclSyntax syntax,
        string name,
        string fullName,
        IReadOnlyList<ComponentFieldSymbol> fields,
        bool isExported = false)
    {
        SourceFile = sourceFile;
        Syntax = syntax;
        Name = name;
        FullName = fullName;
        Fields = fields;
        IsExported = isExported;
    }

    public SourceFile SourceFile { get; }
    public ComponentDeclSyntax Syntax { get; }
    public string Name { get; }
    public string FullName { get; }
    public IReadOnlyList<ComponentFieldSymbol> Fields { get; }
    public bool IsExported { get; }
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
    public PrefabSymbol(
        SourceFile sourceFile,
        PrefabDeclSyntax syntax,
        string name,
        string fullName,
        IReadOnlyList<ComponentSymbol> flattenedComponents,
        bool isExported = false)
    {
        SourceFile = sourceFile;
        Syntax = syntax;
        Name = name;
        FullName = fullName;
        FlattenedComponents = flattenedComponents;
        IsExported = isExported;
    }

    public SourceFile SourceFile { get; }
    public PrefabDeclSyntax Syntax { get; }
    public string Name { get; }
    public string FullName { get; }
    public IReadOnlyList<ComponentSymbol> FlattenedComponents { get; }
    public bool IsExported { get; }

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
        string fullName,
        string mangledName,
        string returnType,
        IReadOnlyList<ParameterSymbol> parameters,
        SourceLocation location,
        bool isExternal = false,
        bool isExported = false)
    {
        Module = module;
        SourceFile = sourceFile;
        Syntax = syntax;
        SourceName = sourceName;
        FullName = fullName;
        MangledName = mangledName;
        ReturnType = returnType;
        Parameters = parameters;
        Location = location;
        IsExternal = isExternal;
        IsExported = isExported;
    }

    public ModuleSymbol Module { get; }
    public SourceFile SourceFile { get; }
    public FunctionDeclSyntax Syntax { get; }
    public string SourceName { get; }
    public string FullName { get; }
    public string MangledName { get; }
    public string ReturnType { get; }
    public IReadOnlyList<ParameterSymbol> Parameters { get; }
    public SourceLocation Location { get; }
    public string? ReceiverType { get; set; }
    public bool IsExternal { get; }
    public bool IsExported { get; }
    public FunctionParallelInfo ParallelInfo { get; set; } = FunctionParallelInfo.Serial("not analyzed");
    public bool NeedsWorld => !IsExternal && Syntax.BodyText.Contains("create ", StringComparison.Ordinal);
}

internal sealed class FunctionParallelInfo
{
    private FunctionParallelInfo(bool canRunParallel, string? reasonIfNot)
    {
        CanRunParallel = canRunParallel;
        ReasonIfNot = reasonIfNot;
    }

    public bool CanRunParallel { get; }
    public string? ReasonIfNot { get; }

    public static FunctionParallelInfo Parallel() => new(true, null);
    public static FunctionParallelInfo Serial(string reason) => new(false, reason);
}

internal sealed class GlobalVariableSymbol
{
    public GlobalVariableSymbol(
        ModuleSymbol module,
        SourceFile sourceFile,
        GlobalVariableDeclSyntax syntax,
        string type,
        string name,
        string fullName,
        string? initializer,
        SourceLocation location,
        bool isExported = false)
    {
        Module = module;
        SourceFile = sourceFile;
        Syntax = syntax;
        Type = type;
        Name = name;
        FullName = fullName;
        Initializer = initializer;
        Location = location;
        IsExported = isExported;
    }

    public ModuleSymbol Module { get; }
    public SourceFile SourceFile { get; }
    public GlobalVariableDeclSyntax Syntax { get; }
    public string Type { get; }
    public string Name { get; }
    public string FullName { get; }
    public string? Initializer { get; }
    public SourceLocation Location { get; }
    public bool IsExported { get; }
}

internal sealed class ModuleSymbol
{
    public ModuleSymbol(SourceFile sourceFile, CompilationUnitSyntax syntax)
    {
        SourceFile = sourceFile;
        Syntax = syntax;
        Name = syntax.Module?.Name ?? "";
    }

    public SourceFile SourceFile { get; }
    public CompilationUnitSyntax Syntax { get; }
    public string Name { get; }
    public bool IsRoot => Name.Length == 0;
    public List<CImportSymbol> CImports { get; } = [];
    public Dictionary<string, CImportSymbol> CImportsByAlias { get; } = new(StringComparer.Ordinal);
    public List<ParallelExternalSymbol> ParallelExternalCalls { get; } = [];
    public Dictionary<string, ParallelExternalSymbol> ParallelExternalCallsByName { get; } = new(StringComparer.Ordinal);
    public List<ComponentSymbol> Components { get; } = [];
    public List<PrefabSymbol> Prefabs { get; } = [];
    public List<GlobalVariableSymbol> Globals { get; } = [];
    public List<FunctionSymbol> Functions { get; } = [];
}

internal sealed class CompilationModel
{
    public List<ModuleSymbol> Modules { get; } = [];
    public FunctionRegistry FunctionRegistry { get; } = new();
    public MethodRegistry MethodRegistry { get; } = new();
    public Dictionary<string, GlobalVariableSymbol> GlobalsByFullName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<GlobalVariableSymbol>> GlobalsByShortName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ComponentSymbol> ComponentsByFullName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<ComponentSymbol>> ComponentsByShortName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PrefabSymbol> PrefabsByFullName { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<PrefabSymbol>> PrefabsByShortName { get; } = new(StringComparer.Ordinal);
    public HashSet<string> HiddenExternalSymbols { get; } = new(StringComparer.Ordinal);
    public List<ScheduleDeclSyntax> Schedules { get; } = [];
    public List<string> ExternalHeaders { get; } = [];
    public ScheduleDeclSyntax? Schedule => Schedules.Count == 1 ? Schedules[0] : null;
    public ModuleSymbol? ScheduleModule => Schedule is null
        ? null
        : Modules.FirstOrDefault(module => module.Syntax.Schedules.Contains(Schedule));
    public bool RequiresProgramArguments => FunctionRegistry.AllFunctions.Any(function =>
        ContainsProgramArgumentReference(function.Syntax.BodyText));
    public bool RequiresRuntime => ComponentsByFullName.Count > 0 ||
                                   PrefabsByFullName.Count > 0 ||
                                   RequiresProgramArguments ||
                                   FunctionRegistry.AllFunctions.Any(function => function.NeedsWorld ||
                                       function.Parameters.Any(parameter => PrefabsByFullName.ContainsKey(parameter.Type)) ||
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

    public static string Qualify(string moduleName, string shortName)
    {
        return string.IsNullOrWhiteSpace(moduleName) ? shortName : moduleName + "." + shortName;
    }

    public ComponentSymbol? ResolveComponent(string name, ModuleSymbol module)
    {
        if (name.Contains('.', StringComparison.Ordinal))
            return ComponentsByFullName.TryGetValue(name, out var qualified) ? qualified : null;

        var currentFullName = Qualify(module.Name, name);
        if (ComponentsByFullName.TryGetValue(currentFullName, out var current))
            return current;

        return ComponentsByShortName.TryGetValue(name, out var matches) && matches.Count == 1 ? matches[0] : null;
    }

    public bool IsAmbiguousComponentName(string name, ModuleSymbol module)
    {
        if (name.Contains('.', StringComparison.Ordinal))
            return false;

        var currentFullName = Qualify(module.Name, name);
        if (ComponentsByFullName.ContainsKey(currentFullName))
            return false;

        return ComponentsByShortName.TryGetValue(name, out var matches) && matches.Count > 1;
    }

    public PrefabSymbol? ResolvePrefab(string name, ModuleSymbol module)
    {
        if (name.Contains('.', StringComparison.Ordinal))
            return PrefabsByFullName.TryGetValue(name, out var qualified) ? qualified : null;

        var currentFullName = Qualify(module.Name, name);
        if (PrefabsByFullName.TryGetValue(currentFullName, out var current))
            return current;

        return PrefabsByShortName.TryGetValue(name, out var matches) && matches.Count == 1 ? matches[0] : null;
    }

    public bool IsAmbiguousPrefabName(string name, ModuleSymbol module)
    {
        if (name.Contains('.', StringComparison.Ordinal))
            return false;

        var currentFullName = Qualify(module.Name, name);
        if (PrefabsByFullName.ContainsKey(currentFullName))
            return false;

        return PrefabsByShortName.TryGetValue(name, out var matches) && matches.Count > 1;
    }
}
