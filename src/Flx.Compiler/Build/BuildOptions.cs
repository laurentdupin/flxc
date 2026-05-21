namespace Flx.Compiler.Build;

internal sealed class BuildOptions
{
    public required string CompilerPath { get; init; }
    public required string CompilerMode { get; init; }
    public IReadOnlyList<string> IncludeDirs { get; init; } = [];
    public IReadOnlyList<string> LibraryDirs { get; init; } = [];
    public IReadOnlyList<string> Libraries { get; init; } = [];
    public IReadOnlyList<string> Defines { get; init; } = [];
    public IReadOnlyList<string> Undefines { get; init; } = [];
    public IReadOnlyList<string> CFlags { get; init; } = [];
    public IReadOnlyList<string> LdFlags { get; init; } = [];
    public bool Verbose { get; init; }
}
