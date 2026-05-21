namespace Flx.Compiler.Preprocessing;

internal sealed class PreprocessorOptions
{
    public required string InputPath { get; init; }
    public required string InputText { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string CompilerPath { get; init; }
    public required string CompilerMode { get; init; }
    public IReadOnlyList<string> IncludeDirs { get; init; } = [];
    public IReadOnlyList<string> Defines { get; init; } = [];
    public IReadOnlyList<string> Undefines { get; init; } = [];
    public IReadOnlyList<string> RawPreprocessorFlags { get; init; } = [];
    public bool Verbose { get; init; }
    public bool KeepPreprocessed { get; init; }
    public string? OutputPath { get; init; }
}
