namespace Flx.LanguageServices;

public readonly record struct FlxPosition(int Line, int Character);

public readonly record struct FlxRange(FlxPosition Start, FlxPosition End);
