# EchoBot Redis 集成指南

## 概述

EchoBot 现在支持 Redis 集成，用于存储会议数据、活动记录和参与者信息。这提供了更好的数据持久化和查询能力。

**🚀 已预配置阿里云Redis连接字符串！**

## 功能特性

### 🎯 会议数据管理
- **会议活动记录**: 自动保存语音识别结果到Redis
- **会议信息缓存**: 存储会议元数据和状态信息
- **参与者信息**: 记录参与者的详细信息和状态

### 🔄 数据持久化
- **自动过期**: 数据默认24小时后自动过期
- **高性能**: 使用Redis List和String数据结构
- **容错处理**: 优雅处理Redis连接失败的情况

### 🌟 阿里云Redis优化
- **连接池管理**: 优化的连接池配置
- **重连策略**: 智能的重连和重试机制
- **性能监控**: 内置连接性能测试
- **事件监控**: 实时连接状态监控

## 安装和配置

### 1. 安装 Redis 包

```bash
cd Samples/PublicSamples/EchoBot/src/EchoBot
dotnet add package StackExchange.Redis
```

### 2. 配置 Redis 连接

在 `appsettings.json` 中添加Redis配置：

```json
{
  "AppSettings": {
    "EnableRedis": true,
    "RedisConnectionString": "r-bp1qom3k4nhkeqcw2qpd.redis.rds.aliyuncs.com:6379,password=7iBgEs7gWJbO"
  }
}
```

或者在环境变量中设置：

```bash
export AppSettings__EnableRedis=true
export AppSettings__RedisConnectionString="r-bp1qom3k4nhkeqcw2qpd.redis.rds.aliyuncs.com:6379,password=7iBgEs7gWJbO"
```

### 3. 阿里云Redis特定配置

项目已预配置了阿里云Redis的连接字符串：

```csharp
// 在RedisConfig.cs中已预配置
public string ConnectionString { get; set; } = "r-bp1qom3k4nhkeqcw2qpd.redis.rds.aliyuncs.com:6379,password=7iBgEs7gWJbO";
```

**⚠️ 重要提示**: 
- 请确保您的阿里云Redis实例已启动
- 检查安全组设置，确保允许从您的应用服务器访问Redis端口
- 密码已包含在连接字符串中，请妥善保管

### 3. Redis 连接字符串格式

```
# 本地Redis（无密码）
localhost:6379

# 本地Redis（有密码）
localhost:6379,password=your_password

# 远程Redis（SSL）
your-redis-server.com:6380,password=your_password,ssl=true

# Azure Redis Cache
your-cache.redis.cache.windows.net:6380,password=your_access_key,ssl=true
```

## 使用方法

### 自动集成

Redis服务会在 `SpeechService` 初始化时自动启动，无需额外代码：

```csharp
// 在SpeechService构造函数中自动初始化
if (!string.IsNullOrEmpty(settings.RedisConnectionString))
{
    this._redisService = new RedisService(settings.RedisConnectionString, logger);
    this._webSocketClient.SetRedisService(this._redisService);
}
```

### 手动使用Redis服务

```csharp
// 创建Redis服务实例
var redisService = new RedisService("localhost:6379", logger);

// 保存会议活动
await redisService.SaveMeetingActivityAsync(
    meetingId: "meeting-123", 
    callId: "call-456", 
    activity: "用户说：你好", 
    type: "Transcription"
);

// 获取会议活动
var activities = await redisService.GetMeetingActivitiesAsync("meeting-123", 10);

// 保存会议信息
await redisService.SaveMeetingInfoAsync("meeting-123", "call-456", meetingInfoJson);

// 检查连接状态
if (redisService.IsConnected())
{
    var info = redisService.GetConnectionInfo();
    Console.WriteLine($"Redis连接信息: {info}");
}
```

## 数据结构

### Redis Key 命名规范

```
meeting:{meetingId}:activities    # 会议活动列表
meeting:{meetingId}:info          # 会议信息
meeting:{meetingId}:participants:{participantId}  # 参与者信息
```

### 数据格式示例

#### 会议活动记录
```json
{
  "MeetingId": "meeting-123",
  "CallId": "call-456",
  "Activity": "用户说：你好",
  "Type": "Transcription",
  "Timestamp": "2024-01-15T10:30:00Z"
}
```

#### 会议信息
```json
{
  "MeetingId": "meeting-123",
  "CallId": "call-456",
  "CallState": "Established",
  "ParticipantsCount": 3,
  "Timestamp": "2024-01-15T10:30:00Z"
}
```

## 监控和调试

### 连接状态检查

```csharp
// 检查Redis连接状态
if (_redisService.IsConnected())
{
    var info = _redisService.GetConnectionInfo();
    _logger.LogInformation($"Redis状态: {info}");
}
else
{
    _logger.LogWarning("Redis连接已断开");
}
```

### 日志输出

Redis操作会在控制台输出详细日志：

```
[Redis] 会议活动已保存到Redis - 会议ID: meeting-123
[Redis] 会议信息已保存到Redis - 会议ID: meeting-123
[Redis] 获取会议活动失败 - 会议ID: meeting-123
```

## 故障排除

### 常见问题

1. **连接失败**
   - 检查Redis服务器是否运行
   - 验证连接字符串格式
   - 确认防火墙设置

2. **权限错误**
   - 检查Redis密码是否正确
   - 确认用户权限设置

3. **性能问题**
   - 监控Redis内存使用
   - 检查网络延迟
   - 优化数据结构

### 调试模式

启用详细日志记录：

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "EchoBot.Media.RedisService": "Debug"
    }
  }
}
```

## 最佳实践

### 1. 连接管理
- 使用连接池管理Redis连接
- 实现重连机制
- 监控连接健康状态

### 2. 数据管理
- 设置合理的过期时间
- 使用压缩存储大量数据
- 实现数据备份策略

### 3. 性能优化
- 批量操作减少网络往返
- 使用管道（Pipeline）提高吞吐量
- 合理设置缓存策略

## 扩展功能

### 自定义Redis操作

```csharp
public class CustomRedisService : RedisService
{
    public CustomRedisService(string connectionString, ILogger logger) 
        : base(connectionString, logger)
    {
    }

    // 添加自定义方法
    public async Task SaveCustomDataAsync(string key, string data)
    {
        await _database.StringSetAsync(key, data);
    }
}
```

### 集成其他服务

Redis服务可以轻松集成到其他组件中：

```csharp
// 在CallHandler中使用
public class CallHandler
{
    private readonly RedisService _redisService;

    public CallHandler(ICall call, AppSettings settings, ILogger logger)
    {
        if (settings.EnableRedis)
        {
            _redisService = new RedisService(settings.RedisConnectionString, logger);
        }
    }
}
```

## 总结

Redis集成为EchoBot提供了强大的数据存储和缓存能力，支持：
- 会议数据的持久化存储
- 高性能的数据查询
- 灵活的扩展性
- 可靠的容错处理

通过简单的配置，您就可以启用这些功能，提升EchoBot的数据管理能力。 