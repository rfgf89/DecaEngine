<#
.SYNOPSIS
	Full tree check: solution build, unit tests, probe regression.

.DESCRIPTION
	One command for the whole refactor. Moving code between projects and splitting files must not
	change behaviour - so after every step this must be green, and if it is not green, the step was
	not mechanical.

	Three levels, cheapest to most expensive, stopping at the first failure: running the probes on a
	tree that does not build makes no sense.

.PARAMETER SkipProbes
	Build and unit tests only (seconds instead of minutes). For checking as you go; run the full
	thing before committing.

.PARAMETER SkipEditor
	Do not launch the editor. Launching opens a window for a few seconds - annoying if the check runs
	in the background.

.PARAMETER Backend
	Backend for the probes: d3d12 (default) or vulkan.

.EXAMPLE
	.\tools\Verify.ps1
	.\tools\Verify.ps1 -SkipProbes
#>
[CmdletBinding()]
param(
	[switch]$SkipProbes,
	[switch]$SkipEditor,
	[ValidateSet('d3d12', 'vulkan')][string]$Backend = 'd3d12'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

function Write-Step {
	param([string]$Text)
	Write-Host ""
	Write-Host "=== $Text" -ForegroundColor Cyan
}

# Diligent does not work on AnyCPU - the platform is set explicitly everywhere.
Write-Step "Solution build (x64)"
& dotnet build (Join-Path $RepoRoot 'DecaEngine.sln') -c Debug -p:Platform=x64 --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) {
	Write-Host "BUILD FAILED" -ForegroundColor Red
	exit 1
}
Write-Host "  built" -ForegroundColor Green

Write-Step "Unit tests"
$testOutput = & dotnet test (Join-Path $RepoRoot 'DecaEngine.Tests\DecaEngine.Tests.csproj') `
	-c Debug -p:Platform=x64 --nologo -v q --no-build 2>&1
$testExit = $LASTEXITCODE
$summary = $testOutput | Select-String -Pattern 'Passed!|Failed!|error'
if ($testExit -ne 0) {
	Write-Host "TESTS FAILED" -ForegroundColor Red
	$testOutput | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" }
	exit 1
}
$summary | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }

if ($SkipProbes) {
	Write-Host ""
	Write-Host "Probes skipped (-SkipProbes). Run the full check before committing." -ForegroundColor Yellow
	exit 0
}

Write-Step "Probe regression"
& (Join-Path $PSScriptRoot 'Run-ProbeSuite.ps1') -SkipBuild -Backend $Backend
$probeExit = $LASTEXITCODE

if ($probeExit -eq 0 -and -not $SkipEditor) {
	<#
		The probes bring up graphics but do NOT bring up ImGui: they have no window. Because of
		that the whole suite stayed green while the editor would not start at all - the ImGui
		shaders used to reach the output from the SAMPLES project, whose reference was removed as
		unneeded, and the very first launch died with a NullReferenceException while creating a PSO
		with no vertex shader.

		238 metrics are silent about that by construction. So the editor is simply LAUNCHED here:
		it checks exactly what the probes do not cover - that the process reaches frames and does
		not crash.
	#>
	Write-Step "Editor launch"

	$editorExe = Join-Path $RepoRoot 'DecaEngine.Editor.App\bin\x64\Debug\net10.0\DecaEngine.Editor.App.exe'
	if (-not (Test-Path $editorExe)) {
		Write-Host "  no $editorExe" -ForegroundColor Red
		$probeExit = 1
	}
	else {
		$outFile = Join-Path $env:TEMP 'deca_verify_editor_out.txt'
		$errFile = Join-Path $env:TEMP 'deca_verify_editor_err.txt'
		$proc = Start-Process -FilePath $editorExe -WorkingDirectory (Split-Path $editorExe) `
			-PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile

		# Graphics init, ImGui and the first frames fit into a few seconds; a startup crash happens
		# much earlier.
		Start-Sleep -Seconds 12

		if ($proc.HasExited) {
			Write-Host "  EDITOR CRASHED AT STARTUP (code $($proc.ExitCode))" -ForegroundColor Red
			Get-Content $errFile -Tail 25 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "      $_" }
			Get-Content $outFile -Tail 25 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "      $_" }
			$probeExit = 1
		}
		else {
			Stop-Process -Id $proc.Id -Force
			Write-Host "  started and still alive" -ForegroundColor Green
		}
	}
}

$stopwatch.Stop()
Write-Host ""
if ($probeExit -ne 0) {
	Write-Host "VERIFY FAILED in $([int]$stopwatch.Elapsed.TotalSeconds) s" -ForegroundColor Red
	exit 1
}

Write-Host "All green in $([int]$stopwatch.Elapsed.TotalSeconds) s" -ForegroundColor Green
exit 0
