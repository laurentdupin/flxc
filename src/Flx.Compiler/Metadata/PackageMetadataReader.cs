using System.Text.Json;

namespace Flx.Compiler.Metadata;

internal static class PackageMetadataReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static PackageMetadata? Read(string path, out string? error)
    {
        try
        {
            error = null;
            return JsonSerializer.Deserialize<PackageMetadata>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
