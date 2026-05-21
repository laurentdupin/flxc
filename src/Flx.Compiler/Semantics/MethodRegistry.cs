namespace Flx.Compiler.Semantics;

internal sealed class MethodRegistry
{
    private readonly Dictionary<MethodSignatureKey, FunctionSymbol> _methodsBySignature = new();
    private readonly Dictionary<MethodLookupKey, List<FunctionSymbol>> _methodsByLookup = new();

    public IEnumerable<FunctionSymbol> AllMethods => _methodsBySignature.Values;

    public bool TryAdd(FunctionSymbol function, PrefabSymbol receiver, out FunctionSymbol? existing)
    {
        var remainingParameterTypes = function.Parameters.Skip(1).Select(parameter => parameter.Type).ToArray();
        var signatureKey = new MethodSignatureKey(receiver.FullName, function.SourceName, string.Join("\u001f", remainingParameterTypes));
        if (_methodsBySignature.TryGetValue(signatureKey, out existing))
            return false;

        _methodsBySignature.Add(signatureKey, function);

        var lookupKey = new MethodLookupKey(receiver.FullName, function.SourceName, remainingParameterTypes.Length);
        if (!_methodsByLookup.TryGetValue(lookupKey, out var methods))
        {
            methods = [];
            _methodsByLookup.Add(lookupKey, methods);
        }

        methods.Add(function);
        existing = null;
        return true;
    }

    public IReadOnlyList<FunctionSymbol> Resolve(string receiverFullName, string methodName, int argumentCount)
    {
        var key = new MethodLookupKey(receiverFullName, methodName, argumentCount);
        return _methodsByLookup.TryGetValue(key, out var methods) ? methods : [];
    }

    public string? GetReceiverType(FunctionSymbol function)
    {
        foreach (var (key, method) in _methodsBySignature)
        {
            if (ReferenceEquals(method, function))
                return key.ReceiverFullName;
        }

        return null;
    }

    private sealed record MethodSignatureKey(string ReceiverFullName, string MethodName, string RemainingParameterTypes);
    private sealed record MethodLookupKey(string ReceiverFullName, string MethodName, int ArgumentCount);
}
