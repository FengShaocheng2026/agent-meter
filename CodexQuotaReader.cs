using System.Diagnostics;
using System.Text.Json;

namespace AgentMeter;

internal sealed class CodexQuotaReader : IDisposable
{
    private readonly SemaphoreSlim readLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object processLock = new();
    private Process? server;
    private int nextRequestId;
    private bool disposed;

    public async Task<QuotaSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = linked.Token;
        await readLock.WaitAsync(token);
        try
        {
            Exception? lastError = null;
            var attemptsMade = 0;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                token.ThrowIfCancellationRequested();
                attemptsMade = attempt;
                try
                {
                    return await ReadOnceAsync(token);
                }
                catch (Exception exception)
                {
                    StopServer();
                    token.ThrowIfCancellationRequested();
                    lastError = exception;
                    if (attempt < 2 && !IsRateLimit(exception.Message))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), token);
                        continue;
                    }
                    break;
                }
            }

            var retryNote = attemptsMade > 1 ? "（已重试 1 次）" : string.Empty;
            throw new InvalidOperationException($"读取 Codex 额度失败{retryNote}：{lastError!.Message}", lastError);
        }
        finally
        {
            readLock.Release();
        }
    }

    private async Task<QuotaSnapshot> ReadOnceAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            Process process;
            bool initialize;
            lock (processLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                initialize = server is null || server.HasExited;
                if (initialize)
                {
                    StopServer();
                    server = new Process
                    {
                        StartInfo = new ProcessStartInfo(FindCodexExecutable(), "app-server --stdio")
                        {
                            UseShellExecute = false,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };
                    if (!server.Start())
                    {
                        throw new InvalidOperationException("无法启动 Codex app-server。");
                    }
                    // Drain diagnostics without retaining account details or unbounded output.
                    server.ErrorDataReceived += (_, _) => { };
                    server.BeginErrorReadLine();
                }
                process = server!;
            }

            if (initialize)
            {
                await RequestAsync(process, "initialize", new
                {
                    clientInfo = new { name = "agent-meter", title = "AgentMeter", version = "0.1.0" },
                    capabilities = new { experimentalApi = true }
                }, timeout.Token);
                await SendAsync(process, new { jsonrpc = "2.0", method = "initialized", @params = new { } }, timeout.Token);
            }

            var result = await RequestAsync(process, "account/rateLimits/read", new { }, timeout.Token);
            return ParseRateLimits(result, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("读取 Codex 额度超时。");
        }
    }

    private async Task<string> RequestAsync(Process process, string method, object parameters, CancellationToken token)
    {
        var requestId = ++nextRequestId;
        await SendAsync(process, new { jsonrpc = "2.0", id = requestId, method, @params = parameters }, token);
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var responseId) || responseId != requestId)
            {
                continue;
            }
            ThrowIfServerError(root);
            return root.GetProperty("result").GetRawText();
        }
        throw new IOException("Codex app-server 连接已关闭。");
    }

    private void StopServer()
    {
        lock (processLock)
        {
            if (server is null) return;
            try
            {
                if (!server.HasExited) server.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process may have exited already or failed to start.
            }
            finally
            {
                server.Dispose();
                server = null;
            }
        }
    }

    public void Dispose()
    {
        lock (processLock)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            StopServer();
        }
    }

    internal static QuotaSnapshot ParseRateLimits(string json, DateTimeOffset updatedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rateLimits = root.GetProperty("rateLimits");
        var windows = new List<QuotaWindow>();
        AddWindow(rateLimits, "primary", windows);
        AddWindow(rateLimits, "secondary", windows);

        if (windows.Count == 0)
        {
            throw new InvalidDataException("Codex 未返回可用的额度窗口。");
        }

        return new QuotaSnapshot(windows.OrderBy(window => window.DurationMinutes).ToArray(), updatedAt);
    }

    internal static void SelfTest()
    {
        const string json = """
        {
          "rateLimits": {
            "primary": { "usedPercent": 1, "windowDurationMins": 10080, "resetsAt": 1786825805 },
            "secondary": { "usedPercent": 92, "windowDurationMins": 300, "resetsAt": 1786221005 },
            "planType": "pro"
          }
        }
        """;

        var snapshot = ParseRateLimits(json, DateTimeOffset.UnixEpoch);
        if (snapshot.Windows.Count != 2
            || snapshot.Windows[0].Label != "5 小时"
            || snapshot.Windows[0].RemainingPercent != 8
            || snapshot.Windows[1].Label != "1 周"
            || snapshot.Windows[1].RemainingPercent != 99)
        {
            throw new InvalidOperationException("Codex quota parser self-test failed.");
        }

        using var errorDocument = JsonDocument.Parse("""{"error":{"code":429,"message":"rate limit exceeded"}}""");
        try
        {
            ThrowIfServerError(errorDocument.RootElement);
            throw new InvalidOperationException("Codex error parser self-test failed.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "Codex app-server 错误 429：rate limit exceeded")
        {
        }
    }

    private static void AddWindow(JsonElement rateLimits, string propertyName, ICollection<QuotaWindow> windows)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var used = Math.Clamp((int)Math.Round(value.GetProperty("usedPercent").GetDouble()), 0, 100);
        var duration = value.GetProperty("windowDurationMins").GetInt32();
        var reset = DateTimeOffset.FromUnixTimeSeconds(value.GetProperty("resetsAt").GetInt64());
        var (label, shortLabel) = FormatDuration(duration);
        windows.Add(new QuotaWindow(label, shortLabel, used, 100 - used, reset, duration));
    }

    private static (string Label, string ShortLabel) FormatDuration(int minutes) => minutes switch
    {
        300 => ("5 小时", "5时"),
        10080 => ("1 周", "周"),
        < 1440 => ($"{Math.Max(1, minutes / 60)} 小时", $"{Math.Max(1, minutes / 60)}时"),
        _ => ($"{Math.Max(1, minutes / 1440)} 天", $"{Math.Max(1, minutes / 1440)}天")
    };

    private static Task SendAsync(Process process, object message, CancellationToken token) =>
        process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);

    private static void ThrowIfServerError(JsonElement response)
    {
        if (!response.TryGetProperty("error", out var error))
        {
            return;
        }

        var code = error.TryGetProperty("code", out var codeValue) ? codeValue.ToString() : "未知";
        var message = error.TryGetProperty("message", out var messageValue)
            ? messageValue.GetString()?.Replace('\r', ' ').Replace('\n', ' ').Trim()
            : null;
        if (message?.Length > 200)
        {
            message = message[..200] + "…";
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(message)
                ? $"Codex app-server 错误 {code}。"
                : $"Codex app-server 错误 {code}：{message}");
    }

    private static bool IsRateLimit(string message) =>
        message.Contains("429", StringComparison.OrdinalIgnoreCase)
        || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
        || message.Contains("限流", StringComparison.OrdinalIgnoreCase);

    private static string FindCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("AGENT_METER_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var desktopRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");
        var desktopExecutable = FindNewestCodexExecutable(desktopRoot);
        if (desktopExecutable is not null)
        {
            return desktopExecutable;
        }

        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Trim('"'))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var pathDirectory in pathDirectories)
        {
            var executable = Path.Combine(pathDirectory, "codex.exe");
            if (File.Exists(executable))
            {
                return executable;
            }
        }

        var npmRoots = pathDirectories
            .Where(path => File.Exists(Path.Combine(path, "codex.cmd")))
            .Select(path => Path.Combine(path, "node_modules", "@openai", "codex", "node_modules"))
            .Append(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                "node_modules",
                "@openai",
                "codex",
                "node_modules"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var npmRoot in npmRoots)
        {
            var npmExecutable = FindNewestCodexExecutable(npmRoot);
            if (npmExecutable is not null)
            {
                return npmExecutable;
            }
        }

        throw new FileNotFoundException(
            "找不到 Codex CLI。请安装 Codex 桌面版或 @openai/codex，或设置 AGENT_METER_CODEX_PATH 指向 codex.exe。");
    }

    private static string? FindNewestCodexExecutable(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

internal sealed record QuotaSnapshot(
    IReadOnlyList<QuotaWindow> Windows,
    DateTimeOffset UpdatedAt);

internal sealed record QuotaWindow(
    string Label,
    string ShortLabel,
    int UsedPercent,
    int RemainingPercent,
    DateTimeOffset ResetsAt,
    int DurationMinutes = 0);
