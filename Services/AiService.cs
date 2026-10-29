using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsProcessViewer.Models;

namespace WindowsProcessViewer.Services;

public sealed record AiVerdict(
    int Pid,
    string Software,
    string Purpose,
    string LaunchedBy,
    string CanClose,
    string CloseMethod,
    string Risk,
    string Note);

public sealed record AiAnalysisResult(
    IReadOnlyList<AiVerdict> Verdicts,
    string RawText,
    string Model,
    int PromptTokens,
    int CompletionTokens);

/// <summary>
/// MiniMax（OpenAI 兼容接口）客户端：把进程信息打包成一次对话请求，要求模型返回 JSON 数组。
/// 未配置 ApiKey 时抛 <see cref="AiException"/>，由界面提示用户去填。
/// </summary>
public sealed class AiService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public sealed class AiException(string message) : Exception(message);

    private const string SystemPrompt = """
你是资深的 Windows 系统运维与安全分析专家。用户会给你一批从本机采集到的进程信息，
请为每个进程回答：
1) 它属于哪个软件/厂商（software）；
2) 它的作用是什么（purpose）；
3) 谁启动的（launchedBy）：用户手动、某个父进程、Windows 服务、计划任务、开机自启或系统；
4) 能不能关闭（canClose），只能取以下四个值之一：可以关闭 / 谨慎关闭 / 建议保留 / 禁止关闭；
5) 如何关闭（closeMethod）：具体操作，例如“任务管理器结束任务”“在该软件界面退出”“services.msc 停止某某服务”；
6) 风险等级（risk），只能取：低 / 中 / 高 / 极高；
7) 一句话提示（note），可为空。

严格要求：
- 只输出 JSON 数组，不要 Markdown 代码块、不要任何解释文字。
- 数组每一项的字段名固定为：pid, software, purpose, launchedBy, canClose, closeMethod, risk, note。
- pid 必须与输入一致（整数）。
- 全部使用简体中文；purpose 与 closeMethod 各不超过 60 字，note 不超过 40 字。
- 不确定的信息写“不确定”，绝不编造具体公司、功能或文件路径。
- 判断以“普通办公电脑”为前提：系统核心进程（如 csrss、lsass、winlogon、services）必须为“禁止关闭”。
""";

    public bool IsConfigured(AppSettings settings) => !string.IsNullOrWhiteSpace(ConfigService.ResolveApiKey(settings));

    /// <summary>分析一批进程（建议 ≤ MaxProcessesPerRequest 个）。</summary>
    public async Task<AiAnalysisResult> AnalyzeAsync(
        IReadOnlyList<ProcessRecord> records,
        AppSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
            throw new AiException("没有可分析的进程。");

        var payload = BuildPayload(records, settings);
        var (content, model, promptTokens, completionTokens) =
            await SendAsync(payload, settings, apiKey, cancellationToken).ConfigureAwait(false);

        var verdicts = ParseVerdicts(content);
        return new AiAnalysisResult(verdicts, content, model, promptTokens, completionTokens);
    }

    /// <summary>连通性测试：发一句 ping，验证 Key、区域域名与模型名是否匹配。</summary>
    public async Task<string> PingAsync(AppSettings settings, string apiKey, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.MiniMax.Model,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = "请只回复两个字：正常" },
            },
            ["max_completion_tokens"] = 64,
            ["stream"] = false,
        };

        var (content, model, _, _) = await SendAsync(payload, settings, apiKey, cancellationToken).ConfigureAwait(false);
        return $"{model}：{content.Trim()}";
    }

    private static object BuildPayload(IReadOnlyList<ProcessRecord> records, AppSettings settings)
    {
        var builder = new StringBuilder();
        builder.AppendLine("请分析下列进程，并按约定返回 JSON 数组：");
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            builder.AppendLine($"[{i + 1}]");
            builder.AppendLine($"name: {r.Name}");
            builder.AppendLine($"pid: {r.Pid}");
            builder.AppendLine($"path: {r.Path}");
            builder.AppendLine($"cmdline: {Truncate(r.CommandLine, 300)}");
            builder.AppendLine($"company: {r.Company}");
            builder.AppendLine($"product: {r.ProductName}");
            builder.AppendLine($"description: {r.FileDescription}");
            builder.AppendLine($"signature: {r.SignatureStatus}");
            builder.AppendLine($"owner: {r.Owner}");
            builder.AppendLine($"elevated: {(r.IsElevated ? "是" : "否")}");
            builder.AppendLine($"parent: {r.ParentName} (PID {r.ParentPid})");
            builder.AppendLine($"service: {(r.IsService ? $"{r.ServiceDisplayName} ({r.ServiceName})" : "无")}");
            builder.AppendLine($"autostart: {(string.IsNullOrWhiteSpace(r.Autostart) ? "未检测到" : r.Autostart)}");
            builder.AppendLine($"ruleGuess: {r.Category} / {r.CanClose} / {r.Risk}");
            builder.AppendLine();
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.MiniMax.Model,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = SystemPrompt },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = builder.ToString() },
            },
            ["max_completion_tokens"] = settings.MiniMax.MaxCompletionTokens,
            ["stream"] = false,
        };

        if (!string.IsNullOrWhiteSpace(settings.MiniMax.ReasoningEffort))
            payload["reasoning_effort"] = settings.MiniMax.ReasoningEffort;

        // 注意：MiniMax-M3.1-Flash 不支持关闭思考，传 disabled 会返回 400。
        if (settings.MiniMax.DisableThinking)
            payload["thinking"] = new Dictionary<string, object?> { ["type"] = "disabled" };

        return payload;
    }

    private static async Task<(string Content, string Model, int PromptTokens, int CompletionTokens)> SendAsync(
        object payload,
        AppSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.MiniMax.TimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.MiniMax.Endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        string body;
        try
        {
            response = await Http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiException($"请求超时（{settings.MiniMax.TimeoutSeconds} 秒）。可在 appsettings.json 调大 TimeoutSeconds，或减少单次分析的进程数量。");
        }
        catch (HttpRequestException ex)
        {
            throw new AiException($"无法连接 MiniMax 接口：{ex.Message}\n请检查网络/代理，以及 Endpoint 是否与账号区域一致（国内 api.minimaxi.com，国际 api.minimax.io）。");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new AiException(DescribeHttpError(response.StatusCode, body));

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("base_resp", out var baseResp) &&
                baseResp.TryGetProperty("status_code", out var statusCode) &&
                statusCode.ValueKind == JsonValueKind.Number &&
                statusCode.GetInt32() != 0)
            {
                var message = baseResp.TryGetProperty("status_msg", out var msg) ? msg.GetString() : "";
                var code = statusCode.GetInt32();
                throw new AiException($"MiniMax 返回错误码 {code}：{message}（{ExplainCode(code)}）");
            }

            var model = root.TryGetProperty("model", out var modelElement) ? (modelElement.GetString() ?? settings.MiniMax.Model) : settings.MiniMax.Model;

            var content = "";
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                if (message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
                    content = contentElement.GetString() ?? "";
            }

            if (string.IsNullOrWhiteSpace(content))
                throw new AiException("MiniMax 返回了空内容，可能是触发内容安全策略或模型未按格式输出。请重试或更换模型。");

            var promptTokens = 0;
            var completionTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                    promptTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                    completionTokens = ct.GetInt32();
            }

            return (content, model, promptTokens, completionTokens);
        }
    }

    private static IReadOnlyList<AiVerdict> ParseVerdicts(string content)
    {
        var json = ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<AiVerdict>();

        try
        {
            var items = JsonSerializer.Deserialize<List<AiVerdictDto>>(json, JsonOptions);
            if (items is null || items.Count == 0)
                return Array.Empty<AiVerdict>();

            return items
                .Where(i => i.Pid > 0)
                .Select(i => new AiVerdict(
                    i.Pid,
                    Clean(i.Software),
                    Clean(i.Purpose),
                    Clean(i.LaunchedBy),
                    NormalizeCanClose(i.CanClose),
                    Clean(i.CloseMethod),
                    NormalizeRisk(i.Risk),
                    Clean(i.Note)))
                .ToList();
        }
        catch (JsonException ex)
        {
            Log.Warn($"AI 返回内容不是合法 JSON：{ex.Message}");
            return Array.Empty<AiVerdict>();
        }
    }

    /// <summary>模型有时会包一层 ```json，或前后带解释文字，这里截取第一个 [ 到最后一个 ]。</summary>
    private static string ExtractJson(string content)
    {
        var start = content.IndexOf('[');
        var end = content.LastIndexOf(']');
        if (start >= 0 && end > start)
            return content[start..(end + 1)];

        return "";
    }

    private static string Clean(string? text)
        => string.IsNullOrWhiteSpace(text) ? "" : text.Trim().TrimEnd('。', '；', ',', '.');

    private static string NormalizeCanClose(string? value)
    {
        var text = Clean(value);
        return text switch
        {
            "可以关闭" or "可关闭" or "安全" => "可以关闭",
            "谨慎关闭" or "视情况" => "谨慎关闭",
            "建议保留" or "不建议关闭" or "保留" => "建议保留",
            "禁止关闭" or "不可关闭" or "无法关闭" => "禁止关闭",
            _ => string.IsNullOrEmpty(text) ? "不确定" : text,
        };
    }

    private static string NormalizeRisk(string? value)
    {
        var text = Clean(value);
        return text switch
        {
            "低" or "低风险" => "低",
            "中" or "中风险" => "中",
            "高" or "高风险" => "高",
            "极高" or "严重" => "极高",
            _ => string.IsNullOrEmpty(text) ? "中" : text,
        };
    }

    private static string DescribeHttpError(HttpStatusCode status, string body)
    {
        var reason = ExtractErrorReason(body);
        var hint = status switch
        {
            HttpStatusCode.Unauthorized => "API Key 无效、已失效，或与 Endpoint 区域不匹配（国内 api.minimaxi.com / 国际 api.minimax.io）。",
            HttpStatusCode.Forbidden => "该 Key 无权访问此模型或资源，或账号余额/额度不足。",
            HttpStatusCode.NotFound => "接口地址不存在：请检查 Endpoint 是否带 /v1/chat/completions 且没有重复的 /v1。",
            HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后重试或减少批量数量。",
            HttpStatusCode.PaymentRequired => "账号余额不足。",
            _ => "请检查配置或稍后重试。",
        };

        var detail = string.IsNullOrWhiteSpace(reason) ? "" : $"{Environment.NewLine}服务端说明：{reason}";
        return $"MiniMax 接口返回 HTTP {(int)status}：{hint}{detail}";
    }

    /// <summary>从错误响应里提取一句可读原因，避免把整段 JSON 弹给用户。</summary>
    private static string ExtractErrorReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    return message.GetString() ?? "";
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? "";
            }

            if (root.TryGetProperty("base_resp", out var baseResp) &&
                baseResp.TryGetProperty("status_msg", out var statusMessage) &&
                statusMessage.ValueKind == JsonValueKind.String)
            {
                return statusMessage.GetString() ?? "";
            }

            if (root.TryGetProperty("message", out var topMessage) && topMessage.ValueKind == JsonValueKind.String)
                return topMessage.GetString() ?? "";
        }
        catch (JsonException)
        {
            // 非 JSON 响应（如网关 HTML 错误页）时退回截断文本
        }

        var trimmed = body.Trim().ReplaceLineEndings(" ");
        return trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;
    }

    private static string ExplainCode(int code) => code switch
    {
        1000 => "未知错误",
        1001 => "请求超时",
        1002 => "触发限流，请稍后重试",
        1004 => "认证失败：Key 无效或区域不匹配",
        1008 => "账号余额不足",
        2049 => "API Key 无效",
        2056 => "用量超限",
        _ => "详见 MiniMax 错误码文档",
    };

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";

    private sealed class AiVerdictDto
    {
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonPropertyName("software")] public string? Software { get; set; }
        [JsonPropertyName("purpose")] public string? Purpose { get; set; }
        [JsonPropertyName("launchedBy")] public string? LaunchedBy { get; set; }
        [JsonPropertyName("canClose")] public string? CanClose { get; set; }
        [JsonPropertyName("closeMethod")] public string? CloseMethod { get; set; }
        [JsonPropertyName("risk")] public string? Risk { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }
}
