using System.Diagnostics;
using System.Text.Json;

namespace AgentMeter;

internal sealed class CodexQuotaReader
{
    public async Task<QuotaSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var executable = FindCodexExecutable();
        var startInfo = new ProcessStartInfo(executable, "app-server --stdio")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 Codex app-server。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new { name = "agent-meter", title = "AgentMeter", version = "0.1.0" },
                    capabilities = new { experimentalApi = true }
                }
            });

            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var requestId))
                {
                    continue;
                }

                if (requestId == 1)
                {
                    await SendAsync(process, new { jsonrpc = "2.0", method = "initialized", @params = new { } });
                    await SendAsync(process, new
                    {
                        jsonrpc = "2.0",
                        id = 2,
                        method = "account/rateLimits/read",
                        @params = new { }
                    });
                }
                else if (requestId == 2)
                {
                    if (root.TryGetProperty("error", out _))
                    {
                        throw new InvalidOperationException("Codex app-server 返回错误。");
                    }

                    return ParseRateLimits(root.GetProperty("result").GetRawText(), DateTimeOffset.Now);
                }
            }

            await stderrTask;
            throw new InvalidOperationException("Codex app-server 未返回额度数据。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("读取 Codex 额度超时。");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            try
            {
                await stderrTask;
            }
            catch
            {
                // The user-facing state intentionally excludes app-server stderr and local paths.
            }
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

    private static Task SendAsync(Process process, object message) =>
        process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));

    private static string FindCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("AGENT_METER_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "node_modules",
            "@openai",
            "codex",
            "node_modules");
        if (Directory.Exists(packageRoot))
        {
            var executable = Directory.EnumerateFiles(packageRoot, "codex.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "codex.exe", StringComparison.OrdinalIgnoreCase));
            if (executable is not null)
            {
                return executable;
            }
        }

        throw new FileNotFoundException(
            "找不到 Codex CLI。请安装 @openai/codex，或设置 AGENT_METER_CODEX_PATH 指向 codex.exe。");
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
