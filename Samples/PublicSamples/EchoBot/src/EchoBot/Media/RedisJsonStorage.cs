using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using StackExchange.Redis;

public class RedisJsonStorage
{
    private readonly IDatabase _redisDb;
    
    // 初始化 Redis 连接
    public RedisJsonStorage()
    {
        string connectionString = "r-bp1qom3k4nhkeqcw2qpd.redis.rds.aliyuncs.com:6379,password=7iBgEs7gWJbO";
        var connection = ConnectionMultiplexer.Connect(connectionString);
        _redisDb = connection.GetDatabase();
    }
    
    /// <summary>
    /// 将对象序列化为 JSON 并存储到 Redis
    /// </summary>
    /// <typeparam name="T">对象类型</typeparam>
    /// <param name="key">Redis 键名</param>
    /// <param name="data">要存储的对象</param>
    /// <param name="expiry">过期时间</param>
    public void StoreAsJson<T>(string key, T data, TimeSpan? expiry = null) where T : class
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        
        // 将对象序列化为 JSON 字符串
        string json = SerializeToJson(data);
        
        // 存储到 Redis
        _redisDb.StringSet(key, json, expiry);
    }
    
    /// <summary>
    /// 从 Redis 读取 JSON 并反序列化为对象
    /// </summary>
    /// <typeparam name="T">目标对象类型</typeparam>
    /// <param name="key">Redis 键名</param>
    /// <returns>反序列化后的对象</returns>
    public T RetrieveFromJson<T>(string key) where T : class
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentNullException(nameof(key));
        
        // 从 Redis 读取 JSON 字符串
        string json = _redisDb.StringGet(key);
        
        if (string.IsNullOrEmpty(json))
            return null;
        
        // 反序列化为对象
        return DeserializeFromJson<T>(json);
    }
    
    /// <summary>
    /// 将对象序列化为 JSON 字符串
    /// </summary>
    private string SerializeToJson<T>(T data) where T : class
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var memoryStream = new MemoryStream())
        {
            serializer.WriteObject(memoryStream, data);
            return Encoding.UTF8.GetString(memoryStream.ToArray());
        }
    }
    
    /// <summary>
    /// 将 JSON 字符串反序列化为对象
    /// </summary>
    private T DeserializeFromJson<T>(string json) where T : class
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            return serializer.ReadObject(memoryStream) as T;
        }
    }
}

