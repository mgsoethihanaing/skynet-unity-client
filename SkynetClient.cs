// SkynetClient.cs
// Unity MonoBehaviour wrapper for SkynetTransport
// Handles reconnection, watchdog, and main-thread marshalling

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SkynetUnity
{
    /// <summary>
    /// Unity client for Skynet protocol.
    /// Auto-reconnects, monitors connection health, marshals packets to main thread.
    /// </summary>
    public class SkynetClient : MonoBehaviour
    {
        [Header("Network Config")]
        [SerializeField] private string _ip = "127.0.0.1";
        [SerializeField] private int _port = 10000;
        
        [Header("Health Monitoring")]
        [Tooltip("Seconds between PING packets")]
        [SerializeField] private float _pingInterval = 5f;
        
        [Tooltip("Seconds without PONG before considering connection dead")]
        [SerializeField] private float _connectionTimeout = 15f;
        
        [Header("Reconnection")]
        [Tooltip("Initial retry delay in milliseconds")]
        [SerializeField] private int _initialRetryDelayMs = 1000;
        
        [Tooltip("Maximum retry delay in milliseconds")]
        [SerializeField] private int _maxRetryDelayMs = 10000;

        private SkynetTransport _transport;
        private CancellationTokenSource _lifetimeCts;
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        
        private bool _isIntentionallyConnected;
        private int _reconnectAttempts;

        /// <summary>
        /// Current connection status (best-effort).
        /// </summary>
        public bool IsConnected => _transport != null && _transport.IsConnected;

        public string uid;

        private void Start()
        {
            _lifetimeCts = new CancellationTokenSource();
            _transport = new SkynetTransport();

            // Bind events (invoked on background threads)
            _transport.OnPacketReceived += HandlePacketBackground;
            _transport.OnError += (msg) => Enqueue(() => Debug.LogError($"[Skynet] {msg}"));
            _transport.OnDisconnected += () => Enqueue(OnDisconnected);

            _isIntentionallyConnected = false;
        }

        public void Connect()
        {
            if (_isIntentionallyConnected) return;

            _isIntentionallyConnected = true;
            ConnectAndMonitorLoop(_lifetimeCts.Token).SafeFireAndForget();
        }


        /// <summary>
        /// Main async loop: handles connection, reconnection, and watchdog monitoring.
        /// Runs until GameObject is destroyed.
        /// </summary>
        private async Task ConnectAndMonitorLoop(CancellationToken token)
        {
            int retryDelay = _initialRetryDelayMs;

            while (!token.IsCancellationRequested && _isIntentionallyConnected)
            {
                try
                {
                    // Connection phase
                    if (!_transport.IsConnected)
                    {
                        Debug.Log($"[Skynet] Connecting to {_ip}:{_port} (attempt {_reconnectAttempts + 1})...");
                        
                        await _transport.ConnectAsync(_ip, _port, 5000);
                        
                        retryDelay = _initialRetryDelayMs;
                        _reconnectAttempts = 0;
                        
                        Enqueue(OnConnected);
                    }

                    // Monitoring phase - check pong watchdog
                    if (_transport.IsConnected)
                    {
                        long lastPongTicks = _transport.LastPongTicks;
                        double secondsSincePong = (DateTime.UtcNow.Ticks - lastPongTicks) / (double)TimeSpan.TicksPerSecond;

                        if (secondsSincePong > _connectionTimeout)
                        {
                            Debug.LogWarning($"[Skynet] Connection timeout: No PONG for {secondsSincePong:F1}s");
                            _transport.Dispose();
                            continue; // Will reconnect on next iteration
                        }

                        await Task.Delay(1000, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    _reconnectAttempts++;
                    Debug.LogWarning($"[Skynet] Connection failed: {e.Message}. Retrying in {retryDelay}ms...");
                    
                    try
                    {
                        await Task.Delay(retryDelay, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Exponential backoff with jitter to prevent thundering herd
                    retryDelay = Math.Min(retryDelay * 2, _maxRetryDelayMs);
                    retryDelay += UnityEngine.Random.Range(-100, 100);
                }
            }

            Debug.Log("[Skynet] Connection loop terminated");
        }

        private void OnConnected()
        {
            Debug.Log("[Skynet] Connected successfully!");
            _reconnectAttempts = 0;
            
            // Send login request
            string userId = $"Unity-{SystemInfo.deviceUniqueIdentifier.Substring(0, 8)}";
            uid = userId;
            string token = Guid.NewGuid().ToString("N");
            Send(OpCode.LoginReq, $"{userId}/{token}");
            
            // Start ping heartbeat
            CancelInvoke(nameof(SendPing));
            InvokeRepeating(nameof(SendPing), 0f, _pingInterval);
        }

        private void OnDisconnected()
        {
            Debug.LogWarning("[Skynet] Disconnected");
            CancelInvoke(nameof(SendPing));
        }

        private void SendPing()
        {
            if (_transport.IsConnected)
            {
                Send(OpCode.Ping, DateTime.UtcNow.ToString("o"));
            }
        }

        /// <summary>
        /// Packet handler invoked on background thread.
        /// Pong is handled immediately; other packets marshalled to main thread.
        /// </summary>
        private void HandlePacketBackground(ushort cmdId, string body)
        {
            OpCode op = (OpCode)cmdId;
            
            // Handle pong immediately on background thread (no Unity API calls)
            if (op == OpCode.Pong)
            {
                _transport.UpdateLastPong();
                return;
            }

            // Marshal other packets to main thread
            Enqueue(() => HandlePacketOnMainThread(op, body));
        }

        /// <summary>
        /// Handle game packets on main thread (safe for Unity API calls).
        /// Override this method or add event handlers for custom logic.
        /// </summary>
        protected virtual void HandlePacketOnMainThread(OpCode op, string body)
        {
            Debug.Log($"[Skynet] Packet received: {op}");
        }

        /// <summary>
        /// Send packet to server. Thread-safe. Fire-and-forget safe.
        /// </summary>
        public void Send(OpCode op, string body)
        {
            _transport.SendAsync(op, body).SafeFireAndForget(ex =>
            {
                Debug.LogError($"[Skynet] Send failed: {ex.Message}");
            });
        }

        /// <summary>
        /// Gracefully disconnect (stops auto-reconnect).
        /// </summary>
        public void Disconnect()
        {
            _isIntentionallyConnected = false;
            _transport?.Dispose();
            CancelInvoke(nameof(SendPing));
        }

        private void Enqueue(Action action) => _mainThreadQueue.Enqueue(action);

        private void Update()
        {
            // Adaptive batch processing prevents frame stalls
            int maxPerFrame = _mainThreadQueue.Count > 100 ? 100 : 50;
            int processed = 0;
            
            while (processed < maxPerFrame && _mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Skynet] Main thread handler error: {e}");
                }
                processed++;
            }
        }

        private void OnDestroy()
        {
            _isIntentionallyConnected = false;
            _lifetimeCts?.Cancel();
            
            CancelInvoke();
            
            _transport?.Dispose();
            _lifetimeCts?.Dispose();
        }

        private void OnApplicationQuit()
        {
            OnDestroy();
        }
    }
}