using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace Flx.VisualStudio;

internal static class FlxContentTypeDefinitions
{
    public const string ContentTypeName = "flx";

    [Export]
    [Name(ContentTypeName)]
    [BaseDefinition("code")]
    [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    internal static ContentTypeDefinition FlxContentTypeDefinition = null!;

    [Export]
    [FileExtension(".flx")]
    [ContentType(ContentTypeName)]
    internal static FileExtensionToContentTypeDefinition FlxFileExtensionDefinition = null!;
}
