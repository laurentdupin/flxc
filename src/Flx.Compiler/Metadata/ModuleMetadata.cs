namespace Flx.Compiler.Metadata;

internal sealed class ModuleMetadata
{
    public string Source { get; set; } = "";
    public string? Package { get; set; }
    public string Module { get; set; } = "";
    public string CFile { get; set; } = "";
    public List<FunctionMetadata> Functions { get; set; } = [];
    public List<CImportMetadata> CImports { get; set; } = [];
    public List<ComponentMetadata> Components { get; set; } = [];
    public List<PrefabMetadata> Prefabs { get; set; } = [];
    public ScheduleMetadata? Schedule { get; set; }
}

internal sealed class FunctionMetadata
{
    public string SourceName { get; set; } = "";
    public string FullName { get; set; } = "";
    public string MangledName { get; set; } = "";
    public string ReturnType { get; set; } = "";
    public string? ReceiverType { get; set; }
    public List<ParameterMetadata> Parameters { get; set; } = [];
    public int Line { get; set; }
    public int Column { get; set; }
}

internal sealed class ParameterMetadata
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
}

internal sealed class CImportMetadata
{
    public string Header { get; set; } = "";
    public string Alias { get; set; } = "";
}

internal sealed class ComponentMetadata
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public List<ComponentFieldMetadata> Fields { get; set; } = [];
}

internal sealed class ComponentFieldMetadata
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DefaultValue { get; set; }
}

internal sealed class PrefabMetadata
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public List<string> FlattenedComponents { get; set; } = [];
}

internal sealed class ScheduleMetadata
{
    public List<ScheduleStepMetadata> Steps { get; set; } = [];
}

internal sealed class ScheduleStepMetadata
{
    public string Kind { get; set; } = "run";
    public string Name { get; set; } = "";
}
