using System.ComponentModel.DataAnnotations;

namespace EchoBot
{
    /// <summary>
    /// Redis配置类，专门用于阿里云Redis配置
    /// </summary>
    public class RedisConfig
    {
        /// <summary>
        /// 是否启用Redis功能
        /// </summary>
        public bool EnableRedis { get; set; } = true;

        /// <summary>
        /// Redis连接字符串
        /// </summary>
        [Required]
        public string ConnectionString { get; set; } = "r-bp1qom3k4nhkeqcw2qpd.redis.rds.aliyuncs.com:6379,password=7iBgEs7gWJbO";

        /// <summary>
        /// 连接超时时间（毫秒）
        /// </summary>
        public int ConnectTimeout { get; set; } = 10000;

        /// <summary>
        /// 同步超时时间（毫秒）
        /// </summary>
        public int SyncTimeout { get; set; } = 10000;

        /// <summary>
        /// 响应超时时间（毫秒）
        /// </summary>
        public int ResponseTimeout { get; set; } = 10000;

        /// <summary>
        /// 连接重试次数
        /// </summary>
        public int ConnectRetry { get; set; } = 3;

        /// <summary>
        /// 重连重试策略延迟（毫秒）
        /// </summary>
        public int ReconnectRetryDelay { get; set; } = 5000;

        /// <summary>
        /// 是否启用SSL
        /// </summary>
        public bool EnableSsl { get; set; } = false;

        /// <summary>
        /// 数据库编号
        /// </summary>
        public int Database { get; set; } = 0;

        /// <summary>
        /// 连接池大小
        /// </summary>
        public int ConnectionPoolSize { get; set; } = 50;

        /// <summary>
        /// 数据过期时间（小时）
        /// </summary>
        public int DataExpirationHours { get; set; } = 24;

        /// <summary>
        /// 是否启用连接监控
        /// </summary>
        public bool EnableConnectionMonitoring { get; set; } = true;

        /// <summary>
        /// 连接监控间隔（秒）
        /// </summary>
        public int ConnectionMonitorInterval { get; set; } = 30;

        /// <summary>
        /// 获取完整的Redis连接字符串
        /// </summary>
        public string GetFullConnectionString()
        {
            var baseString = ConnectionString;
            
            // 添加SSL配置
            if (EnableSsl && !baseString.Contains("ssl="))
            {
                baseString += ",ssl=true";
            }
            
            // 添加数据库配置
            if (!baseString.Contains("defaultDatabase="))
            {
                baseString += $",defaultDatabase={Database}";
            }
            
            // 添加连接池配置
            if (!baseString.Contains("connectTimeout="))
            {
                baseString += $",connectTimeout={ConnectTimeout}";
            }
            
            if (!baseString.Contains("syncTimeout="))
            {
                baseString += $",syncTimeout={SyncTimeout}";
            }
            
            if (!baseString.Contains("responseTimeout="))
            {
                baseString += $",responseTimeout={ResponseTimeout}";
            }
            
            if (!baseString.Contains("connectRetry="))
            {
                baseString += $",connectRetry={ConnectRetry}";
            }
            
            return baseString;
        }

        /// <summary>
        /// 验证配置是否有效
        /// </summary>
        public bool IsValid()
        {
            return !string.IsNullOrEmpty(ConnectionString) && 
                   ConnectionString.Contains(":") && 
                   ConnectionString.Contains("password=");
        }

        /// <summary>
        /// 获取配置摘要信息
        /// </summary>
        public string GetConfigSummary()
        {
            return $"Redis配置 - 启用: {EnableRedis}, 端点: {ConnectionString.Split(',')[0]}, 数据库: {Database}, 过期时间: {DataExpirationHours}小时";
        }
    }
} 