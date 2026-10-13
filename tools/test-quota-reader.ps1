$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('agent-meter-quota-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
 Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../CodexQuotaReader.cs') -Destination $root
 '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>' | Set-Content (Join-Path $root 'QuotaCheck.csproj')
 @'
using System.Diagnostics;
using System.Text.Json;
using AgentMeter;
var dir = AppContext.BaseDirectory;
var mode = Path.Combine(dir, "mode");
var log = Path.Combine(dir, "log");
if(args.Contains("app-server")) {
 File.AppendAllText(log, $"start {Environment.ProcessId}\n");
 bool initialized = false;
 while(Console.ReadLine() is {} line) {
  using var d = JsonDocument.Parse(line);
  var m = d.RootElement;
  var method = m.GetProperty("method").GetString();
  if(method == "initialized") { initialized=true; continue; }
  var id = m.GetProperty("id").GetInt32();
  if(method == "initialize") { Console.WriteLine(JsonSerializer.Serialize(new {id,result=new {}})); continue; }
  if(!initialized) throw new Exception("Missing initialize");
  File.AppendAllText(log,"read\n");
  var action = File.Exists(mode) ? File.ReadAllText(mode) : "";
  if(action=="crash") { File.Delete(mode); return; }
  if(action=="hang") { File.Delete(mode); await Task.Delay(Timeout.Infinite); }
  if(action=="limit") { Console.WriteLine(JsonSerializer.Serialize(new {id,error=new {code=429,message="rate limit"}})); continue; }
  Console.WriteLine("""{"method":"account/rateLimits/updated","params":{}}""");
  Console.WriteLine(JsonSerializer.Serialize(new {id,result=new {rateLimits=new {primary=new {usedPercent=25,windowDurationMins=300,resetsAt=1800000000}}}}));
 }
 return;
}
Environment.SetEnvironmentVariable("AGENT_METER_CODEX_PATH",Environment.ProcessPath);
int Count(string prefix) => File.Exists(log) ? File.ReadAllLines(log).Count(x=>x.StartsWith(prefix)) : 0;
void Check(bool ok) { if(!ok) throw new Exception("Lifecycle assertion failed"); }
using var reader = new CodexQuotaReader();
CodexQuotaReader.SelfTest();
var results=await Task.WhenAll(reader.ReadAsync(),reader.ReadAsync(),reader.ReadAsync());
Check(Count("start")==1 && results.All(x=>x.Windows[0].RemainingPercent==75));
File.WriteAllText(mode,"crash");
await reader.ReadAsync(); Check(Count("start")==2);
File.WriteAllText(mode,"hang");
await reader.ReadAsync(); Check(Count("start")==3);
File.WriteAllText(mode,"limit");
var before=Count("read");
try { await reader.ReadAsync(); throw new Exception("Expected 429"); }
catch(InvalidOperationException e) when(e.Message.Contains("429")) {}
Check(Count("read")==before+1);
File.Delete(mode); await reader.ReadAsync(); Check(Count("start")==4);
File.WriteAllText(mode,"hang");
before=Count("read");
var pending=reader.ReadAsync();
using(var timeout=new CancellationTokenSource(10000)) {
 while(Count("read")<=before || File.Exists(mode)) await Task.Delay(25,timeout.Token);
}
reader.Dispose();
try { await pending; throw new Exception("Expected cancellation"); } catch(OperationCanceledException) {}
reader.Dispose();
try { await reader.ReadAsync(); throw new Exception("Expected cancellation"); } catch(OperationCanceledException) {}
foreach(var pid in File.ReadAllLines(log).Where(x=>x.StartsWith("start ")).Select(x=>int.Parse(x[6..]))) {
 try { using var child=Process.GetProcessById(pid); Check(child.WaitForExit(5000)); } catch(ArgumentException) {}
}
Console.WriteLine("PASS: reuse, concurrent reads, notifications, crash retry, timeout, rate limit, disposal and child cleanup.");
'@ | Set-Content (Join-Path $root 'Program.cs')
 dotnet run --project (Join-Path $root 'QuotaCheck.csproj')
 if ($LASTEXITCODE -ne 0) { throw 'Quota check failed.' }
} finally {
 $resolved = [IO.Path]::GetFullPath($root)
 $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
 if (!$resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected cleanup path.' }
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
