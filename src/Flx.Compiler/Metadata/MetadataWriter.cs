using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Flx.Compiler.Frontend;
using Flx.Compiler.Semantics;

namespace Flx.Compiler.Metadata;

internal static class MetadataWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static async Task WriteAsync(ModuleSymbol module, CompilationModel model, string cFilePath, string metadataPath)
    {
        var metadata = new ModuleMetadata
        {
            Source = module.SourceFile.DisplayPath,
            Package = module.SourceFile.PackageName,
            Module = module.Name,
            CFile = Path.GetFileName(cFilePath),
            Functions = module.Functions.Select(function => new FunctionMetadata
            {
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
                Parallelizable = function.ParallelInfo.CanRunParallel,
                ParallelReason = function.ParallelInfo.ReasonIfNot,
                Line = function.Location.Line,
                Column = function.Location.Column
            }).ToList(),
            CImports = module.CImports.Select(import => new CImportMetadata
            {
                Header = import.Header,
                Alias = import.Alias
            }).ToList(),
            Components = module.Components.Select(component => new ComponentMetadata
            {
                Name = component.Name,
                FullName = component.FullName,
                Fields = component.Fields.Select(field => new ComponentFieldMetadata
                {
                    Type = field.Type,
                    Name = field.Name,
                    DefaultValue = field.DefaultValue
                }).ToList()
            }).ToList(),
            Prefabs = module.Prefabs.Select(prefab => new PrefabMetadata
            {
                Name = prefab.Name,
                FullName = prefab.FullName,
                FlattenedComponents = prefab.FlattenedComponents.Select(component => component.FullName).ToList()
            }).ToList()
        };

        if (module.Syntax.Schedules.Count > 0)
        {
            metadata.Schedule = new ScheduleMetadata
            {
                Steps = module.Syntax.Schedules.SelectMany(schedule => schedule.Steps).Select(step => ToMetadataStep(step, model, module)).ToList()
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));
    }

    private static ScheduleStepMetadata ToMetadataStep(ScheduleStmtSyntax step, CompilationModel model, ModuleSymbol module)
    {
        return step switch
        {
            RunStepSyntax run => ToRunMetadataStep(run, model, module),
            LabelStepSyntax label => new ScheduleStepMetadata { Kind = "label", Name = label.Name },
            LoopToStepSyntax loopTo => new ScheduleStepMetadata { Kind = "loopto", Name = loopTo.TargetLabel },
            _ => new ScheduleStepMetadata { Kind = "unknown", Name = "" }
        };
    }

    private static ScheduleStepMetadata ToRunMetadataStep(RunStepSyntax run, CompilationModel model, ModuleSymbol module)
    {
        var resolution = ScheduleTargetResolver.Resolve(model, run, module);
        var execution = resolution.Functions.Count > 0 &&
                        resolution.Functions.All(function => function.ParallelInfo.CanRunParallel)
            ? "parallel"
            : "serial";
        return new ScheduleStepMetadata
        {
            Kind = "run",
            Name = run.Name,
            Target = run.Name,
            IsWildcard = run.Target.HasWildcard,
            ExpandedTargets = resolution.FunctionGroupFullNames.ToList(),
            Execution = execution
        };
    }
}
