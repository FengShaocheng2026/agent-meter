$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content (Join-Path $projectRoot 'AgentMeter.csproj') -Raw
$version = $project.Project.PropertyGroup.Version
$name = "AgentMeter-$version-win-x64"
$output = Join-Path $projectRoot 'artifacts'
$publish = Join-Path $output $name
if (Test-Path -LiteralPath $publish) { throw "Move existing package directory first: $publish" }
dotnet publish (Join-Path $projectRoot 'AgentMeter.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$exe = Join-Path $publish 'AgentMeter.exe'
$test = Start-Process -FilePath $exe -ArgumentList '--self-test' -Wait -PassThru
if ($test.ExitCode -ne 0) { throw "Release self-test failed: $($test.ExitCode)" }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publish
$zip = Join-Path $output "$name.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content -LiteralPath "$zip.sha256" -Encoding ASCII
Write-Output "Release: $zip"
Write-Output "SHA256: $hash"