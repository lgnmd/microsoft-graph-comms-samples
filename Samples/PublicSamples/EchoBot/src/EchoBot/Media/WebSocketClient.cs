using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using EchoBot.Media; // Added for Dictionary

public class WebSocketClient
{
    private ClientWebSocket _webSocket;
    private readonly Uri _serverUri;
    private bool _connected = false;
    private bool _isListening = false;
    private readonly object _lockObject = new object();
    private CancellationTokenSource _cancellationTokenSource;
    private readonly int _maxReconnectAttempts = 5;
    private readonly int _reconnectDelayMs = 2000;
    
    // 添加会议ID属性
    public string MeetingId { get; private set; }
    public string CallId { get; private set; }
    
    // 添加Redis服务
    private RedisService _redisService;

    public bool IsConnected => _connected && _webSocket?.State == WebSocketState.Open;

    // 添加设置会议ID的方法
    public void SetMeetingInfo(string meetingId, string callId = null)
    {
        MeetingId = meetingId;
        CallId = callId;
        Console.WriteLine($"设置会议信息 - 会议ID: {meetingId}, 通话ID: {callId ?? "未设置"}");
    }

    // 设置Redis服务的方法
    public void SetRedisService(RedisService redisService)
    {
        _redisService = redisService;
        Console.WriteLine("Redis服务已设置");
    }

    // 获取会议信息的JSON格式
    public string GetMeetingInfoJson()
    {
        var meetingInfo = new
        {
            MeetingId = MeetingId,
            CallId = CallId,
            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ConnectionStatus = IsConnected ? "Connected" : "Disconnected"
        };
        
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };
        
