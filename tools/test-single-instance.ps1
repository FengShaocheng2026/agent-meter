param([Parameter(Mandatory=$true)][string]$Executable, [switch]$CheckWindowRestore)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class InstanceTestWindow {
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr handle);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
"@
$children = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts'))
$copyDirectory = Join-Path $root ('single-instance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $copyDirectory | Out-Null
$copyExe = Join-Path $copyDirectory ([System.IO.Path]::GetFileName($Executable))
Copy-Item -LiteralPath $Executable -Destination $copyExe
try {
 $first = Start-Process -FilePath $Executable -PassThru -WindowStyle Normal
 $children.Add($first)
 Start-Sleep -Seconds 3
 $first.Refresh()
 if ($first.HasExited) { throw 'First instance exited; close existing instances before testing.' }
 $handle = $first.MainWindowHandle
 if ($CheckWindowRestore) {
  if ($handle -eq [IntPtr]::Zero) { throw 'Main window was not created.' }
  [void][InstanceTestWindow]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
  Start-Sleep -Seconds 1
  if ([InstanceTestWindow]::IsWindowVisible($handle)) { throw 'Close did not hide the window.' }
 }
 foreach ($path in @($Executable, $copyExe, $Executable)) {
  $child = Start-Process -FilePath $path -PassThru -WindowStyle Hidden
  $children.Add($child)
 }
 foreach ($child in $children | Select-Object -Skip 1) {
  if (!$child.WaitForExit(10000)) { throw 'Duplicate instance remained running.' }
  if ($child.ExitCode -ne 0) { throw "Duplicate launch failed: $($child.ExitCode)" }
 }
 $first.Refresh()
 if ($first.HasExited) { throw 'Original instance exited unexpectedly.' }
 if ($CheckWindowRestore) {
  Start-Sleep -Seconds 1
  if (![InstanceTestWindow]::IsWindowVisible($handle)) { throw 'Duplicate launch did not restore the original window.' }
 }
 # Verify the singleton is released even after an abnormal termination.
 Stop-Process -Id $first.Id
 [void]$first.WaitForExit(5000)
 $restart = Start-Process -FilePath $copyExe -PassThru -WindowStyle Hidden
 $children.Add($restart)
 Start-Sleep -Seconds 2
 if ($restart.HasExited) { throw 'Restart after termination failed.' }
 Write-Output 'PASS: duplicate launch, concurrent duplicate launches, different path, restart after termination.'
 if ($CheckWindowRestore) { Write-Output 'PASS: close to tray and restore original window.' }
} finally {
 foreach ($child in $children) { if (!$child.HasExited) { Stop-Process -Id $child.Id; [void]$child.WaitForExit(5000) }; $child.Dispose() }
 if ([System.IO.Path]::GetDirectoryName($copyDirectory) -ne $root) { throw 'Unexpected cleanup path.' }
 Remove-Item -LiteralPath $copyDirectory -Recurse -Force
}