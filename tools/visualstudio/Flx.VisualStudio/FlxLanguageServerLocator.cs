using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Flx.VisualStudio;

internal static class FlxLanguageServerLocator
{
    public static string? Resolve()
    {
        var extensionDirectory = Path.GetDirectoryName(typeof(FlxLanguageServerLocator).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(extensionDirectory))
        {
            var bundled = Path.Combine(extensionDirectory, "server-bundle", "flx-lsp.exe");
            if (File.Exists(bundled))
                return bundled;

            var repoLocal = FindRepoLocalServer(extensionDirectory);
            if (repoLocal is not null)
                return repoLocal;
        }

        return FindOnPath("flx-lsp.exe");
    }

    public static string ResolveLogPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "flx",
            "visualstudio");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "flx-lsp.log");
    }

    private static string? FindRepoLocalServer(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var project = Path.Combine(directory.FullName, "src", "Flx.LanguageServer", "Flx.LanguageServer.csproj");
            if (File.Exists(project))
            {
                var candidates = new[]
                {
                    Path.Combine(directory.FullName, "src", "Flx.LanguageServer", "bin", "Debug", "net10.0", "flx-lsp.exe"),
                    Path.Combine(directory.FullName, "src", "Flx.LanguageServer", "bin", "Release", "net10.0", "flx-lsp.exe")
                };

                return candidates.FirstOrDefault(File.Exists);
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var entry in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var candidate = Path.Combine(entry, executable);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
