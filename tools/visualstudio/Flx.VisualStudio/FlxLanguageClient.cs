using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace Flx.VisualStudio;

[ContentType(FlxContentTypeDefinitions.ContentTypeName)]
[Export(typeof(ILanguageClient))]
public sealed class FlxLanguageClient : ILanguageClient
{
    private Process? _process;

    public string Name => "FLX Language Server";

    public IEnumerable<string>? ConfigurationSections => null;

    public object? InitializationOptions => null;

    public IEnumerable<string>? FilesToWatch => new[] { "**/flx.package.json" };

    public bool ShowNotificationOnInitializeFailed => true;

    public event AsyncEventHandler<EventArgs>? StartAsync;

#pragma warning disable CS0067
    public event AsyncEventHandler<EventArgs>? StopAsync;
#pragma warning restore CS0067

    public async Task OnLoadedAsync()
    {
        if (StartAsync is not null)
            await StartAsync.InvokeAsync(this, EventArgs.Empty);
    }

    public Task OnServerInitializedAsync()
    {
        return Task.CompletedTask;
    }

    public Task<InitializationFailureContext?> OnServerInitializeFailedAsync(
        ILanguageClientInitializationInfo initializationState)
    {
        return Task.FromResult<InitializationFailureContext?>(new InitializationFailureContext
        {
            FailureMessage = "FLX language server failed to initialize."
        });
    }

    public Task<Connection?> ActivateAsync(CancellationToken token)
    {
        var serverPath = FlxLanguageServerLocator.Resolve();
        if (serverPath is null)
            return Task.FromResult<Connection?>(null);

        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = $"--stdio --log \"{FlxLanguageServerLocator.ResolveLogPath()}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.ErrorDataReceived += (_, _) => { };

        if (!_process.Start())
            return Task.FromResult<Connection?>(null);

        _process.BeginErrorReadLine();
        return Task.FromResult<Connection?>(new Connection(
            _process.StandardOutput.BaseStream,
            _process.StandardInput.BaseStream));
    }
}
