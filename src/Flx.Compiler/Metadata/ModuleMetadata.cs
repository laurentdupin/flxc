namespace Flx.Compiler.Metadata;

internal sealed class ModuleMetadata
{
    public string Source { get; set; } = "";
    public string CFile { get; set; } = "";
    public List<FunctionMetadata> Functions { get; set; } = [];
    public List<CImportMetadata> CImports { get; set; } = [];
    public ScheduleMetadata? Schedule { get; set; }
}

internal sealed class FunctionMetadata
{
    public string SourceName { get; set; } = "";
    public string MangledName { get; set; } = "";
    public string ReturnType { get; set; } = "";
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

internal sealed class ScheduleMetadata
{
    public List<ScheduleStepMetadata> Steps { get; set; } = [];
}

internal sealed class ScheduleStepMetadata
{
    public string Kind { get; set; } = "run";
    public string Name { get; set; } = "";
}
