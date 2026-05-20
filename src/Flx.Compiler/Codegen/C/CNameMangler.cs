using System.Security.Cryptography;
using System.Text;

namespace Flx.Compiler.Codegen.C;

internal static class CNameMangler
{
    public static string Mangle(string moduleName, string sourceName, IEnumerable<string> parameterTypes)
    {
        var hash = ModuleHash(moduleName);
        var safeName = SanitizeIdentifier(sourceName);
        var signature = SignatureCode(parameterTypes);
        return $"flx_f_{hash}_{safeName}__{signature}";
    }

    private static string ModuleHash(string moduleName)
    {
        var normalized = moduleName.Replace('\\', '/').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..6];
    }

    private static string SignatureCode(IEnumerable<string> parameterTypes)
    {
        var parts = parameterTypes.Select(SanitizeIdentifier).ToArray();
        return parts.Length == 0 ? "v" : string.Join("_", parts);
    }

    private static string SanitizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "_";

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if ((i == 0 && (char.IsLetter(c) || c == '_')) || (i > 0 && (char.IsLetterOrDigit(c) || c == '_')))
                builder.Append(c);
            else
                builder.Append('_');
        }

        return builder.ToString();
    }
}
