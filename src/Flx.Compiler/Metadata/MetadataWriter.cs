using System.Text.Json;
using System.Text.Json.Serialization;
using Flx.Compiler.Semantics;

namespace Flx.Compiler.Metadata;

internal static class MetadataWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static async Task WriteAsync(ModuleSymbol module, string cFilePath, string metadataPath)
    {
        var metadata = new ModuleMetadata
        {
            Source = module.SourceFile.DisplayPath,
            CFile = Path.GetFileName(cFilePath),
            Functions = module.Functions.Select(function => new FunctionMetadata
            {
                SourceName = function.SourceName,
                MangledName = function.MangledName,
                ReturnType = function.ReturnType,
                Parameters = function.Parameters.Select(parameter => new ParameterMetadata
                {
                    Type = parameter.Type,
                    Name = parameter.Name
                }).ToList(),
                Line = function.Location.Line,
                Column = function.Location.Column
            }).ToList(),
            CImports = module.CImports.Select(import => new CImportMetadata
            {
                Header = import.Header,
                Alias = import.Alias
            }).ToList()
        };

        if (module.Syntax.Schedules.Count > 0)
        {
            metadata.Schedule = new ScheduleMetadata
            {
                Steps = module.Syntax.Schedules.SelectMany(schedule => schedule.Steps).Select(step => new ScheduleStepMetadata
                {
                    Kind = "run",
                    Name = step.Name
                }).ToList()
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));
    }
}
