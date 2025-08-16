using System;
using System.Net.WebSockets;
using System.Text;
using EchoBot.Media; // Added for Dictionary
using Microsoft.Graph.Communications.Calls;
using System.IO; // 添加文件操作支持

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

    public List<IParticipant> Participants { get; private set; }
    
    // 添加Redis服务
    private RedisService _redisService;

    // 添加音频相关属性
    private List<short> _audioChunks = new List<short>();
    private Timer _sendTimer;
    private readonly int _sendIntervalMs = 3000; // 3秒发送间隔
    private readonly object _audioLock = new object();
    
    // VAD相关属性
    private readonly double _vadThreshold = 0.01; // VAD阈值，可调整
    private readonly int _vadMinSamples = 1600; // 最小样本数（100ms @ 16kHz）
    private readonly int _vadSilenceFrames = 3; // 静音帧数阈值
    private int _silenceFrameCount = 0;
    private bool _isVoiceActive = false;
    private string _processMessageText;

    public bool IsConnected => _connected && _webSocket?.State == WebSocketState.Open;

    /// <summary>
    /// 获取连接状态信息
    /// </summary>
    public string GetConnectionStatus()
    {
        lock (_lockObject)
        {
            return $"内部标志: {_connected}, WebSocket状态: {_webSocket?.State}, IsConnected: {IsConnected}";
        }
    }

    // 添加设置会议ID的方法
    public void SetMeetingInfo(List<IParticipant> participants,string meetingId, string callId = null)
    {
        Participants = participants;
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
                                // 获取所有参与者的displayName
                                var displayNames = new List<string>();
                                if (Participants != null && Participants.Count > 0)
                                {
                                    foreach (var participant in Participants)
                                    {
                                        if (participant?.Resource?.Info?.Identity?.User != null)
                                        {
                                            var displayName = participant.Resource.Info.Identity.User.DisplayName;
                                            if (!string.IsNullOrEmpty(displayName))
                                            {
                                                displayNames.Add(displayName);
                                            }
                                        }
                                    }
                                }

                                // 检查并移除字幕信息
                                var cleanText = ProcessTranscriptionText(transcriptionText, _processMessageText);
                                
                                _processMessageText = _processMessageText + cleanText;
                                // 使用与SpeechService一致的数据格式
                                var asrResult = new
                                {
                                    text = _processMessageText,
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                    voice_id = messageData.ContainsKey("voice_id") ? messageData["voice_id"].ToString() : "",
                                    message_id = messageData.ContainsKey("message_id") ? messageData["message_id"].ToString() : "",
                                    start_time = messageData.ContainsKey("start_time") ? messageData["start_time"].ToString() : "",
                                    end_time = messageData.ContainsKey("end_time") ? messageData["end_time"].ToString() : "",
                                    display_name = displayNames,
                                    meeting_id = MeetingId,
                                    slice_type = messageData.ContainsKey("slice_type") ? messageData["slice_type"].ToString() : ""
                                };
                                
                                // 序列化为JSON并存入Redis
                                var jsonResult = System.Text.Json.JsonSerializer.Serialize(asrResult, jsonOptions);
                                var key = $"meeting:{MeetingId}:transcription";
                                await _redisService.SetAsync(key, jsonResult, TimeSpan.FromHours(24)); // 保存24小时
                                
                                Console.WriteLine($"[Redis] ASR结果已存入Redis，key: {key}, 内容: {jsonResult}");
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
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"处理会议消息时出错: {ex.Message}");
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
            // 将字节数组转换为Int16数组
            var int16Data = ConvertBytesToInt16Array(message);
            
            // 使用VAD判断是否有语音活动
            if (HasVoiceActivity(int16Data))
            {
                // 检测到语音活动，添加到缓冲区
                lock (_audioLock)
                {
                    _audioChunks.AddRange(int16Data);
                }
                
                // 启动定时器（如果还没启动）
                StartSendTimer();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"处理音频数据失败: {ex.Message}");
            throw;
        }
    }
    
    private void StartSendTimer()
    {
        if (_sendTimer == null)
        {
            _sendTimer = new Timer(async _ => await SendAudioChunks(), null, _sendIntervalMs, _sendIntervalMs);
            Console.WriteLine($"音频发送定时器已启动，间隔: {_sendIntervalMs}ms");
        }
    }
    
    private async Task SendAudioChunks()
    {
        if (!IsConnected || _webSocket?.State != WebSocketState.Open)
        {
            return;
        }
        
        lock (_audioLock)
        {
            if (_audioChunks.Count == 0)
            {
                return;
            }
            
            // 获取累积的音频数据
            var audioData = _audioChunks.ToArray();
            _audioChunks.Clear();
            
            // 转换为字节数组
            var byteData = ConvertInt16ArrayToBytes(audioData);
            
            // 异步发送（不等待，避免阻塞定时器）
            _ = Task.Run(async () =>
            {
                try
                {
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(byteData),
                        WebSocketMessageType.Binary,
                        true,
                        _cancellationTokenSource.Token);
                    
                    Console.WriteLine($"已发送音频块，样本数: {audioData.Length}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"发送音频数据失败: {ex.Message}");
                }
            });
        }
    }
    
    // 新增：将字节数组转换为Int16数组
    private short[] ConvertBytesToInt16Array(byte[] buffer)
    {
        if (buffer == null || buffer.Length % 2 != 0)
            throw new ArgumentException("缓冲区长度必须是2的倍数", nameof(buffer));

        int length = buffer.Length / 2;
        short[] result = new short[length];

        for (int i = 0; i < length; i++)
        {
            int index = i * 2;
            result[i] = (short)((buffer[index] << 8) | buffer[index + 1]);
        }

        return result;
    }
    
    // 新增：将Int16数组转换为字节数组
    private byte[] ConvertInt16ArrayToBytes(short[] int16Array)
    {
        if (int16Array == null)
            throw new ArgumentNullException(nameof(int16Array));

        byte[] result = new byte[int16Array.Length * 2];
        
        for (int i = 0; i < int16Array.Length; i++)
        {
            int index = i * 2;
            result[index] = (byte)(int16Array[i] >> 8);     // 高位字节
            result[index + 1] = (byte)(int16Array[i] & 0xFF); // 低位字节
        }
        
        return result;
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
        
        // 停止并清理定时器
        _sendTimer?.Dispose();
        _sendTimer = null;
        
        // 清空音频缓冲区
        lock (_audioLock)
        {
            _audioChunks.Clear();
        }
        
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
    
    // 新增：VAD语音活动检测方法
    private bool HasVoiceActivity(short[] audioData)
    {
        if (audioData == null || audioData.Length == 0)
            return false;
            
        // 计算音频数据的能量（RMS）
        double sum = 0;
        for (int i = 0; i < audioData.Length; i++)
        {
            sum += (double)(audioData[i] * audioData[i]);
        }
        double rms = Math.Sqrt(sum / audioData.Length);
        
        // 将RMS值归一化到0-1范围（假设16位音频的最大值是32767）
        double normalizedRms = rms / 32767.0;
        
        // 检查是否超过VAD阈值
        bool hasVoice = normalizedRms > _vadThreshold;
        
        // 更新静音帧计数和语音活动状态
        if (hasVoice)
        {
            _silenceFrameCount = 0;
            _isVoiceActive = true;
        }
        else
        {
            _silenceFrameCount++;
            // 如果连续多帧都是静音，则认为语音活动结束
            if (_silenceFrameCount >= _vadSilenceFrames)
            {
                _isVoiceActive = false;
            }
        }
        
        // 记录VAD检测结果（可选，用于调试）
        if (audioData.Length >= _vadMinSamples)
        {
            Console.WriteLine($"[VAD] RMS: {normalizedRms:F6}, 阈值: {_vadThreshold:F6}, 有语音: {hasVoice}, 静音帧: {_silenceFrameCount}");
        }
        
        return hasVoice || _isVoiceActive; // 返回当前帧有语音或之前有语音活动
    }
    
    // 新增：处理转录文本的方法（集成字幕移除和去重）
    private string ProcessTranscriptionText(string text, string existingText)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
            
        // 第一步：移除字幕信息
        var cleanText = RemoveSubtitleInfoOnly(text);
        
        // 第二步：去重处理
        var newText = GetNewTextOnly(existingText, cleanText);
        
        return newText;
    }
    
    // 新增：仅移除字幕信息的方法
    private string RemoveSubtitleInfoOnly(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
            
        // 定义需要移除的字幕信息模式（支持多种变体）
        var subtitlePatterns = new[]
        {
            "字幕由 Am ara. org 社群提供",
            "字幕由 Amara.org 社群提供",
            "字幕由 Amara org 社群提供",
            "字幕由 Am ara.org 社群提供",
            "字幕由 Am ara . org 社群提供",
            "字幕由 Am ara .org 社群提供",
            "字幕由 Amara . org 社群提供",
            "字幕由 Amara .org 社群提供"
        };
        
        var cleanText = text;
        foreach (var pattern in subtitlePatterns)
        {
            // 移除完整的字幕信息
            cleanText = cleanText.Replace(pattern, "");
            
            // 移除可能包含额外空格的变体
            var spacedPattern = pattern.Replace(" ", "\\s+");
            cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, spacedPattern, "");
        }
        
        // 清理可能留下的多余空格
        cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, "\\s+", " ").Trim();
        
        return cleanText;
    }

    // 新增：获取新增文本的方法（优化版）
    private string GetNewTextOnly(string existingText, string newText)
    {
        if (string.IsNullOrEmpty(newText))
            return string.Empty;

        if (string.IsNullOrEmpty(existingText))
            return newText;

        // 如果新文本完全包含在现有文本中，返回空字符串
        if (existingText.Contains(newText))
            return string.Empty;

        // 查找新增的文本部分
        var newPart = FindNewTextPart(existingText, newText);
        
        if (!string.IsNullOrEmpty(newPart))
        {
            Console.WriteLine($"[去重] 检测到新增部分: '{newPart}'");
            return newPart;
        }

        // 如果无法检测到新增部分，返回完整新文本
        return newText;
    }

    // 查找新增文本部分的核心算法
    private string FindNewTextPart(string existingText, string newText)
    {
        // 方法1: 从末尾开始查找，找到第一个不同的位置
        int startIndex = FindFirstDifferentPosition(existingText, newText);
        if (startIndex >= 0 && startIndex < newText.Length)
        {
            return newText.Substring(startIndex);
        }

        // 方法2: 从开头开始查找，找到第一个不同的位置
        int endIndex = FindLastDifferentPosition(existingText, newText);
        if (endIndex >= 0)
        {
            return newText.Substring(0, endIndex + 1);
        }

        return string.Empty;
    }

    // 从末尾开始查找第一个不同的位置
    private int FindFirstDifferentPosition(string existingText, string newText)
    {
        int minLength = Math.Min(existingText.Length, newText.Length);
        
        for (int i = 0; i < minLength; i++)
        {
            if (existingText[i] != newText[i])
            {
                return i;
            }
        }
        
        // 如果前面都相同，返回新文本中超出部分的位置
        return minLength < newText.Length ? minLength : -1;
    }

    // 从开头开始查找最后一个不同的位置
    private int FindLastDifferentPosition(string existingText, string newText)
    {
        int minLength = Math.Min(existingText.Length, newText.Length);
        
        for (int i = 1; i <= minLength; i++)
        {
            if (existingText[existingText.Length - i] != newText[newText.Length - i])
            {
                return newText.Length - i;
            }
        }
        
        // 如果后面都相同，返回新文本中前面部分的位置
        return minLength < newText.Length ? minLength - 1 : -1;
    }
}
