namespace Flx.Compiler.Cli;

internal sealed class CommandLineOptions
{
    public List<string> InputFiles { get; } = [];
    public string? OutputPath { get; set; }
    public bool CompileOnly { get; set; }
    public bool EmitC { get; set; }
    public bool EmitPreprocessed { get; set; }
    public bool KeepC { get; set; }
    public bool KeepPreprocessed { get; set; }
    public bool NoPreprocess { get; set; }
    public bool Verbose { get; set; }
    public bool NoMain { get; set; }
    public bool ShowHelp { get; set; }
    public bool ShowVersion { get; set; }
    public string? ObjDir { get; set; }
    public string? CCompiler { get; set; }
    public string? CCompilerMode { get; set; }
    public List<string> IncludeDirs { get; } = [];
    public List<string> LibraryDirs { get; } = [];
    public List<string> Libraries { get; } = [];
    public List<string> Defines { get; } = [];
    public List<string> Undefines { get; } = [];
    public List<string> CFlags { get; } = [];
    public List<string> LdFlags { get; } = [];
    public List<string> PreprocessorFlags { get; } = [];

    public static string HelpText =>
        """
        flxc - small FLX-to-C compiler

        Usage:
          flxc [options] <input.flx>...

        Options:
          -o <path>           Output executable or object path
          -c                  Compile object only; do not generate main or link
          --emit-c            Generate C and metadata, then stop
          --emit-pp           Generate preprocessed FLX, then stop
          --keep-c            Keep generated C files after native build
          --keep-pp           Keep preprocessed FLX files after native build
          --no-preprocess     Disable FLX preprocessing
          --obj-dir <dir>     Intermediate/generated file directory
          --cc <compiler>     C compiler name or path, or auto
          --cc-mode <mode>    C compiler mode: gcc, clang, or msvc
          -I <dir>            C include directory
          -L <dir>            Library search directory
          -l <name>           Link library
          -D <macro>          C preprocessor define
          -U <macro>          C preprocessor undefine
          --cflag <flag>      Raw flag passed to C compilation
          --ldflag <flag>     Raw flag passed to linking
          --ppflag <flag>     Raw flag passed to FLX preprocessing
          --no-main           Allow builds without schedule-generated main
          --library           Alias for --no-main
          --verbose           Print generated native compiler commands
          --version           Print version
          -h, --help          Print this help

        Examples:
          flxc hello.flx -o hello
          flxc hello.flx --emit-c --obj-dir build/gen
          flxc -c hello.flx -o hello.o
        """;
}
