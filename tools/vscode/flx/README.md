# FLX Language Support for VS Code

This is a syntax-only VS Code extension for FLX. It intentionally does not start an LSP server yet.

It contributes:

- `.flx` language detection
- TextMate syntax coloring
- line and block comments
- bracket matching
- auto-closing pairs
- basic indentation rules

The grammar and language configuration are copied from the shared source files in:

```text
tools/grammar/
```

Keep `language-configuration.json` and `syntaxes/flx.tmLanguage.json` synchronized with those shared files when the grammar changes.

## Test Locally

Open this folder in VS Code:

```powershell
code tools\vscode\flx
```

Press F5 to launch an Extension Development Host, then open a `.flx` file from the repo.

## Limitations

- no LSP
- no semantic diagnostics while typing
- no completion
- no hover
- no go to definition
