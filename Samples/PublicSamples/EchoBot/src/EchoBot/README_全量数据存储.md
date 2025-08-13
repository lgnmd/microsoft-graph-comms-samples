# 会议全量语音识别结果存储

## 概述

本系统采用全量数据存储方式，将语音识别的完整结果作为单一记录存储，而不是存储多条增量记录。每次新的识别结果会覆盖之前的数据，确保Redis中始终保存最新的完整转录内容。

## 存储结构

### 主要Key
- **`meeting:{meetingId}:transcription`** - 存储会议的全量语音识别结果
- **`meeting:{meetingId}:summary`** - 存储会议摘要信息
- **`meeting:{meetingId}:timeindex`** - 存储时间索引（用于查询）

### 数据格式

#### 转录结果数据结构
```json
{
  "MeetingId": "meeting123",
  "CallId": "call456",
  "Transcription": "完整的语音识别文本内容",
  "Type": "FullTranscription",
  "Timestamp": "2025-08-13T22:30:00Z",
  "MessageId": "guid-123",
  "Source": "WebSocket",
  "IsFullData": true,
  "UpdatedAt": "2025-08-13T22:30:00Z",
  "WebSocketMessage": "原始WebSocket消息",
  "ProcessedAt": "2025-08-13T22:30:00Z",
  "SessionId": "session-789"
}
```

#### 会议摘要数据结构
```json
{
  "MeetingId": "meeting123",
  "CallId": "call456",
  "LastTranscription": "最新的完整转录内容",
  "LastTranscriptionTime": "2025-08-13T22:30:00Z",
  "TranscriptionLength": 150,
  "Status": "Active",
  "UpdatedAt": "2025-08-13T22:30:00Z",
  "StorageType": "FullData"
}
```

## 核心方法

### 1. 保存全量转录结果
```csharp
// 保存会议全量语音识别结果（覆盖存储）
var success = await _redisService.SaveMeetingFullTranscriptionAsync(
    meetingId, 
    callId, 
    transcription, 
    additionalData
);
```

### 2. 获取全量转录结果
```csharp
// 获取会议全量语音识别结果
var transcription = await _redisService.GetMeetingFullTranscriptionAsync(meetingId);
```

## 工作流程

### 1. 语音识别处理
```
WebSocket消息 → 解析JSON → 提取转录文本 → 存储到Redis
```

### 2. 数据存储策略
- **覆盖存储**: 每次新的识别结果覆盖之前的记录
- **全量数据**: 保存完整的转录内容，不是增量追加
- **实时更新**: 每次收到新消息立即更新存储

### 3. 自动过期
- 所有数据24小时后自动过期
- 避免Redis内存无限增长

## 使用场景

### 1. 实时会议转录
- 会议进行中实时更新转录结果
- 每次更新都是完整的转录内容

### 2. 会议记录查询
- 查询特定会议的最新转录结果
- 获取转录内容的长度和更新时间

### 3. 会议状态监控
- 监控会议是否活跃
- 跟踪转录内容的更新频率

## 优势

### 1. 存储效率
- 避免存储重复的增量数据
- 减少Redis内存占用

### 2. 查询性能
- 直接获取最新完整结果
- 无需合并多条记录

### 3. 数据一致性
- 始终保存最新的完整数据
- 避免数据碎片化

### 4. 维护简单
- 自动过期清理
- 无需手动管理历史数据

## 配置说明

### Redis连接配置
```json
{
  "AppSettings": {
    "EnableRedis": true,
    "RedisConnectionString": "r-bp1qom3k4nhkeqcw2qpd.redis.rds.aliyuncs.com:6379,password=7iBgEs7gWJbO"
  }
}
```

### 阿里云Redis优化配置
- 连接超时: 10秒
- 同步超时: 10秒
- 重连策略: 指数重试
- 连接重试: 3次

## 监控和日志

### 控制台输出
```
[Redis] 会议全量语音识别结果已以key 'meeting:meeting123:transcription' 存入Redis - 会议ID: meeting123
[Redis] 会议摘要: {...}
```

### 日志记录
- 成功存储: 记录会议ID和转录长度
- 存储失败: 记录错误信息和会议ID
- 连接状态: 监控Redis连接状态

## 注意事项

1. **数据覆盖**: 每次新数据会完全覆盖旧数据
2. **存储限制**: 转录内容长度受Redis字符串限制
3. **过期时间**: 24小时后数据自动删除
4. **错误处理**: 存储失败不影响WebSocket连接

## 扩展功能

### 1. 历史版本
可以扩展支持保存多个历史版本：
```csharp
// 保存历史版本
await SaveMeetingTranscriptionVersionAsync(meetingId, callId, transcription, version);
```

### 2. 数据压缩
对于长转录内容，可以添加压缩支持：
```csharp
// 压缩存储
await SaveMeetingCompressedTranscriptionAsync(meetingId, callId, transcription);
```

### 3. 批量操作
支持批量获取多个会议的转录结果：
```csharp
// 批量获取
var transcriptions = await GetMultipleMeetingTranscriptionsAsync(meetingIds);
``` 