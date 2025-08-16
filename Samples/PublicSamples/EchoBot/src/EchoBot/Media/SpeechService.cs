using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Skype.Bots.Media;
using System.Runtime.InteropServices;
using Microsoft.Graph.Communications.Calls;
using System.Threading.Channels;

namespace EchoBot.Media
{
	/// <summary>
	/// ASR客户端类型枚举
	/// </summary>
	public enum AsrClientType
	{
		/// <summary>
		/// 火山引擎ASR客户端
		/// </summary>
		Volcengine,
		
		/// <summary>
		/// WebSocket客户端
		/// </summary>
		WebSocket,
		
		/// <summary>
		/// 不使用ASR客户端
		/// </summary>
		None
	}

	/// <summary>
	/// Class SpeechService.
	/// </summary>
	public class SpeechService
	{
		/// <summary>
		/// The is the indicator if the media stream is running
		/// </summary>
		private bool _isRunning = false;
		/// <summary>
		/// The is draining indicator
		/// </summary>
		protected bool _isDraining;

		/// <summary>
		/// The logger
		/// </summary>
		private readonly ILogger _logger;
		private readonly PushAudioInputStream _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
		private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();

		private readonly SpeechConfig _speechConfig;
		private SpeechRecognizer _recognizer;
		private readonly SpeechSynthesizer _synthesizer;

		// ASR客户端相关字段
		private AsrClientType _asrClientType;
		private readonly WebSocketClient? _webSocketClient;
		private readonly ASRController.Builder? _asrClient;
		private readonly RedisService? _redisService; // 添加Redis服务
		// Volcengine 实时流相关
		private ASRController? _asrController;
		private Channel<byte[]>? _asrAudioChannel;
		private bool _asrStreamingStarted = false;
		private byte[] _asrPendingChunk = Array.Empty<byte>();
		private int _asrPendingOffset = 0;
		
		// 会议信息
		private readonly string? _meetingId;
		private readonly List<IParticipant>? _participants;

		private string _meetingText = "";
		/// <summary>
		/// 获取当前使用的ASR客户端类型
		/// </summary>
		public AsrClientType CurrentAsrClientType => _asrClientType;

		/// <summary>
		/// Initializes a new instance of the <see cref="SpeechService" /> class.
		/// </summary>
		/// <param name="settings">应用设置</param>
		/// <param name="logger">日志记录器</param>
		/// <param name="participants">参与者列表</param>
		/// <param name="meetingId">会议ID</param>
		/// <param name="callId">通话ID</param>
		/// <param name="asrClientType">ASR客户端类型，默认为None</param>
		public SpeechService(AppSettings settings, ILogger logger, List<IParticipant> participants, string meetingId = null, string callId = null, AsrClientType asrClientType = AsrClientType.None)
		{
			_logger = logger;
			_asrClientType = asrClientType;
			_meetingId = meetingId;
			_participants = participants;

			_speechConfig = SpeechConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
			_speechConfig.SpeechSynthesisLanguage = settings.BotLanguage;
			_speechConfig.SpeechRecognitionLanguage = settings.BotLanguage;

			var audioConfig = AudioConfig.FromStreamOutput(_audioOutputStream);
			_synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig);

			// 根据ASR客户端类型初始化相应的客户端
			switch (_asrClientType)
			{
				case AsrClientType.Volcengine:
					if (string.IsNullOrEmpty(settings.VolicengineAppId) || string.IsNullOrEmpty(settings.VolicengineAppSecret))
					{
						_logger.LogWarning("火山引擎ASR配置不完整，AppId或AppSecret为空");
						_asrClientType = AsrClientType.None;
					}
					else
					{
						_asrClient = new ASRController.Builder();
						_asrClient.appid = settings.TentAppId;
						_asrClient.tentid = settings.TentAppSecretId;
						_asrClient.tentkey = settings.TentAppSecret;
						_asrClient.engine_model_type = "16k_zh-TW";
						
						_asrClient.needvad = 1;
						_asrAudioChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
						_logger.LogInformation("火山引擎ASR客户端已初始化");
					}
					break;
					
				case AsrClientType.WebSocket:
					this._webSocketClient = new WebSocketClient();
					_logger.LogInformation("WebSocket客户端已初始化");
					break;
					
				case AsrClientType.None:
				default:
					_logger.LogInformation("未使用ASR客户端");
					break;
			}
			
