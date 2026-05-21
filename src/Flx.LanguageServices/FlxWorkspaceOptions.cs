namespace Flx.LanguageServices;

public sealed class FlxWorkspaceOptions
{
    public bool RequireSchedule { get; init; }
    public bool ValidateScheduleTargets { get; init; } = true;

    /// <summary>
    /// Tools usually only need binary package metadata. The compiler build path keeps artifact validation enabled.
    /// </summary>
    public bool ValidateBinaryArtifacts { get; init; }
}
