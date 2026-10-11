using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TuneUpEngine
{
    // The client side of the engine's pipe protocol (see IpcContracts). Used by the UI, and by the
    // tests, so both talk to the engine exactly the same way.
    public sealed class EngineClient
    {
        private readonly string _pipeName;
        private readonly IReadOnlyList<string> _trustedEnginePaths;

        // Why the last request couldn't reach the engine, in plain words (null after a success).
        // Lets the UI say more than "Engine Offline" — e.g. that a different copy of the engine is
        // running and must be closed first.
        public string? LastConnectionProblem { get; private set; }

        public EngineClient(IReadOnlyList<string> trustedEnginePaths, string pipeName = IpcServer.DefaultPipeName)
        {
            _trustedEnginePaths = trustedEnginePaths;
            _pipeName = pipeName;
        }

        // The usual client: trusts the engine next to this program (or the dev build's engine).
        public static EngineClient ForThisApplication() =>
            new EngineClient(ClientVerifier.TrustedEnginePaths(AppContext.BaseDirectory));

        // Opens the pipe. The connection lets the engine act as this user (impersonation) so the
        // user's settings and files are changed as them, not as SYSTEM. That's a real permission,
        // so before sending anything the client checks the pipe belongs to the genuine engine —
        // Windows only lets the server act as the client after reading from the pipe, so a fake
        // engine that grabbed the name never gets the chance.
        private NamedPipeClientStream Connect(int timeoutMs)
        {
            var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.None, TokenImpersonationLevel.Impersonation);
            try
            {
                client.Connect(timeoutMs);
                string? server = ClientVerifier.GetServerImagePath(client);
                if (!ClientVerifier.IsTrustedClient(server, _trustedEnginePaths))
                {
                    LastConnectionProblem = server != null
                        ? $"A different copy of the TuneUp engine is running ({server}). Close that copy of TuneUp (right-click its tray icon → Exit) and open this one again."
                        : "Another program is using the TuneUp engine's connection, so this copy can't start its engine.";
                    throw new UnauthorizedAccessException(LastConnectionProblem);
                }
                LastConnectionProblem = null;
                return client;
            }
            catch (TimeoutException)
            {
                LastConnectionProblem = "The TuneUp engine isn't running.";
                client.Dispose();
                throw;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        // One request, one reply. Returns null if the engine can't be reached. Only for commands
        // that don't report progress — use SendStreaming for those.
        public IpcResponse? Send(string command, string? payload = null, int timeoutMs = 3000)
        {
            try
            {
                using var client = Connect(timeoutMs);
                var writer = new StreamWriter(client, Encoding.UTF8);
                var reader = new StreamReader(client, Encoding.UTF8);

                writer.WriteLine(JsonSerializer.Serialize(new IpcRequest { Command = command, Payload = payload }));
                writer.Flush();

                string? line = reader.ReadLine();
                return string.IsNullOrEmpty(line) ? null : JsonSerializer.Deserialize<IpcResponse>(line);
            }
            catch
            {
                return null; // Engine offline, untrusted, or pipe closed
            }
        }

        // Typed payload (serialized to JSON). Deliberately NOT an overload of Send: a generic
        // Send<T>(string, T) also matched Send(command, "some string"), and C# picked it — the
        // string was then JSON-encoded again on every call to itself until it hit the 268 MB limit.
        public IpcResponse? SendJson<T>(string command, T payload, int timeoutMs = 3000) where T : class =>
            Send(command, IpcPayload.Serialize(payload), timeoutMs);

        // Sends a command that may stream Progress replies, and blocks until its final reply.
        // Cancelling `ct` asks the engine to stop (CancelRequest with this request's ID); the call
        // still returns the engine's final reply, which then says the operation was cancelled.
        // onProgress runs on the calling thread.
        public IpcResponse SendStreaming(string command, string? payload, Action<IpcResponse>? onProgress,
            CancellationToken ct = default, int timeoutMs = 5000)
        {
            string requestId = Guid.NewGuid().ToString("N");
            try
            {
                using var client = Connect(timeoutMs);
                var writer = new StreamWriter(client, Encoding.UTF8);
                var reader = new StreamReader(client, Encoding.UTF8);
                var writeLock = new object();

                lock (writeLock)
                {
                    writer.WriteLine(JsonSerializer.Serialize(new IpcRequest { Command = command, Payload = payload, RequestId = requestId }));
                    writer.Flush();
                }

                using var cancelRegistration = ct.Register(() =>
                {
                    lock (writeLock)
                    {
                        try
                        {
                            writer.WriteLine(JsonSerializer.Serialize(new IpcRequest { Command = IpcCommands.CancelRequest, Payload = requestId }));
                            writer.Flush();
                        }
                        catch
                        {
                            // Pipe already closed — the operation finished anyway.
                        }
                    }
                });

                while (client.IsConnected)
                {
                    string? line = reader.ReadLine();
                    if (string.IsNullOrEmpty(line)) break;

                    var response = JsonSerializer.Deserialize<IpcResponse>(line);
                    if (response == null) break;

                    if (response.Status == IpcStatus.Progress)
                        onProgress?.Invoke(response);
                    else
                        return response;
                }

                return new IpcResponse { Status = IpcStatus.Error, Message = "The engine closed the connection before the operation finished." };
            }
            catch (Exception ex)
            {
                return new IpcResponse { Status = IpcStatus.Error, Message = $"Failed to communicate with engine: {ex.Message}" };
            }
        }

        public IpcResponse SendStreamingJson<T>(string command, T payload, Action<IpcResponse>? onProgress, CancellationToken ct = default) where T : class =>
            SendStreaming(command, IpcPayload.Serialize(payload), onProgress, ct);
    }
}
