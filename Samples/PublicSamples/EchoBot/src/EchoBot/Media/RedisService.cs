using StackExchange.Redis;
using System.Text.Json;

namespace EchoBot.Media
{
    /// <summary>
    /// Redis服务类，用于处理会议数据和缓存
    /// </summary>
    public class RedisService : IDisposable
    {
        private readonly ConnectionMultiplexer _redis;
        private readonly IDatabase _database;
        private readonly ILogger _logger;
        private readonly string _connectionString;

        public RedisService(string connectionString, ILogger logger)
        {
            _connectionString = connectionString;
            _logger = logger;
            
            try
            {
                // 为阿里云Redis配置连接选项
                var options = ConfigurationOptions.Parse(connectionString);
                options.ConnectTimeout = 10000; // 10秒连接超时
                options.SyncTimeout = 10000;    // 10秒同步超时
                // options.ResponseTimeout = 10000; // 已过时，移除
                options.ReconnectRetryPolicy = new ExponentialRetry(5000); // 指数重试策略
                // options.AbortConnect = false;   // 此属性不存在，移除
                options.ConnectRetry = 3;       // 连接重试次数
                
                _logger.LogInformation($"正在连接Redis: {options.EndPoints.FirstOrDefault()?.ToString()}");
                
                _redis = ConnectionMultiplexer.Connect(options);
                _database = _redis.GetDatabase();
                
                // 测试连接
                var pingResult = _database.Ping();
                _logger.LogInformation($"Redis连接成功，Ping延迟: {pingResult.TotalMilliseconds}ms");
                
                // 注册连接事件
                _redis.ConnectionFailed += (sender, e) =>
                {
                    _logger.LogWarning($"Redis连接失败: {e.EndPoint}, 异常: {e.Exception?.Message}");
                };
                
                _redis.ConnectionRestored += (sender, e) =>
                {
                    _logger.LogInformation($"Redis连接已恢复: {e.EndPoint}");
                };
                
                _redis.ErrorMessage += (sender, e) =>
                {
                    _logger.LogError($"Redis错误消息: {e.Message}");
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis连接失败");
                throw;
            }
        }

        /// <summary>
        /// 安全地序列化对象到JSON，避免循环引用问题
        /// </summary>
        private string SafeSerializeToJson(object obj)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false,
                    // 移除 ReferenceHandler.Preserve，避免生成 $id 等引用跟踪信息
                    MaxDepth = 32
                };
                