			// 设置会议信息
			if (!string.IsNullOrEmpty(meetingId))
			{
				if (this._webSocketClient != null)
				{
					this._webSocketClient.SetMeetingInfo(participants, meetingId, callId);
					_logger.LogInformation($"WebSocket客户端已设置会议信息 - 会议ID: {meetingId}, 通话ID: {callId ?? "未设置"}");
				}
				
				// if (this._asrClient != null)
				// {
				//     this._asrClient.SetMeetingInfo(participants, meetingId, callId);
				//     _logger.LogInformation($"火山引擎ASR客户端已设置会议信息 - 会议ID: {meetingId}, 通话ID: {callId ?? "未设置"}");
				// }
			}
			
			// 初始化Redis服务（如果配置了Redis连接字符串）
			try
			{
				if (!string.IsNullOrEmpty(settings.RedisConnectionString))
				{
					this._redisService = new RedisService(settings.RedisConnectionString, logger);
					
					if (this._webSocketClient != null)
					{
						this._webSocketClient.SetRedisService(this._redisService);
					}
					
					// if (this._volcengineAsrClient != null)
					// {
					//     this._volcengineAsrClient.SetRedisService(this._redisService);
					// }
					
					_logger.LogInformation("Redis服务已初始化");
					
					// 测试Redis连接
					_ = Task.Run(async () =>
					{
						try
						{
							await Task.Delay(2000); // 等待2秒让连接稳定
							var testResult = await this._redisService.TestConnectionAsync();
							_logger.LogInformation($"Redis连接测试结果: {testResult}");
						}
						catch (Exception ex)
						{
							_logger.LogWarning(ex, "Redis连接测试失败");
						}
					});
				}
				else
				{
					_logger.LogInformation("Redis连接字符串未配置，跳过Redis服务初始化");
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Redis服务初始化失败，将继续运行但不使用Redis功能");
			}
			
			// 启动后台任务来初始化连接和监听（仅当有ASR客户端时）
			if (_asrClientType != AsrClientType.None)
			{
				_ = Task.Run(async () =>
				{
					try
					{
						await InitializeConnectionsAsync();
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "连接初始化失败");
					}
				});
			}
		}

