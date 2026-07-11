using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StartTooler.Services;

/// <summary>
    /// AI 配置连通性测试。发一个极小请求（max_tokens=16，prompt="say 'ok'"）验证：
    ///   - BaseUrl 可达
    ///   - 鉴权通过
    ///   - Model 存在
    ///
    /// 请求协议由调用方传入的 <paramref name="protocol"/> 字符串决定（"OpenAI" / "Anthropic"）：
    ///   - "Anthropic"：POST {base}/v1/messages，header x-api-key + anthropic-version
    ///   - "OpenAI"  ：POST {base}/chat/completions，header Authorization: Bearer
    ///
    /// v0.6 改：AITester 不再依赖 AIProviderMeta.ProtocolKind，由 SettingsViewModel 显式传入
    /// AIConfig.Protocol（UI 强制让用户选 OpenAI 或 Anthropic）。Custom 厂商特例已废除 —— 协议独立选。
    /// </summary>
    public static class AITester
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private const string AnthropicVersion = "2023-06-01";

        private static readonly HttpClient s_http = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 4,
        })
        {
            Timeout = RequestTimeout,
        };

        public sealed record TestResult(
            bool Success,
            string Message,
            int LatencyMs,
            string? ProtocolNote = null);

        public static async Task<TestResult> TestAsync(
            string protocol,
            string apiKey,
            string baseUrl,
            string model,
            string? prompt = null,
            CancellationToken ct = default)
        {
            // 输入校验（本地直接 fail，避免发无意义的请求）
            if (string.IsNullOrWhiteSpace(protocol))
                return new TestResult(false, "AI 协议未选择，请先在设置页选 OpenAI 或 Anthropic", 0);
            if (string.IsNullOrWhiteSpace(apiKey))
                return new TestResult(false, "API Key 为空，请先填写", 0);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return new TestResult(false, "Base URL 为空，请先填写", 0);
            if (string.IsNullOrWhiteSpace(model))
                return new TestResult(false, "Model 为空，请先填写", 0);

            // v0.11: 用户可自定义测试 prompt；空时用默认 "say 'ok'" 跟老行为一致
            var effectivePrompt = string.IsNullOrWhiteSpace(prompt) ? "say 'ok'" : prompt!;
            // 验证场景 max_tokens 给多一点，让用户自定义 prompt 能拿到完整回答
            var maxTokens = string.IsNullOrWhiteSpace(prompt) ? 16 : 256;

            var sw = Stopwatch.StartNew();
            try
            {
                var result = protocol switch
                {
                    "Anthropic" => await SendAnthropicAsync(baseUrl, apiKey, model, effectivePrompt, maxTokens, ct),
                    "OpenAI" => await SendOpenAIAsync(baseUrl, apiKey, model, effectivePrompt, maxTokens, ct),
                    _ => new TestResult(false, $"未知协议：{protocol}", (int)sw.ElapsedMilliseconds),
                };
                return result;
            }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new TestResult(false, $"请求超时（{RequestTimeout.TotalSeconds:0}s）", (int)sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return new TestResult(false, $"网络错误：{Truncate(ex.Message, 200)}", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResult(false, $"异常：{Truncate(ex.Message, 200)}", (int)sw.ElapsedMilliseconds);
        }
    }

    private static async Task<TestResult> SendAnthropicAsync(string baseUrl, string apiKey, string model, string prompt, int maxTokens, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/v1/messages";
        var sw = Stopwatch.StartNew();

        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);
        req.Content = JsonContent.Create(new
        {
            model,
            max_tokens = maxTokens,
            messages = new[] { new { role = "user", content = prompt } }
        });

        var resp = await s_http.SendAsync(req, ct);
        sw.Stop();
        var latency = (int)sw.ElapsedMilliseconds;

        if (IsSuccess(resp.StatusCode))
        {
            return new TestResult(true, $"OK · {latency}ms", latency);
        }
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new TestResult(false, $"HTTP {(int)resp.StatusCode}：{Truncate(body, 150)}", latency);
    }

    private static async Task<TestResult> SendOpenAIAsync(string baseUrl, string apiKey, string model, string prompt, int maxTokens, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/chat/completions";
        var sw = Stopwatch.StartNew();

        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = JsonContent.Create(new
        {
            model,
            max_tokens = maxTokens,
            messages = new[] { new { role = "user", content = prompt } }
        });

        var resp = await s_http.SendAsync(req, ct);
        sw.Stop();
        var latency = (int)sw.ElapsedMilliseconds;

        if (IsSuccess(resp.StatusCode))
        {
            return new TestResult(true, $"OK · {latency}ms", latency);
        }
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new TestResult(false, $"HTTP {(int)resp.StatusCode}：{Truncate(body, 150)}", latency);
    }

    private static bool IsSuccess(System.Net.HttpStatusCode code)
        => (int)code >= 200 && (int)code < 300;

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}