        return System.Text.Json.JsonSerializer.Serialize(meetingInfo, jsonOptions);
    }

    // 处理会议相关消息的方法
    private async Task ProcessMeetingMessage(string message)
    {
        try
        {
            // 尝试解析JSON消息
            if (message.StartsWith("{") && message.EndsWith("}"))
            {
                // 配置JSON反序列化选项，确保中文字符正确解析
                var jsonOptions = new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                
                var messageData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(message, jsonOptions);
                
                if (messageData.ContainsKey("text"))
                {
                    var transcriptionText = messageData["text"]?.ToString();
                    if (!string.IsNullOrEmpty(transcriptionText))
                    {
                        Console.WriteLine($"[会议ID: {MeetingId}] 语音识别结果: {transcriptionText}");
                        
                        // 以会议ID为key，将会议活动存入Redis
                        if (_redisService != null && !string.IsNullOrEmpty(MeetingId))
                        {
                            try
                            {
                                // 创建额外数据
                                var additionalData = new Dictionary<string, object>
                                {
                                    ["WebSocketMessage"] = message,
                                    ["ProcessedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                                    ["SessionId"] = Guid.NewGuid().ToString()
                                };
                                
                                // 使用全量数据存储方式，覆盖之前的记录
                                var success = await _redisService.SaveMeetingFullTranscriptionAsync(
                                    MeetingId, 
                                    CallId ?? "unknown", 
                                    transcriptionText, 
                                    additionalData
                                );
                                
                                if (success)
                                {
                                    Console.WriteLine($"[Redis] 会议全量语音识别结果已以key 'meeting:{MeetingId}:transcription' 存入Redis - 会议ID: {MeetingId}");
                                    
                                    // 获取会议摘要信息
                                    var meetingSummary = await _redisService.GetMeetingInfoAsync(MeetingId);
                                    if (!string.IsNullOrEmpty(meetingSummary))
                                    {
                                        Console.WriteLine($"[Redis] 会议摘要: {meetingSummary}");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"[Redis] 保存会议全量语音识别结果到Redis失败 - 会议ID: {MeetingId}");
                                }
                            }
                            catch (Exception redisEx)
                            {
                                Console.WriteLine($"[Redis] 保存到Redis时出错: {redisEx.Message}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[Redis] Redis服务未配置或会议ID未设置，跳过Redis保存");
                        }
                        
                        // 记录会议活动
                        await LogMeetingActivity(transcriptionText);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"处理会议消息时出错: {ex.Message}");
        }
    }

    // 获取会议活动数量的辅助方法
    private async Task<int> GetMeetingActivitiesCount(string meetingId)
    {
        try
        {
            if (_redisService != null)
            {
                var activities = await _redisService.GetMeetingActivitiesByTimeAsync(meetingId, 1000); // 获取更多活动来计算总数
                return activities.Count;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取会议活动数量时出错: {ex.Message}");
        }
        return 0;
    }

    // 记录会议活动的方法
    private async Task LogMeetingActivity(string activity)
    {
        try
        {
            var logEntry = new
            {
                MeetingId = MeetingId,
                CallId = CallId,
                Activity = activity,
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Type = "Transcription"
            };
            
            // 配置JSON序列化选项，确保中文字符正确显示
            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false
            };
            
            var logJson = System.Text.Json.JsonSerializer.Serialize(logEntry, jsonOptions);
            Console.WriteLine($"[会议日志] {logJson}");
            
            // 保存到Redis（如果Redis服务可用）
            if (_redisService != null && !string.IsNullOrEmpty(MeetingId))
            {
                try
                {
                    var success = await _redisService.SaveMeetingFullTranscriptionAsync(
                        MeetingId, 
                        CallId ?? "unknown", 
                        activity
                    );
                    
                    if (success)
                    {
                        Console.WriteLine($"[Redis] 会议全量语音识别结果已保存到Redis - 会议ID: {MeetingId}");
                    }
                    else
                    {
                        Console.WriteLine($"[Redis] 保存会议全量语音识别结果到Redis失败 - 会议ID: {MeetingId}");
                    }
                }
                catch (Exception redisEx)
                {
                    Console.WriteLine($"[Redis] 保存到Redis时出错: {redisEx.Message}");
                }
            }
            else
            {
                Console.WriteLine("[Redis] Redis服务未配置或会议ID未设置，跳过Redis保存");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"记录会议活动时出错: {ex.Message}");
        }
    }

    public WebSocketClient()
    {
        string serverUrl = " wss://asr-ws.votee-demo.votee.dev/v1/audio/transcriptions?language=yue&api-key=votee_bb0ea526910c8af62992bf4d ";     
        
        _serverUri = new Uri(serverUrl);
        _cancellationTokenSource = new CancellationTokenSource();
        InitializeWebSocket();
    }

    private void InitializeWebSocket()
    {
        lock (_lockObject)
        {
            if (_webSocket != null)
            {
                try
                {
                    _webSocket.Dispose();
                }
                catch { }
            }
            _webSocket = new ClientWebSocket();
        }
    }

    public async Task Connect()
    {
        lock (_lockObject)
        {
            if (_connected && _webSocket?.State == WebSocketState.Open)
            {
                Console.WriteLine("WebSocket已经连接");
                return;
            }
        }

        try
        {
            InitializeWebSocket();
            await _webSocket.ConnectAsync(_serverUri, _cancellationTokenSource.Token);
            
            lock (_lockObject)
            {
                _connected = true;
            }
            
            Console.WriteLine("已连接到服务器");
            
            // 显示会议信息
            if (!string.IsNullOrEmpty(MeetingId))
            {
                Console.WriteLine($"会议信息: {GetMeetingInfoJson()}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"连接失败: {ex.Message}");
            throw;
        }
    }

    public async Task SendMessage(byte[] message)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("WebSocket连接未打开");
        }

        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(ConvertBufferToInt16Array(message)),
                WebSocketMessageType.Binary,
                true,
                _cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"发送消息失败: {ex.Message}");
            await HandleConnectionError();
            throw;
        }
    }

    public async Task StartListening()
    {
        if (_isListening)
        {
            Console.WriteLine("已经在监听中");
            return;
        }

        if (!IsConnected)
        {
            await Connect();
        }

        _isListening = true;
        await ListenLoop();
    }

    private async Task ListenLoop()
    {
        var buffer = new byte[1024 * 4];
        
        while (_isListening && !_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                if (!IsConnected)
                {
                    await Reconnect();
                    continue;
                }

                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), 
                    _cancellationTokenSource.Token);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    
                    // 记录包含会议ID的消息
                    if (!string.IsNullOrEmpty(MeetingId))
                    {
                        Console.WriteLine($"[会议ID: {MeetingId}] 收到服务器消息: {message}");
                        
                        // 可以在这里添加会议特定的处理逻辑
                        await ProcessMeetingMessage(message);
                    }
                    else
                    {
                        Console.WriteLine($"收到服务器消息: {message}");
                    }
                    
                    // add redis 
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("收到服务器关闭消息");
                    await HandleConnectionClose();
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    Console.WriteLine($"收到二进制消息，长度: {result.Count}");
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("监听被取消");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"接收消息错误: {ex.Message}");
                await HandleConnectionError();
            }
        }
    }

    private async Task HandleConnectionClose()
    {
        lock (_lockObject)
        {
            _connected = false;
        }

        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "服务器关闭连接",
                    _cancellationTokenSource.Token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"关闭连接时出错: {ex.Message}");
        }

        // 尝试重新连接
        if (_isListening)
        {
            await Reconnect();
        }
    }

    private async Task HandleConnectionError()
    {
        lock (_lockObject)
        {
            _connected = false;
        }

        if (_isListening)
        {
            await Reconnect();
        }
    }

    private async Task Reconnect()
    {
        int attempts = 0;
        while (attempts < _maxReconnectAttempts && _isListening && !_cancellationTokenSource.Token.IsCancellationRequested)
        {
            attempts++;
            Console.WriteLine($"尝试重新连接... (第{attempts}次)");
            
            try
            {
                await Task.Delay(_reconnectDelayMs, _cancellationTokenSource.Token);
                await Connect();
                
                if (IsConnected)
                {
                    Console.WriteLine("重新连接成功");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"重新连接失败: {ex.Message}");
            }
        }

        if (attempts >= _maxReconnectAttempts)
        {
            Console.WriteLine($"达到最大重连次数({_maxReconnectAttempts})，停止重连");
            _isListening = false;
        }
    }

    public async Task Disconnect()
    {
        _isListening = false;
        _cancellationTokenSource.Cancel();
        
        lock (_lockObject)
        {
            _connected = false;
        }

        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "客户端主动关闭",
                    _cancellationTokenSource.Token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"关闭连接时出错: {ex.Message}");
        }
        finally
        {
            try
            {
                _webSocket?.Dispose();
            }
            catch { }
        }
    }

    public void Dispose()
    {
        Disconnect().Wait();
        _cancellationTokenSource?.Dispose();
    }
    
    public static byte[] ConvertBufferToInt16Array(byte[] buffer)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));

        // 确保缓冲区长度是2的倍数（每个Int16由2个字节组成）
        if (buffer.Length % 2 != 0)
            throw new ArgumentException("缓冲区长度必须是2的倍数", nameof(buffer));

        int length = buffer.Length / 2;
        byte[] result = new byte[length];

        for (int i = 0; i < length; i++)
        {
            // 计算当前Int16在缓冲区中的起始索引
            int index = i * 2;

            // 大端字节序：高位字节在前，低位字节在后
            byte highByte = buffer[index];
            byte lowByte = buffer[index + 1];

            // 组合成Int16
            result[i] = (byte)((highByte << 8) | lowByte);
        }

        return result;
    }
}
