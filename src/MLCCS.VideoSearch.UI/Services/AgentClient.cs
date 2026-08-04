using System.IO.Pipes;
using System.Text.Json;
using MLCCS.VideoSearch.Core.Contracts;

namespace MLCCS.VideoSearch.UI.Services;

internal static class AgentClient
{
    private const string PipeName = "MLCCS.VideoSearch.Agent.v1";

    public static async Task<JsonElement> RequestAsync(string kind, object? payload = null,
        CancellationToken cancellationToken = default)
    {
        Exception? firstError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(kind == "update.apply" ? TimeSpan.FromHours(24) :
                    TimeSpan.FromSeconds(kind is "search.query" or "storage.summary" ? 120 : 10));
                await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(timeout.Token);
                var request = IpcEnvelope.Create(kind) with
                {
                    Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, JsonDefaults.Options)
                };
                await PipeFraming.WriteAsync(pipe, request, timeout.Token);
                var response = await PipeFraming.ReadAsync(pipe, timeout.Token);
                if (response.Error is not null)
                    throw new ProtocolException(response.Error.Code, response.Error.Summary);
                return response.Payload?.Clone() ?? JsonSerializer.SerializeToElement(new { });
            }
            catch (Exception error) when (attempt == 0 && !cancellationToken.IsCancellationRequested &&
                                          error is IOException or TimeoutException or OperationCanceledException)
            {
                firstError = error;
                await Task.Delay(180, cancellationToken);
            }
        }
        throw new IOException($"Agent 请求重试失败：{firstError?.Message}", firstError);
    }
}