		/// <summary>
		/// Appends the audio buffer.
		/// </summary>
		/// <param name="audioBuffer"></param>
		public async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer)
		{
			if (!_isRunning)
			{
				Start();
				await ProcessSpeech();
			}

			try
			{
				// audio for a 1:1 call
				var bufferLength = audioBuffer.Length;
				if (bufferLength > 0)
				{
					var buffer = new byte[bufferLength];
					Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

					_audioInputStream.Write(buffer);

					// 根据ASR客户端类型发送音频数据
					switch (_asrClientType)
					{
						case AsrClientType.WebSocket:
							await SendAudioToWebSocket(buffer);
							break;
							
						case AsrClientType.Volcengine:
							// 实时流：将音频写入通道，并确保仅启动一次ASR流
							try
							{
								if (_asrAudioChannel == null)
								{
									_asrAudioChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
								}
								if (!_asrStreamingStarted && _asrClient != null)
								{
									var ctl = this._asrClient.build();
									_asrController = ctl;
									_asrStreamingStarted = true;
									_ = Task.Run(async () =>
									{
										try
										{
											_logger.LogInformation("启动火山引擎ASR流式任务...");
											
											// 启动ASR流，使用正确的回调签名
											await ctl.startAsync(
												// 音频读取回调 - DataSource委托，不返回值
												async (byte[] buf, CancellationToken token) =>
												{
													try
													{
														var count = await FillBufferFromAsrChannelAsync(buf, token);
														if (count <= 0)
														{
															_logger.LogInformation("ASR音频流结束，停止识别");
															await ctl.stopAsync();
														}
														// DataSource委托不返回值，所以这里不return
													}
													catch (OperationCanceledException)
													{
														_logger.LogInformation("ASR音频读取被取消");
													}
													catch (Exception ex)
													{
														_logger.LogError(ex, "ASR音频读取异常");
													}
												},
												// 识别结果回调 - MessageHandler委托，不返回值
												async (string msg) =>
												{
													try
													{
														if (!string.IsNullOrEmpty(msg))
														{
															_logger.LogInformation($"ASR识别结果: {msg}");
															
															// 解析JSON结果
															try
															{
																var result = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(msg);
																
																// 检查code是否为0（成功）
																if (result.ContainsKey("code") && result["code"].ToString() == "0")
																{
																	// 检查是否有result字段
																	if (result.ContainsKey("result") && result["result"] is System.Text.Json.JsonElement resultElement)
																	{
																		var resultDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(resultElement.GetRawText());
																		
																		// 检查slice_type是否为2
																		if (resultDict.ContainsKey("slice_type") && resultDict["slice_type"].ToString() == "2")
																		{
																			// 获取文本内容
																			if (resultDict.ContainsKey("voice_text_str"))
																			{
																				var text = resultDict["voice_text_str"].ToString();
																				_meetingText = _meetingText+text;
																				_logger.LogInformation($"检测到最终识别结果: {_meetingText}");
																				
																				// 将结果存入Redis
																				if (_redisService != null)
																				{
																					try
																					{
																						// 获取当前发言者的displayName
																						var displayNames = new List<string>();
																						if (_participants != null && _participants.Count > 0)
																						{
																							// 遍历所有参与者，收集他们的显示名称
																							foreach (var participant in _participants)
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
																						
																						// 使用meeting:meetingId:transcription作为key
																						var key = $"meeting:{_meetingId ?? "unknown"}:transcription";
																						
																						// 创建结果对象
																						var asrResult = new
																						{
																							text = _meetingText,
																							timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
																							voice_id = result.ContainsKey("voice_id") ? result["voice_id"].ToString() : "",
																							message_id = result.ContainsKey("message_id") ? result["message_id"].ToString() : "",
																							start_time = resultDict.ContainsKey("start_time") ? resultDict["start_time"].ToString() : "",
																							end_time = resultDict.ContainsKey("end_time") ? resultDict["end_time"].ToString() : "",
																							display_name = displayNames,
																							meeting_id = _meetingId ?? "",
																							slice_type = resultDict.ContainsKey("slice_type") ? resultDict["slice_type"].ToString() : ""
																						};
																						
																						// 序列化为JSON并存入Redis
																						var jsonResult = System.Text.Json.JsonSerializer.Serialize(asrResult);
																						await _redisService.SetAsync(key, jsonResult, TimeSpan.FromHours(24)); // 保存24小时
																						
																						_logger.LogInformation($"ASR结果已存入Redis，key: {key}, 内容: {jsonResult}");
																					}
																					catch (Exception redisEx)
																					{
																						_logger.LogError(redisEx, "将ASR结果存入Redis时出错");
																					}
																				}
																				else
																				{
																					_logger.LogWarning("Redis服务未初始化，无法保存ASR结果");
																				}
																			}
																		}
																	}
																}
															}
															catch (System.Text.Json.JsonException jsonEx)
															{
																_logger.LogWarning(jsonEx, "解析ASR识别结果JSON时出错");
															}
														}
													}
													catch (Exception ex)
													{
														_logger.LogError(ex, "处理ASR识别结果时异常");
													}
												}
											);
											
											// 等待ASR流完成
											await ctl.waitAsync();
											_logger.LogInformation("火山引擎ASR流式任务已完成");
										}
										catch (OperationCanceledException)
										{
											_logger.LogInformation("火山引擎ASR流式任务被取消");
										}
										catch (Exception ex)
										{
											_logger.LogError(ex, "火山引擎ASR流式任务异常");
										}
										finally
										{
											// 确保资源被正确清理
											try
											{
												_asrStreamingStarted = false;
												_asrController = null;
												_logger.LogInformation("火山引擎ASR流式任务资源已清理");
											}
											catch (Exception cleanupEx)
											{
												_logger.LogError(cleanupEx, "清理ASR流式任务资源时异常");
											}
										}
									});
								}
								
								// 将音频数据写入通道
								if (_asrAudioChannel != null)
								{
									if (!_asrAudioChannel.Writer.TryWrite(buffer))
									{
										await _asrAudioChannel.Writer.WriteAsync(buffer);
									}
								}
							}
							catch (Exception e)
							{
								_logger.LogError(e, "写入ASR实时流失败");
								// 如果出现异常，重置流状态
								_asrStreamingStarted = false;
								_asrController = null;
							}
							break;
							
						case AsrClientType.None:
						default:
							_logger.LogDebug("未配置ASR客户端，跳过音频发送");
							break;
					}
				}
			}
			catch (Exception e)
			{
				_logger.LogError(e, "Exception happend writing to input stream");
			}
		}
		
		/// <summary>
		/// 发送音频数据到WebSocket
		/// </summary>
		private async Task SendAudioToWebSocket(byte[] buffer)
		{
			try
			{
				if (this._webSocketClient?.IsConnected == true)
				{
					this._webSocketClient.SendMessage(buffer);
				}
				else
				{
					_logger.LogDebug("WebSocket未连接，跳过音频发送");
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "发送音频到WebSocket时出错");
			}
		}
		
		public virtual void OnSendMediaBufferEventArgs(object sender, MediaStreamEventArgs e)
		{
			if (SendMediaBuffer != null)
			{
				SendMediaBuffer(this, e);
			}
		}

		public event EventHandler<MediaStreamEventArgs> SendMediaBuffer;

		/// <summary>
		/// Ends this instance.
		/// </summary>
		/// <returns>Task.</returns>
		public async Task ShutDownAsync()
		{
			if (!_isRunning)
			{
				return;
			}

			if (_isRunning)
			{
				try
				{
					_logger.LogInformation("开始关闭SpeechService...");
					
					// 停止语音识别
					if (_recognizer != null)
					{
						await _recognizer.StopContinuousRecognitionAsync();
						_recognizer.Dispose();
					}
					
					// 关闭音频流
					_audioInputStream.Close();
					_audioInputStream.Dispose();
					_audioOutputStream.Dispose();
					
					// 关闭语音合成器
					_synthesizer.Dispose();
					
					// 根据ASR客户端类型断开相应的连接
					switch (_asrClientType)
					{
						case AsrClientType.Volcengine:
							try
							{
								_logger.LogInformation("开始清理火山引擎ASR资源...");
								
								// 标记流已停止
								_asrStreamingStarted = false;
								
								// 完成音频通道写入
								if (_asrAudioChannel != null)
								{
									_asrAudioChannel.Writer.TryComplete();
									_logger.LogInformation("ASR音频通道已关闭");
								}
								
								// 停止ASR控制器
								if (_asrController != null)
								{
									try
									{
										await _asrController.stopAsync();
										_logger.LogInformation("火山引擎ASR控制器已停止");
									}
									catch (Exception stopEx)
									{
										_logger.LogWarning(stopEx, "停止火山引擎ASR控制器时出错");
									}
									finally
									{
										_asrController = null;
									}
								}
								
								_logger.LogInformation("火山引擎ASR资源清理完成");
							}
							catch (Exception ex)
							{
								_logger.LogWarning(ex, "断开火山引擎ASR流时出错");
							}
							break;
							
						case AsrClientType.WebSocket:
							if (this._webSocketClient != null)
							{
								try
								{
									await this._webSocketClient.Disconnect();
									_logger.LogInformation("WebSocket连接已断开");
								}
								catch (Exception ex)
								{
									_logger.LogWarning(ex, "断开WebSocket连接时出错");
								}
							}
							break;
							
						case AsrClientType.None:
						default:
							_logger.LogInformation("未配置ASR客户端，无需断开连接");
							break;
					}
					
					_isRunning = false;
					_logger.LogInformation("SpeechService已关闭");
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "关闭SpeechService时发生错误");
				}
			}
		}

		/// <summary>
		/// Starts this instance.
		/// </summary>
		private void Start()
		{
			if (!_isRunning)
			{
				_isRunning = true;
			}
		}

		/// <summary>
		/// Processes this instance.
		/// </summary>
		private async Task ProcessSpeech()
		{
			try
			{
				// 如果_recognizer为null，说明没有使用Azure Speech Services，直接返回
				if (_recognizer == null)
				{
					_logger.LogInformation("SpeechRecognizer未初始化，跳过Azure Speech Services处理");
					return;
				}

				var stopRecognition = new TaskCompletionSource<int>();

				_recognizer.Canceled += (s, e) =>
				{
					_logger.LogInformation($"CANCELED: Reason={e.Reason}");

					if (e.Reason == CancellationReason.Error)
					{
						_logger.LogInformation($"CANCELED: ErrorCode={e.ErrorCode}");
						_logger.LogInformation($"CANCELED: ErrorDetails={e.ErrorDetails}");
						_logger.LogInformation($"CANCELED: Did you update the subscription info?");
					}

					stopRecognition.TrySetResult(0);
				};



				_recognizer.SessionStopped += (s, e) =>
				{
					_logger.LogInformation("\nSession stopped event.");
					_logger.LogInformation("\nStop recognition.");
					stopRecognition.TrySetResult(0);
				};

				// Starts continuous recognition. Uses StopContinuousRecognitionAsync() to stop recognition.
				await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

				// Waits for completion.
				// Use Task.WaitAny to keep the task rooted.
				Task.WaitAny(new[] { stopRecognition.Task });

				// Stops recognition.
				await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
			}
			catch (ObjectDisposedException ex)
			{
				_logger.LogError(ex, "The queue processing task object has been disposed.");
			}
			catch (Exception ex)
			{
				// Catch all other exceptions and log
				_logger.LogError(ex, "Caught Exception");
			}

			_isDraining = false;
		}


		/// <summary>
		/// 启动后台任务来初始化WebSocket连接和监听
		/// </summary>
		private async Task InitializeConnectionsAsync()
		{
			try
			{
				_logger.LogInformation("开始初始化WebSocket和火山引擎ASR连接...");
				
				// 并行初始化两个连接
				var connectionTasks = new List<Task>();
				
				// WebSocket连接
				if (this._webSocketClient != null)
				{
					connectionTasks.Add(Task.Run(async () =>
					{
						try
						{
							await this._webSocketClient.Connect();
							await this._webSocketClient.StartListening();
							_logger.LogInformation("WebSocket连接初始化成功");
						}
						catch (Exception ex)
						{
							_logger.LogError(ex, "WebSocket连接初始化失败");
						}
					}));
				}
				
				// 等待所有连接完成
				await Task.WhenAll(connectionTasks);
				
				_logger.LogInformation("所有连接初始化完成");
				
				// 启动连接监控任务
				_ = Task.Run(async () => await MonitorConnectionsAsync());
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "连接初始化过程中发生错误");
			}
		}
		
		/// <summary>
		/// 监控连接状态
		/// </summary>
		private async Task MonitorConnectionsAsync()
		{
			while (_isRunning)
			{
				try
				{
					await Task.Delay(30000); // 每30秒检查一次
					
					// 检查WebSocket连接状态
					if (this._webSocketClient != null)
					{
						var webSocketStatus = this._webSocketClient.GetConnectionStatus();
						_logger.LogDebug($"WebSocket连接状态: {webSocketStatus}");
					}
					
					// 如果WebSocket连接断开，尝试重连
					if (this._webSocketClient != null && !this._webSocketClient.IsConnected)
					{
						_logger.LogWarning("检测到WebSocket连接断开，尝试重连...");
						await ReconnectWebSocketAsync();
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "连接监控过程中发生错误");
				}
			}
		}
		
		/// <summary>
		/// 重连WebSocket服务
		/// </summary>
		private async Task ReconnectWebSocketAsync()
		{
			try
			{
				_logger.LogInformation("开始重连WebSocket服务...");
				
				// 断开现有连接
				if (this._webSocketClient != null)
				{
					await this._webSocketClient.Disconnect();
				}
				
				// 等待一段时间
				await Task.Delay(2000);
				
				// 重新连接
				if (this._webSocketClient != null)
				{
					await this._webSocketClient.Connect();
					await this._webSocketClient.StartListening();
				}
				
				_logger.LogInformation("WebSocket重连成功");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "WebSocket重连失败");
			}
		}
		
		/// <summary>
		/// 手动重连WebSocket服务
		/// </summary>
		public async Task ReconnectWebSocket()
		{
			try
			{
				_logger.LogInformation("手动重连WebSocket服务...");
				await ReconnectWebSocketAsync();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "手动重连WebSocket服务失败");
				throw;
			}
		}

		private async Task<int> FillBufferFromAsrChannelAsync(byte[] destination, CancellationToken token)
		{
			try
			{
				int written = 0;
				while (written < destination.Length)
				{
					// 先消费未读完的分片
					if (_asrPendingChunk.Length - _asrPendingOffset > 0)
					{
						int toCopy = Math.Min(destination.Length - written, _asrPendingChunk.Length - _asrPendingOffset);
						Buffer.BlockCopy(_asrPendingChunk, _asrPendingOffset, destination, written, toCopy);
						_asrPendingOffset += toCopy;
						written += toCopy;
						if (_asrPendingOffset >= _asrPendingChunk.Length)
						{
							_asrPendingChunk = Array.Empty<byte>();
							_asrPendingOffset = 0;
						}
						continue;
					}

					if (_asrAudioChannel == null)
					{
						_logger.LogDebug("ASR音频通道为空，停止填充缓冲区");
						break;
					}

					// 等待新的音频分片，添加超时机制
					try
					{
						using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 5秒超时
						using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
						
						var nextChunk = await _asrAudioChannel.Reader.ReadAsync(combinedCts.Token);
						if (nextChunk != null && nextChunk.Length > 0)
						{
							_asrPendingChunk = nextChunk;
							_asrPendingOffset = 0;
						}
						else
						{
							_logger.LogDebug("收到空的音频分片，跳过");
							break;
						}
					}
					catch (OperationCanceledException) when (token.IsCancellationRequested)
					{
						_logger.LogDebug("ASR音频读取被取消");
						break;
					}
					catch (OperationCanceledException)
					{
						_logger.LogDebug("ASR音频读取超时");
						break;
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "从ASR通道读取音频分片时异常");
						break;
					}
				}

				return written;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "FillBufferFromAsrChannelAsync方法异常");
				return 0;
			}
		}
	}
}
