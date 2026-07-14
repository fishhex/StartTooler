using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace StartTooler.Services;

/// <summary>
/// v0.11 spec/09 §3: 网络在线/离线监控。
///
/// 启动后每 <see cref="_checkInterval"/> 跑一次:
///   1. 检查 <see cref="NetworkInterface"/> 是否有活跃连接
///   2. HTTP HEAD/GET 探活 OSS endpoint（HEAD 失败回退 GET）
///   3. 状态变化 → 触发 <see cref="NetworkStatusChanged"/> 事件（UI 线程）
///
/// 不依赖 .NET 的 <c>NetworkChange.NetworkAvailabilityChanged</c>（macOS 上不可靠）。
/// </summary>
public class NetworkStatusService : IDisposable
{
    private readonly string? _ossEndpoint;  // null = 跳过 OSS 探活（OSS 未配置）
    private readonly HttpClient _http;
    private readonly TimeSpan _checkInterval;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private bool _isOnline = true;
    /// <summary>当前是否在线</summary>
    public bool IsOnline
    {
        get => _isOnline;
        private set
        {
            if (_isOnline == value) return;
            _isOnline = value;
            Trace.WriteLine($"[Network] 状态变化 → {(value ? "在线" : "离线")}");
            NetworkStatusChanged?.Invoke(value);
        }
    }

    /// <summary>网络状态变化事件（UI 线程上抛）</summary>
    public event Action<bool>? NetworkStatusChanged;

    /// <param name="ossEndpoint">OSS endpoint URL（如 https://oss-cn-hangzhou.aliyuncs.com/）。传 null/空 = 跳过 OSS 探活</param>
    /// <param name="checkInterval">检测间隔，默认 30 秒</param>
    public NetworkStatusService(string? ossEndpoint, TimeSpan? checkInterval = null)
    {
        _ossEndpoint = string.IsNullOrWhiteSpace(ossEndpoint) ? null : ossEndpoint.TrimEnd('/') + "/";
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(30);
        _http = new HttpClient
        {
            // 单次超时 5 秒（spec §3.2）
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>启动周期性检测</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NetworkStatusService));
        if (_cts != null) return;  // 已启动

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 启动时先跑一次（不等第一个 interval）
        _ = Task.Run(async () =>
        {
            try
            {
                var online = await CheckNowAsync(ct);
                IsOnline = online;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Network] 首次检测异常: {ex.Message}");
            }

            // 周期循环
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_checkInterval, ct);
                    if (ct.IsCancellationRequested) break;
                    var online = await CheckNowAsync(ct);
                    IsOnline = online;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Network] 周期检测异常: {ex.Message}");
                }
            }
        }, ct);
    }

    /// <summary>手动立即检测一次</summary>
    public async Task<bool> CheckNowAsync(CancellationToken ct = default)
    {
        // Step 1: 网卡检测
        if (!HasActiveNetworkInterface())
        {
            return false;
        }

        // Step 2: OSS endpoint 探活（如果配置了）
        if (_ossEndpoint != null)
        {
            if (await ProbeEndpointAsync(_ossEndpoint, ct))
            {
                return true;
            }
            // 失败时若配置有 region，端点可能不同；不再尝试，让"离线"生效
            return false;
        }

        // 没配置 OSS endpoint → 只要网卡活跃就算在线
        return true;
    }

    private static bool HasActiveNetworkInterface()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Network] 网卡检测异常: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// 先 HEAD 探活，HEAD 405/501 等不支持时回退 GET。
    /// 200/2xx/3xx = 在线，其他/异常 = 离线。
    /// </summary>
    private async Task<bool> ProbeEndpointAsync(string url, CancellationToken ct)
    {
        // 第一次 HEAD
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            if (IsSuccessOrRedirect(resp.StatusCode)) return true;
            // HEAD 被拒（405/501/403）→ 回退 GET
            if (resp.StatusCode is System.Net.HttpStatusCode.MethodNotAllowed
                or System.Net.HttpStatusCode.NotImplemented
                or System.Net.HttpStatusCode.Forbidden)
            {
                // fall through to GET
            }
            else
            {
                return false;  // 4xx/5xx（非回退情形）= 离线
            }
        }
        catch (TaskCanceledException) { return false; }  // 超时
        catch (HttpRequestException) { return false; }

        // 回退 GET
        try
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
            return IsSuccessOrRedirect(resp.StatusCode);
        }
        catch (TaskCanceledException) { return false; }
        catch (HttpRequestException) { return false; }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Network] GET 探活异常: {ex.Message}");
            return false;
        }
    }

    private static bool IsSuccessOrRedirect(System.Net.HttpStatusCode code)
    {
        var n = (int)code;
        return n is >= 200 and < 400;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }
        _http.Dispose();
    }
}
