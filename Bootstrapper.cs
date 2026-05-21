using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using MdModManager.Services;

namespace MdModManager;

internal static class Bootstrapper
{
    private const string PipeName = "MuseDashTOOL-DeepLink";
    private static readonly CancellationTokenSource DeepLinkPipeCts = new();

    internal static void StartDeepLinkPipeServer()
    {
        _ = ListenForDeepLinkPipeAsync(DeepLinkPipeCts.Token);
    }

    internal static void StopDeepLinkPipeServer()
    {
        DeepLinkPipeCts.Cancel();
        DeepLinkPipeCts.Dispose();
    }

    internal static void SendArgsToPrimaryInstance(string[] args)
    {
        try
        {
            var uri = args[0];
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var writer = new StreamWriter(client);
            writer.Write(uri);
            writer.Flush();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }

    private static async Task ListenForDeepLinkPipeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                
                await using (server.ConfigureAwait(false))
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    var uri = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(uri))
                    {
                        Dispatcher.UIThread.Post(() => Ioc.Default.GetRequiredService<DeepLinkService>().HandleUri(uri));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}
