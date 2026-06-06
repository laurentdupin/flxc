import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind
} from "vscode-languageclient/node";

let client: LanguageClient | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const config = vscode.workspace.getConfiguration("flx");
  const configuredPath = config.get<string>("languageServer.path") ?? "";
  const configuredLogPath = config.get<string>("languageServer.log") ?? "";
  const logPath = resolveLogPath(context, configuredLogPath);
  const serverPath = resolveServerPath(context, configuredPath);

  if (!serverPath) {
    vscode.window.showWarningMessage(
      "FLX language server not found. Build src/Flx.LanguageServer or set flx.languageServer.path."
    );
    return;
  }

  const args = ["--stdio"];
  args.push("--log", logPath);

  const serverOptions: ServerOptions = {
    run: {
      command: serverPath,
      args,
      transport: TransportKind.stdio
    },
    debug: {
      command: serverPath,
      args,
      transport: TransportKind.stdio
    }
  };

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: "file", language: "flx" }],
    synchronize: {
      fileEvents: vscode.workspace.createFileSystemWatcher("**/flx.package.json")
    }
  };

  client = new LanguageClient(
    "flxLanguageServer",
    "FLX Language Server",
    serverOptions,
    clientOptions
  );

  context.subscriptions.push(client);
  await client.start();
}

export async function deactivate(): Promise<void> {
  await client?.stop();
}

function resolveServerPath(
  context: vscode.ExtensionContext,
  configuredPath: string
): string | undefined {
  const trimmedPath = configuredPath.trim();
  if (trimmedPath.length > 0) {
    if (!looksLikePath(trimmedPath)) {
      return trimmedPath;
    }

    for (const candidate of resolveConfiguredPathCandidates(context, trimmedPath)) {
      if (fileExists(candidate)) {
        return candidate;
      }
    }

    return undefined;
  }

  const candidates = [
    context.asAbsolutePath(path.join("server-bundle", "flx-lsp.exe")),
    context.asAbsolutePath(path.join("server-bundle", "flx-lsp")),
    path.resolve(context.extensionPath, "../../../src/Flx.LanguageServer/bin/Debug/net10.0/flx-lsp.exe"),
    path.resolve(context.extensionPath, "../../../src/Flx.LanguageServer/bin/Debug/net10.0/flx-lsp"),
    path.resolve(context.extensionPath, "../../../src/Flx.LanguageServer/bin/Release/net10.0/flx-lsp.exe"),
    path.resolve(context.extensionPath, "../../../src/Flx.LanguageServer/bin/Release/net10.0/flx-lsp")
  ];

  for (const candidate of candidates) {
    if (fileExists(candidate)) {
      return candidate;
    }
  }

  return findOnPath(process.platform === "win32" ? "flx-lsp.exe" : "flx-lsp");
}

function resolveLogPath(context: vscode.ExtensionContext, configuredPath: string): string {
  const trimmedPath = configuredPath.trim();
  if (trimmedPath.length > 0) {
    const resolvedPath = resolveWritableConfiguredPath(context, trimmedPath);
    fs.mkdirSync(path.dirname(resolvedPath), { recursive: true });
    return resolvedPath;
  }

  const directory = context.globalStorageUri.fsPath;
  fs.mkdirSync(directory, { recursive: true });
  return path.join(directory, "flx-lsp.log");
}

function fileExists(filePath: string): boolean {
  try {
    return fs.statSync(filePath).isFile();
  } catch {
    return false;
  }
}

function looksLikePath(value: string): boolean {
  return value.includes("/") || value.includes("\\") || path.isAbsolute(value);
}

function resolveConfiguredPathCandidates(
  context: vscode.ExtensionContext,
  configuredPath: string
): string[] {
  const expandedPath = expandConfiguredPath(context, configuredPath);
  if (path.isAbsolute(expandedPath)) {
    return [expandedPath];
  }

  const candidates: string[] = [];
  for (const workspaceFolder of vscode.workspace.workspaceFolders ?? []) {
    candidates.push(path.resolve(workspaceFolder.uri.fsPath, expandedPath));
  }

  candidates.push(context.asAbsolutePath(expandedPath));
  return Array.from(new Set(candidates));
}

function resolveWritableConfiguredPath(
  context: vscode.ExtensionContext,
  configuredPath: string
): string {
  const expandedPath = expandConfiguredPath(context, configuredPath);
  if (path.isAbsolute(expandedPath)) {
    return expandedPath;
  }

  const workspaceFolder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  return path.resolve(workspaceFolder ?? context.globalStorageUri.fsPath, expandedPath);
}

function expandConfiguredPath(context: vscode.ExtensionContext, configuredPath: string): string {
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  return configuredPath
    .replace(/\$\{workspaceFolder\}/g, workspaceFolder ?? "")
    .replace(/\$\{extensionPath\}/g, context.extensionPath);
}

function findOnPath(executable: string): string | undefined {
  const pathEnv = process.env.PATH ?? "";
  const delimiter = process.platform === "win32" ? ";" : ":";
  const extensions = process.platform === "win32"
    ? (process.env.PATHEXT ?? ".EXE;.CMD;.BAT").split(";")
    : [""];

  for (const entry of pathEnv.split(delimiter)) {
    if (entry.length === 0) {
      continue;
    }

    const direct = path.join(entry, executable);
    if (fileExists(direct)) {
      return direct;
    }

    if (path.extname(executable).length > 0) {
      continue;
    }

    for (const extension of extensions) {
      const candidate = path.join(entry, executable + extension.toLowerCase());
      if (fileExists(candidate)) {
        return candidate;
      }
    }
  }

  return undefined;
}
