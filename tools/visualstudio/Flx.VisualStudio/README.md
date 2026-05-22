# FLX Visual Studio Extension

This is the Visual Studio editor extension for FLX. It keeps the existing TextMate editor support and starts the shared `flx-lsp` language server for semantic features.

It provides:

- TextMate syntax coloring for `.flx` files
- line and block comment configuration
- bracket matching
- auto-closing pairs
- basic indentation rules
- diagnostics through `flx-lsp`
- document symbols through `flx-lsp`
- hover through `flx-lsp`
- go to definition through `flx-lsp`
- completion through `flx-lsp`

The grammar and language configuration are shared with the VS Code extension from:

```text
tools\grammar\
```

The semantic features use the same language server as VS Code:

```text
src\Flx.LanguageServer\
```

Build:

```powershell
dotnet build .\Flx.VisualStudio.sln -c Debug --no-restore
```

The build publishes `flx-lsp` into `server-bundle\` and creates:

```text
bin\Debug\net472\Flx.VisualStudio.vsix
```

Install the generated VSIX into an experimental Visual Studio instance first. After installation, open one of the example C++ projects under `examples\visualstudio` and open a `.flx` file.

Server logs are written to:

```text
%LOCALAPPDATA%\flx\visualstudio\flx-lsp.log
```

Limitations:

- no rename
- no find references
- no formatting
- no FLX debugger or expression evaluator
