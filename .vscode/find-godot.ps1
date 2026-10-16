# Prints the full path of the Godot (.NET) editor executable on this machine.
# Used by launch.json so no machine-specific path has to live in the repo or in VS Code settings.
#
# Search order:
#   1. GODOT4 / GODOT environment variable (explicit override)
#   2. godot on PATH
#   3. The project's parent folders, then the usual download/install locations
# When several builds are found, the one matching the Godot.NET.Sdk version in the .csproj wins.

$ErrorActionPreference = 'SilentlyContinue'
$projectDir = Split-Path -Parent $PSScriptRoot

function Test-Exe($path) {
	return $path -and (Test-Path -LiteralPath $path -PathType Leaf)
}

# 1. Explicit override
foreach ($name in 'GODOT4', 'GODOT') {
	$value = [Environment]::GetEnvironmentVariable($name)
	if (Test-Exe $value) {
		Write-Output (Resolve-Path -LiteralPath $value).Path
		exit 0
	}
}

# 2. PATH
foreach ($name in 'godot4', 'godot', 'godot-mono') {
	$cmd = Get-Command $name -CommandType Application | Select-Object -First 1
	if ($cmd -and (Test-Exe $cmd.Source)) {
		Write-Output $cmd.Source
		exit 0
	}
}

# 3. Search folders
$dirs = New-Object System.Collections.Generic.List[string]

$dir = Split-Path -Parent $projectDir
while ($dir) {
	$dirs.Add($dir)
	$dirs.Add((Join-Path $dir 'Godot'))
	$dir = Split-Path -Parent $dir
}

$userProfile = $env:USERPROFILE
foreach ($base in @(
		[Environment]::GetFolderPath('Desktop'),
		[Environment]::GetFolderPath('MyDocuments'),
		(Join-Path $userProfile 'Desktop'),
		(Join-Path $userProfile 'Documents'),
		(Join-Path $userProfile 'Downloads'),
		$userProfile,
		$env:ProgramFiles,
		${env:ProgramFiles(x86)},
		$env:LOCALAPPDATA,
		(Join-Path $env:LOCALAPPDATA 'Programs')
	)) {
	if ($base) {
		$dirs.Add($base)
		$dirs.Add((Join-Path $base 'Godot'))
	}
}
$dirs.Add((Join-Path $userProfile 'scoop\apps\godot-mono\current'))
$dirs.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'))

$found = foreach ($d in ($dirs | Select-Object -Unique)) {
	if (Test-Path -LiteralPath $d -PathType Container) {
		# Depth 2 covers the zip's own "Godot_v*_mono_win64" folder inside a search folder.
		Get-ChildItem -LiteralPath $d -Filter 'Godot*.exe' -File -Recurse -Depth 2 |
			Where-Object { $_.Name -notlike '*_console.exe' }
	}
}
$found = @($found | Sort-Object FullName -Unique)

if ($found.Count -eq 0) {
	[Console]::Error.WriteLine('Godot executable not found. Put it on PATH or set the GODOT4 environment variable to its full path.')
	exit 1
}

# This is a C# project, so only a .NET ("mono") build can run it.
$mono = @($found | Where-Object { $_.Name -like '*mono*' })
if ($mono.Count -gt 0) { $found = $mono }

$sdkVersion = $null
$csproj = Get-ChildItem -LiteralPath $projectDir -Filter '*.csproj' -File | Select-Object -First 1
if ($csproj -and ((Get-Content -LiteralPath $csproj.FullName -Raw) -match 'Godot\.NET\.Sdk/([0-9.]+)')) {
	$sdkVersion = $Matches[1]
}

$best = $null
if ($sdkVersion) {
	$best = $found | Where-Object { $_.Name -like "*v$sdkVersion-*" } | Select-Object -First 1
}
if (-not $best) {
	$best = $found | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

Write-Output $best.FullName
exit 0
