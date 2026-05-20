namespace Flx.Compiler.Semantics;

internal sealed class FunctionRegistry
{
    private readonly Dictionary<string, List<FunctionSymbol>> _functionsBySourceName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionSymbol> _functionsByMangledName = new(StringComparer.Ordinal);

    public IEnumerable<FunctionSymbol> AllFunctions => _functionsByMangledName.Values;

    public bool TryAdd(FunctionSymbol function)
    {
        if (_functionsByMangledName.ContainsKey(function.MangledName))
            return false;

        _functionsByMangledName.Add(function.MangledName, function);

        if (!_functionsBySourceName.TryGetValue(function.SourceName, out var overloads))
        {
            overloads = [];
            _functionsBySourceName.Add(function.SourceName, overloads);
        }

        overloads.Add(function);
        return true;
    }

    public IReadOnlyList<FunctionSymbol> LookupSourceName(string sourceName)
    {
        if (_functionsBySourceName.TryGetValue(sourceName, out var overloads))
            return overloads;

        return [];
    }

    public bool ContainsExactSignature(string sourceName, IReadOnlyList<ParameterSymbol> parameters)
    {
        if (!_functionsBySourceName.TryGetValue(sourceName, out var overloads))
            return false;

        return overloads.Any(existing => SameParameterTypes(existing.Parameters, parameters));
    }

    private static bool SameParameterTypes(IReadOnlyList<ParameterSymbol> left, IReadOnlyList<ParameterSymbol> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Type, right[i].Type, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