                return JsonSerializer.Serialize(obj, options);
            }
            catch (JsonException ex) when (ex.Message.Contains("cycle") || ex.Message.Contains("depth"))
            {
                // 如果遇到循环引用或深度问题，尝试创建一个简化的对象
                _logger.LogWarning($"JSON序列化遇到循环引用或深度问题，使用简化对象: {ex.Message}");
                
                try
                {
                    // 创建一个包含基本信息的简化对象
                    var simplifiedObj = new
                    {
                        Error = "原始对象包含循环引用，无法序列化",
                        Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        Type = obj?.GetType().Name ?? "Unknown"
                    };
                    
                    return JsonSerializer.Serialize(simplifiedObj, new JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        WriteIndented = false
                    });
                }
                catch
                {
                    // 最后的备选方案
                    return JsonSerializer.Serialize(new { Error = "序列化失败", Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JSON序列化失败");
                return JsonSerializer.Serialize(new { Error = "序列化失败", Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") });
            }
        }

        /// <summary>
        /// 获取会议活动列表（按时间排序）
        /// </summary>
        public async Task<List<string>> GetMeetingActivitiesByTimeAsync(string meetingId, int count = 50, bool ascending = false)
        {
            try
            {
                var timeIndexKey = $"meeting:{meetingId}:timeindex";
                var activities = await _database.SortedSetRangeByScoreAsync(
                    timeIndexKey, 
                    order: ascending ? Order.Ascending : Order.Descending,
                    take: count
                );
                
                return activities.Select(a => a.ToString()).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"按时间获取会议活动失败 - 会议ID: {meetingId}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 获取会议信息
        /// </summary>
        public async Task<string> GetMeetingInfoAsync(string meetingId)
        {
            try
            {
                var key = $"meeting:{meetingId}:info";
                var info = await _database.StringGetAsync(key);
                
                return info.HasValue ? info.ToString() : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"获取会议信息失败 - 会议ID: {meetingId}");
                return null;
            }
        }

        /// <summary>
        /// 保存会议全量语音识别结果到Redis（覆盖存储）
        /// </summary>
        public async Task<bool> SaveMeetingFullTranscriptionAsync(string meetingId, string callId, string transcription, Dictionary<string, object> additionalData = null)
        {
            try
            {
                // 主转录结果key - 全量覆盖存储
                var transcriptionKey = $"meeting:{meetingId}:transcription";
                
                // 创建全量转录记录
                var transcriptionData = new Dictionary<string, object>
                {
                    ["MeetingId"] = meetingId,
                    ["CallId"] = callId,
                    ["Transcription"] = transcription,
                    ["Type"] = "FullTranscription",
                    ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["MessageId"] = Guid.NewGuid().ToString(),
                    ["Source"] = "WebSocket",
                    ["IsFullData"] = true,
                    ["UpdatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                };
                
                // 添加额外数据
                if (additionalData != null)
                {
                    foreach (var kvp in additionalData)
                    {
                        // 检查值是否可以安全序列化
                        if (kvp.Value != null)
                        {
                            try
                            {
                                // 尝试序列化来验证是否安全
                                var testJson = SafeSerializeToJson(kvp.Value);
                                transcriptionData[kvp.Key] = kvp.Value;
                            }
                            catch
                            {
                                // 如果无法序列化，存储一个简化的值
                                transcriptionData[kvp.Key] = $"无法序列化的对象: {kvp.Value.GetType().Name}";
                                _logger.LogWarning($"跳过无法序列化的额外数据: {kvp.Key} = {kvp.Value.GetType().Name}");
                            }
                        }
                        else
                        {
                            transcriptionData[kvp.Key] = kvp.Value;
                        }
                    }
                }
                
                var json = SafeSerializeToJson(transcriptionData);
                
                // 1. 保存全量转录结果（覆盖存储）
                await _database.StringSetAsync(transcriptionKey, json, TimeSpan.FromHours(24));
                
                // 2. 保存最新活动到会议摘要
                var summaryKey = $"meeting:{meetingId}:summary";
                var summaryData = new
                {
                    MeetingId = meetingId,
                    CallId = callId,
                    LastTranscription = transcription,
                    LastTranscriptionTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    TranscriptionLength = transcription?.Length ?? 0,
                    Status = "Active",
                    UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    StorageType = "FullData"
                };
                
                var summaryJson = SafeSerializeToJson(summaryData);
                await _database.StringSetAsync(summaryKey, summaryJson, TimeSpan.FromHours(24));
                
                // 3. 保存到时间索引（用于按时间查询）
                var timeIndexKey = $"meeting:{meetingId}:timeindex";
                var timeScore = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await _database.SortedSetAddAsync(timeIndexKey, json, timeScore);
                
                // 4. 设置过期时间
                await _database.KeyExpireAsync(transcriptionKey, TimeSpan.FromHours(24));
                await _database.KeyExpireAsync(timeIndexKey, TimeSpan.FromHours(24));
                
                _logger.LogInformation($"已保存会议全量语音识别结果到Redis - 会议ID: {meetingId}, 转录长度: {transcription?.Length ?? 0}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"保存会议全量语音识别结果到Redis失败 - 会议ID: {meetingId}");
                return false;
            }
        }

        /// <summary>
        /// 设置键值对
        /// </summary>
        /// <param name="key">键</param>
        /// <param name="value">值</param>
        /// <param name="expiry">过期时间</param>
        /// <returns>是否设置成功</returns>
        public async Task<bool> SetAsync(string key, string value, TimeSpan? expiry = null)
        {
            try
            {
                return await _database.StringSetAsync(key, value, expiry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"设置Redis键值对失败 - 键: {key}");
                return false;
            }
        }

        /// <summary>
        /// 获取键值
        /// </summary>
        /// <param name="key">键</param>
        /// <returns>值，如果不存在则返回null</returns>
        public async Task<string> GetAsync(string key)
        {
            try
            {
                var value = await _database.StringGetAsync(key);
                return value.HasValue ? value.ToString() : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"获取Redis键值失败 - 键: {key}");
                return null;
            }
        }

        /// <summary>
        /// 获取匹配模式的键
        /// </summary>
        /// <param name="pattern">键模式，支持通配符</param>
        /// <returns>匹配的键列表</returns>
        public async Task<List<string>> GetKeysAsync(string pattern)
        {
            try
            {
                var server = _redis.GetServer(_redis.GetEndPoints().First());
                var keys = server.Keys(pattern: pattern);
                return keys.Select(k => k.ToString()).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"获取Redis键失败 - 模式: {pattern}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 检查Redis连接状态
        /// </summary>
        public bool IsConnected()
        {
            try
            {
                return _redis?.IsConnected == true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 测试Redis连接和性能
        /// </summary>
        public async Task<string> TestConnectionAsync()
        {
            try
            {
                var testKey = $"test:connection:{Guid.NewGuid()}";
                var testValue = $"测试数据_{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
                
                // 测试写入
                var startTime = DateTime.UtcNow;
                await _database.StringSetAsync(testKey, testValue, TimeSpan.FromMinutes(5));
                var writeTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                
                // 测试读取
                startTime = DateTime.UtcNow;
                var readValue = await _database.StringGetAsync(testKey);
                var readTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                
                // 测试删除
                startTime = DateTime.UtcNow;
                await _database.KeyDeleteAsync(testKey);
                var deleteTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                
                // 测试Ping
                startTime = DateTime.UtcNow;
                var pingResult = await _database.PingAsync();
                var pingTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                
                var testResult = new
                {
                    Status = "成功",
                    Endpoint = _redis.GetEndPoints().FirstOrDefault()?.ToString(),
                    PingLatency = pingResult.TotalMilliseconds,
                    WriteTime = writeTime,
                    ReadTime = readTime,
                    DeleteTime = deleteTime,
                    TotalTestTime = writeTime + readTime + deleteTime + pingTime,
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                };
                
                _logger.LogInformation($"Redis连接测试成功 - 写入: {writeTime}ms, 读取: {readTime}ms, 删除: {deleteTime}ms, Ping: {pingResult.TotalMilliseconds}ms");
                
                return JsonSerializer.Serialize(testResult, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false,
                    ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.Preserve,
                    MaxDepth = 32
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis连接测试失败");
                
                var errorResult = new
                {
                    Status = "失败",
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                };
                
                return JsonSerializer.Serialize(errorResult, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false,
                    ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.Preserve,
                    MaxDepth = 32
                });
            }
        }

        public void Dispose()
        {
            _redis?.Close();
            _redis?.Dispose();
        }
    }
} 