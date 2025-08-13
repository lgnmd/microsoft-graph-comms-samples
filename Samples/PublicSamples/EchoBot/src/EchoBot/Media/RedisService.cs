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
        /// 保存会议活动到Redis
        /// </summary>
        public async Task<bool> SaveMeetingActivityAsync(string meetingId, string callId, string activity, string type = "Transcription")
        {
            try
            {
                var key = $"meeting:{meetingId}:activities";
                var activityData = new
                {
                    MeetingId = meetingId,
                    CallId = callId,
                    Activity = activity,
                    Type = type,
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                };

                var json = JsonSerializer.Serialize(activityData);
                
                // 使用Redis List存储活动记录
                await _database.ListRightPushAsync(key, json);
                
                // 设置过期时间（24小时）
                await _database.KeyExpireAsync(key, TimeSpan.FromHours(24));
                
                _logger.LogInformation($"已保存会议活动到Redis - 会议ID: {meetingId}, 活动: {activity}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"保存会议活动到Redis失败 - 会议ID: {meetingId}");
                return false;
            }
        }

        /// <summary>
        /// 以会议ID为key保存会议活动（增强版）
        /// </summary>
        public async Task<bool> SaveMeetingActivityWithKeyAsync(string meetingId, string callId, string activity, string type = "Transcription", Dictionary<string, object> additionalData = null)
        {
            try
            {
                // 主活动列表key
                var activitiesKey = $"meeting:{meetingId}:activities";
                
                // 创建活动记录
                var activityData = new Dictionary<string, object>
                {
                    ["MeetingId"] = meetingId,
                    ["CallId"] = callId,
                    ["Activity"] = activity,
                    ["Type"] = type,
                    ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["MessageId"] = Guid.NewGuid().ToString(),
                    ["Source"] = "WebSocket"
                };
                
                // 添加额外数据
                if (additionalData != null)
                {
                    foreach (var kvp in additionalData)
                    {
                        activityData[kvp.Key] = kvp.Value;
                    }
                }
                
                var json = JsonSerializer.Serialize(activityData, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false
                });
                
                // 1. 保存到活动列表
                await _database.ListRightPushAsync(activitiesKey, json);
                
                // 2. 保存最新活动到会议摘要
                var summaryKey = $"meeting:{meetingId}:summary";
                var summaryData = new
                {
                    MeetingId = meetingId,
                    CallId = callId,
                    LastActivity = activity,
                    LastActivityTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    LastActivityType = type,
                    TotalActivities = await _database.ListLengthAsync(activitiesKey),
                    Status = "Active",
                    UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                };
                
                var summaryJson = JsonSerializer.Serialize(summaryData, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false
                });
                await _database.StringSetAsync(summaryKey, summaryJson, TimeSpan.FromHours(24));
                
                // 3. 保存到时间索引（用于按时间查询）
                var timeIndexKey = $"meeting:{meetingId}:timeindex";
                var timeScore = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await _database.SortedSetAddAsync(timeIndexKey, json, timeScore);
                
                // 4. 设置过期时间
                await _database.KeyExpireAsync(activitiesKey, TimeSpan.FromHours(24));
                await _database.KeyExpireAsync(timeIndexKey, TimeSpan.FromHours(24));
                
                _logger.LogInformation($"已以会议ID为key保存会议活动到Redis - 会议ID: {meetingId}, 活动: {activity}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"以会议ID为key保存会议活动到Redis失败 - 会议ID: {meetingId}");
                return false;
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
        /// 保存会议信息到Redis
        /// </summary>
        public async Task<bool> SaveMeetingInfoAsync(string meetingId, string callId, string meetingInfo)
        {
            try
            {
                var key = $"meeting:{meetingId}:info";
                await _database.StringSetAsync(key, meetingInfo, TimeSpan.FromHours(24));
                
                _logger.LogInformation($"已保存会议信息到Redis - 会议ID: {meetingId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"保存会议信息到Redis失败 - 会议ID: {meetingId}");
                return false;
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
        /// 保存参与者信息到Redis
        /// </summary>
        public async Task<bool> SaveParticipantInfoAsync(string meetingId, string participantId, string participantInfo)
        {
            try
            {
                var key = $"meeting:{meetingId}:participants:{participantId}";
                await _database.StringSetAsync(key, participantInfo, TimeSpan.FromHours(24));
                
                _logger.LogInformation($"已保存参与者信息到Redis - 会议ID: {meetingId}, 参与者ID: {participantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"保存参与者信息到Redis失败 - 会议ID: {meetingId}, 参与者ID: {participantId}");
                return false;
            }
        }

        /// <summary>
        /// 获取参与者信息
        /// </summary>
        public async Task<string> GetParticipantInfoAsync(string meetingId, string participantId)
        {
            try
            {
                var key = $"meeting:{meetingId}:participants:{participantId}";
                var info = await _database.StringGetAsync(key);
                
                return info.HasValue ? info.ToString() : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"获取参与者信息失败 - 会议ID: {meetingId}, 参与者ID: {participantId}");
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
                        transcriptionData[kvp.Key] = kvp.Value;
                    }
                }
                
                var json = JsonSerializer.Serialize(transcriptionData, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false
                });
                
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
                
                var summaryJson = JsonSerializer.Serialize(summaryData, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false
                });
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
        /// 获取会议全量语音识别结果
        /// </summary>
        public async Task<string> GetMeetingFullTranscriptionAsync(string meetingId)
        {
            try
            {
                var transcriptionKey = $"meeting:{meetingId}:transcription";
                var transcription = await _database.StringGetAsync(transcriptionKey);
                
                return transcription.HasValue ? transcription.ToString() : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"获取会议全量语音识别结果失败 - 会议ID: {meetingId}");
                return null;
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
        /// 获取Redis连接统计信息
        /// </summary>
        public string GetConnectionInfo()
        {
            try
            {
                var endpoints = _redis?.GetEndPoints();
                if (endpoints != null && endpoints.Length > 0)
                {
                    var server = _redis.GetServer(endpoints[0]);
                    var info = new
                    {
                        IsConnected = _redis.IsConnected,
                        Endpoint = endpoints[0].ToString(),
                        Database = _database.Database,
                        ServerVersion = server.Version,
                        ConnectedClients = server.ClientList().Length
                    };
                    
                    return JsonSerializer.Serialize(info, new JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        WriteIndented = false
                    });
                }
                
                return "Redis连接信息不可用";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取Redis连接信息失败");
                return "获取连接信息失败";
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
                    WriteIndented = false
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
                    WriteIndented = false
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