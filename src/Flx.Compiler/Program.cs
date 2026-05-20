using Flx.Compiler.Build;
using Flx.Compiler.Cli;
using Flx.Compiler.Diagnostics;

var diagnostics = new DiagnosticBag();
var options = CommandLineParser.Parse(args, diagnostics);

if (options.ShowHelp)
{
    Console.Write(CommandLineOptions.HelpText);
    return diagnostics.HasErrors ? 1 : 0;
}

if (options.ShowVersion)
{
    Console.WriteLine("flxc 0.1.0");
    return diagnostics.HasErrors ? 1 : 0;
}

if (diagnostics.HasErrors)
{
    diagnostics.PrintTo(Console.Error);
    return 1;
}

var driver = new BuildDriver();
var exitCode = await driver.RunAsync(options, Console.Out, Console.Error);
return exitCode;
