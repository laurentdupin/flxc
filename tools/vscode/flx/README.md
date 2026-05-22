# FLX Language Support for VS Code

This extension provides syntax highlighting and starts `flx-lsp` for FLX language features.

It contributes:

- `.flx` language detection
- TextMate syntax coloring
- line and block comments
- bracket matching
- auto-closing pairs
- basic indentation rules
- diagnostics through `flx-lsp`
- document symbols through `flx-lsp`
- hover through `flx-lsp`
- go to definition through `flx-lsp`
- completion through `flx-lsp`

The grammar and language configuration are copied from the shared source files in:

```text
tools/grammar/
```

Keep `language-configuration.json` and `syntaxes/flx.tmLanguage.json` synchronized with those shared files when the grammar changes.

## Test Locally

Build the language server from the repository root:

```powershell
dotnet build src\Flx.LanguageServer\Flx.LanguageServer.csproj
```

Install extension dependencies and compile the TypeScript client:

```powershell
cd tools\vscode\flx
npm install
npm run compile
```

Open this folder in VS Code:

```powershell
code tools\vscode\flx
```

Press F5 to launch an Extension Development Host, then open a `.flx` file from the repo.

## Build Installable VSIX

From this folder:

```powershell
npm install
npm run package
```

This publishes `flx-lsp` into `server-bundle/` and creates:

```text
flx-language-0.3.0.vsix
```

Install it in VS Code:

```powershell
code --install-extension .\flx-language-0.3.0.vsix
```

After that, opening `.flx` files should use the packaged language server automatically.

If the extension cannot find the server, set:

```json
{
  "flx.languageServer.path": "C:\\path\\to\\flx-lsp.exe"
}
```

Optional server logging:

```json
{
  "flx.languageServer.log": "C:\\temp\\flx-lsp.log"
}
```

## Limitations

- no rename
- no find references
- no formatting
