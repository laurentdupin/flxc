using Flx.Compiler.Diagnostics;

namespace Flx.Compiler.Frontend;

internal sealed class CompilationUnitSyntax
{
    public CompilationUnitSyntax(SourceFile source)
    {
        Source = source;
    }

    public SourceFile Source { get; }
    public List<CImportSyntax> CImports { get; } = [];
    public List<ComponentDeclSyntax> Components { get; } = [];
    public List<PrefabDeclSyntax> Prefabs { get; } = [];
    public List<FunctionDeclSyntax> Functions { get; } = [];
    public List<ScheduleDeclSyntax> Schedules { get; } = [];
}

internal sealed class CImportSyntax
{
    public CImportSyntax(string header, string alias, SourceLocation location)
    {
        Header = header;
        Alias = alias;
        Location = location;
    }

    public string Header { get; }
    public string Alias { get; }
    public SourceLocation Location { get; }
}

internal sealed class FunctionDeclSyntax
{
    public FunctionDeclSyntax(
        string returnType,
        string name,
        IReadOnlyList<ParameterSyntax> parameters,
        string bodyText,
        int bodyStart,
        SourceLocation declarationLocation,
        SourceLocation nameLocation)
    {
        ReturnType = returnType;
        Name = name;
        Parameters = parameters;
        BodyText = bodyText;
        BodyStart = bodyStart;
        DeclarationLocation = declarationLocation;
        NameLocation = nameLocation;
    }

    public string ReturnType { get; }
    public string Name { get; }
    public IReadOnlyList<ParameterSyntax> Parameters { get; }
    public string BodyText { get; }
    public int BodyStart { get; }
    public SourceLocation DeclarationLocation { get; }
    public SourceLocation NameLocation { get; }
}

internal sealed class ComponentDeclSyntax
{
    public ComponentDeclSyntax(string name, string bodyText, int bodyStart, SourceLocation location, SourceLocation nameLocation)
    {
        Name = name;
        BodyText = bodyText;
        BodyStart = bodyStart;
        Location = location;
        NameLocation = nameLocation;
    }

    public string Name { get; }
    public string BodyText { get; }
    public int BodyStart { get; }
    public SourceLocation Location { get; }
    public SourceLocation NameLocation { get; }
}

internal sealed class PrefabDeclSyntax
{
    public PrefabDeclSyntax(string name, string bodyText, int bodyStart, SourceLocation location, SourceLocation nameLocation)
    {
        Name = name;
        BodyText = bodyText;
        BodyStart = bodyStart;
        Location = location;
        NameLocation = nameLocation;
    }

    public string Name { get; }
    public string BodyText { get; }
    public int BodyStart { get; }
    public SourceLocation Location { get; }
    public SourceLocation NameLocation { get; }
}

internal sealed class ParameterSyntax
{
    public ParameterSyntax(string type, string name, SourceLocation location)
    {
        Type = type;
        Name = name;
        Location = location;
    }

    public string Type { get; }
    public string Name { get; }
    public SourceLocation Location { get; }
}

internal sealed class ScheduleDeclSyntax
{
    public ScheduleDeclSyntax(IReadOnlyList<RunStepSyntax> steps, SourceLocation location)
    {
        Steps = steps;
        Location = location;
    }

    public IReadOnlyList<RunStepSyntax> Steps { get; }
    public SourceLocation Location { get; }
}

internal sealed class RunStepSyntax
{
    public RunStepSyntax(string name, SourceLocation location)
    {
        Name = name;
        Location = location;
    }

    public string Name { get; }
    public SourceLocation Location { get; }
}
