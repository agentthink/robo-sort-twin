using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace RobotVisionSortingTwin
{
    public sealed record TelemetryProduct(Guid Id, string Name);

    /// <summary>本地 NDJSON 协议的数据对象，发送端无需引用沙盘程序集。</summary>
    public sealed class TelemetryMessage
    {
        public string Schema { get; set; } = "robot-vision-twin/v1";
        public string Type { get; set; } = "sample";
        public bool Running { get; set; }
        public Guid RunId { get; set; }
        public long Sequence { get; set; }
        public DateTimeOffset Time { get; set; }
        public string Status { get; set; } = string.Empty;
        public Guid? ProductId { get; set; }
        public string? ProductName { get; set; }
        public double? Area { get; set; }
        public double? Perimeter { get; set; }
        public bool Sent { get; set; }
        public double ElapsedMs { get; set; }
        public string? ImageJpeg { get; set; }
        public TelemetryProduct[] Products { get; set; } = [];

        /// <summary>拒绝不支持的版本与无效数据；产品身份以 GUID 匹配。</summary>
        public void Validate()
        {
            if (Schema != "robot-vision-twin/v1" || Type is not ("sample" or "session")
                || Products is null || Products.Length > 128
                || Products.Any(product => product is null || product.Id == Guid.Empty
                    || string.IsNullOrWhiteSpace(product.Name) || product.Name.Length > 80)
                || Products.Select(product => product.Id).Distinct().Count() != Products.Length
                || ProductName?.Length > 80 || Status is null || Status.Length > 80
                || !double.IsFinite(ElapsedMs) || ElapsedMs < 0
                || Area is { } area && (!double.IsFinite(area) || area < 0)
                || Perimeter is { } perimeter && (!double.IsFinite(perimeter) || perimeter < 0)
                || Type == "sample" && (RunId == Guid.Empty || Sequence < 1
                    || Status == "Recognized" && (ProductId is null || ProductId == Guid.Empty
                        || string.IsNullOrWhiteSpace(ProductName))
                    || Status is not ("Recognized" or "NoMatch" or "Ambiguous"
                        or "InvalidTarget" or "ResolutionMismatch")))
            {
                throw new InvalidDataException("联动消息格式或版本无效。");
            }
        }
    }

    /// <summary>仅当前 Windows 用户可连接的本地命名管道；不监听网络端口。</summary>
    public sealed class TelemetryServer : IAsyncDisposable
    {
        public const string PipeName = "RobotVisionSortingTwin.v1";
        private const int MaximumLineLength = 262144;
        private readonly CancellationTokenSource _stop = new();
        private Task? _worker;

        public Func<TelemetryMessage, Task>? Received { get; set; }
        public Func<string, Task>? StateChanged { get; set; }

        /// <summary>后台接收消息；回调返回后才继续读取，避免界面调度无限排队。</summary>
        public void Start()
        {
            _worker ??= Task.Run(ListenAsync);
        }

        private async Task ListenAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(PipeName,
                        PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await NotifyAsync("等待平台连接");
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    await NotifyAsync("平台已连接");
                    await ReadMessagesAsync(pipe, _stop.Token);
                    await NotifyAsync("平台已断开 · 等待重新连接");
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or JsonException or DecoderFallbackException)
                {
                    await NotifyAsync("联动接收暂停：" + ex.Message);
                    try
                    {
                        await Task.Delay(1000, _stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>逐块读取并限制单条消息大小；断行或错误消息不会执行任何设备操作。</summary>
        private async Task ReadMessagesAsync(Stream stream, CancellationToken token)
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                false, 4096, true);
            var buffer = new char[4096];
            var line = new StringBuilder();
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token);
                if (count == 0)
                {
                    return;
                }

                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        if (line.Length > 0)
                        {
                            var message = JsonSerializer.Deserialize<TelemetryMessage>(
                                line.ToString(), new JsonSerializerOptions { MaxDepth = 16 });
                            if (message is null)
                            {
                                throw new InvalidDataException("消息为空。");
                            }

                            message.Validate();
                            if (Received is not null)
                            {
                                await Received(message);
                            }
                        }

                        line.Clear();
                    }
                    else if (buffer[i] != '\r')
                    {
                        if (line.Length >= MaximumLineLength)
                        {
                            throw new InvalidDataException("联动消息超过 256 KiB 限制。");
                        }

                        line.Append(buffer[i]);
                    }
                }
            }
        }

        private Task NotifyAsync(string state)
        {
            return StateChanged?.Invoke(state) ?? Task.CompletedTask;
        }

        /// <summary>取消监听并等待后台任务退出，不保留管道句柄。</summary>
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_worker is not null)
            {
                await _worker;
            }

            _stop.Dispose();
        }
    }
}
