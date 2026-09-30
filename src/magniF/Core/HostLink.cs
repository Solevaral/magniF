// HostLink — серверная сторона протокола All-in-one (версия 1) для .NET-программ.
// Один файл без зависимостей: скопируйте его в свой проект и поменяйте namespace.
// Описание протокола — docs/MODULES.md в репозитории Solevaral/All-in-one.
//
//   var link = new HostLink(pipeName, appVersion, HandleAsync);
//   link.Start();                        // из UI-потока: обработчики будут вызываться в нём же
//   link.Publish("statusChanged", status);
//
// Обработчик получает имя метода и параметры и возвращает результат (сериализуется в JSON)
// или бросает HostLinkError. Методы hello обрабатываются здесь, остальные — программой.

using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace magniF.Core;

internal sealed class HostLink : IDisposable
{
    public const int ProtocolVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _pipeName;
    private readonly string _appVersion;
    private readonly Func<string, JsonNode?, Task<object?>> _handler;
    private readonly Func<IEnumerable<(string Id, string Title)>> _actions;
    private readonly SynchronizationContext? _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private StreamWriter? _writer;

    /// <param name="pipeName">Имя канала без \\.\pipe\ (каркас передаёт его аргументом --pipe).</param>
    /// <param name="appVersion">Версия программы для ответа hello.</param>
    /// <param name="handler">Обработчик методов; вызывается в потоке, где создан HostLink (UI).</param>
    /// <param name="actions">Действия для кнопок каркаса: invoke {"action": id}.</param>
    public HostLink(string pipeName, string appVersion, Func<string, JsonNode?, Task<object?>> handler,
        Func<IEnumerable<(string Id, string Title)>>? actions = null)
    {
        _pipeName = pipeName;
        _appVersion = appVersion;
        _handler = handler;
        _actions = actions ?? (() => []);
        _ui = SynchronizationContext.Current;
    }

    /// <summary>Каркас подключён прямо сейчас.</summary>
    public bool IsConnected => _writer is not null;

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    /// <summary>Событие для каркаса (например, statusChanged). Без подключения молча пропускается.</summary>
    public void Publish(string name, object? data)
    {
        var json = new JsonObject { ["event"] = name, ["data"] = JsonSerializer.SerializeToNode(data, JsonOptions) };
        _ = WriteAsync(json.ToJsonString());
    }

    private async Task AcceptLoopAsync()
    {
        // Каркас может перезапускаться — после разрыва ждём следующего подключения.
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token);

                var utf8 = new UTF8Encoding(false);
                using var reader = new StreamReader(pipe, utf8, false, leaveOpen: true);
                _writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null) break;
                    if (line.Length > 0) _ = HandleLineAsync(line);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Каркас отключился — ждём снова.
            }
            finally
            {
                _writer = null;
            }
        }
    }

    private async Task HandleLineAsync(string line)
    {
        JsonNode? request;
        try { request = JsonNode.Parse(line); }
        catch (JsonException) { return; }

        var id = request?["id"]?.GetValue<long>();
        var method = request?["method"]?.GetValue<string>();
        if (id is null || method is null) return;

        var response = new JsonObject { ["id"] = id };
        try
        {
            object? result = method == "hello"
                ? new
                {
                    protocol = ProtocolVersion,
                    appVersion = _appVersion,
                    processId = Environment.ProcessId,
                    capabilities = new { actions = _actions().Select(a => new { id = a.Id, title = a.Title }).ToArray() },
                }
                : await InvokeOnUiAsync(() => _handler(method, request!["params"]));
            response["result"] = JsonSerializer.SerializeToNode(result ?? new { ok = true }, JsonOptions);
        }
        catch (Exception ex)
        {
            response["error"] = new JsonObject
            {
                ["code"] = ex is HostLinkError e ? e.Code : "error",
                ["message"] = ex.Message,
            };
        }

        await WriteAsync(response.ToJsonString());
    }

    private Task<object?> InvokeOnUiAsync(Func<Task<object?>> action)
    {
        if (_ui is null) return action();
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async _ =>
        {
            try { tcs.SetResult(await action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    private async Task WriteAsync(string line)
    {
        var writer = _writer;
        if (writer is null) return;
        await _writeGate.WaitAsync();
        try { await writer.WriteLineAsync(line); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally { _writeGate.Release(); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>
    /// Разбор аргументов: --hosted включает режим модуля, --pipe &lt;имя&gt; задаёт канал.
    /// Без --pipe используется AllInOne.&lt;defaultId&gt;.
    /// </summary>
    public static (bool Hosted, string Pipe) ParseArgs(string[] args, string defaultId)
    {
        var hosted = args.Any(a => a.Equals("--hosted", StringComparison.OrdinalIgnoreCase));
        var i = Array.FindIndex(args, a => a.Equals("--pipe", StringComparison.OrdinalIgnoreCase));
        var pipe = i >= 0 && i + 1 < args.Length ? args[i + 1] : "AllInOne." + defaultId;
        return (hosted, pipe);
    }
}

/// <summary>Ошибка метода для каркаса: код и сообщение попадут в ответ.</summary>
internal sealed class HostLinkError(string message, string code = "error") : Exception(message)
{
    public string Code { get; } = code;
}
