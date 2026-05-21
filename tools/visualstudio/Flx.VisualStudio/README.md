# FLX Visual Studio Extension

This is the first editor-only Visual Studio extension for FLX. It intentionally does not implement LSP or a custom project system.

It provides:

- TextMate syntax coloring for `.flx` files
- line and block comment configuration
- bracket matching
- auto-closing pairs
- basic indentation rules

The grammar and language configuration are shared with the VS Code extension from:

```text
tools\grammar\
```

Build:

```powershell
dotnet build .\Flx.VisualStudio.sln -c Debug
```

Install the generated VSIX into an experimental Visual Studio instance first. After installation, open one of the example C++ projects under `examples\visualstudio` and open `main.flx`.

Limitations:

- no IntelliSense
- no semantic diagnostics while typing
- no go to definition
- no rename
- no FLX debugger or expression evaluator
