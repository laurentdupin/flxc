namespace Flx.Compiler.Preprocessing;

internal sealed class PreprocessorException : Exception
{
    public PreprocessorException(string message)
        : base(message)
    {
    }
}
