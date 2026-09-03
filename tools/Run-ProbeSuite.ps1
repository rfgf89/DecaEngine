<#
.SYNOPSIS
	Runs the DECA_PROBE_* harnesses as a regression suite and checks their numbers against a baseline.

.DESCRIPTION
	There are no render tests in this project and there cannot be: asserting on an image costs more
	than drawing it. But the probes already print numbers that show EXACTLY what broke - frame
	luminance, entity counts, BVH/cache agreement. This script turns those numbers into a baseline.

	The point is refactoring: moving code between projects and splitting files must not change a
	SINGLE one of these numbers. If frame luminance shifted after ProbeGi moved into its own module,
	the move was not mechanical, and that shows up right away instead of a week later in a screenshot.

	Lines carrying timings (` ms`) never reach the baseline: they jitter from run to run and would
	drown the signal. Numbers are compared with a tolerance, text exactly.

.PARAMETER Baseline
	Record the current numbers as the baseline instead of comparing. Do this ONLY on a known-good
	tree - normally right after a commit that passed the comparison.

.PARAMETER Scenario
	Run a single scenario instead of all of them. Names are in $Scenarios below.

.PARAMETER Tolerance
	Relative tolerance for numbers, 1% by default. Luminance is computed on the GPU, and the last
	digit is allowed to wander.

.PARAMETER Backend
	d3d12 (default) or vulkan. Hardware ray tracing is only compiled on d3d12.

.PARAMETER SkipBuild
	Do not rebuild the editor. For a re-run after a failure.

.EXAMPLE
	.\tools\Run-ProbeSuite.ps1 -Baseline
	.\tools\Run-ProbeSuite.ps1
	.\tools\Run-ProbeSuite.ps1 -Scenario sponza-interior -SkipBuild
#>
[CmdletBinding()]
param(
	[switch]$Baseline,
	[string]$Scenario,
	[double]$Tolerance = 0.01,
	[ValidateSet('d3d12', 'vulkan')][string]$Backend = 'd3d12',
	[switch]$SkipBuild,
	[string]$BinDir = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
# PATH TRAP: a build with -p:Platform=x64 puts fresh DLLs into bin\x64\..., while bin\Debug\... holds
# a STALE set - the exe is updated there, but DecaEngine.Graphics.Diligent.dll is not. Running from
# there silently exercises yesterday's code. Use bin\x64 only.
# The entry point moved to DecaEngine.Editor.App: the editor itself is now a library, otherwise the
# probe project could not reference it (see DecaEngine.Editor.App.csproj). EditorAssets arrive here
# transitively from the editor - 345 of them, exactly as many as in the sources.
# -BinDir <dir> - run the probes from a DIFFERENT set (a copy of bin with fresh DLLs dropped in):
# while the editor is open, its exe and DLLs in bin\x64 are locked and cannot be rebuilt there - but
# a fix still needs checking without closing the scene. Use together with -SkipBuild.
if (-not $BinDir) {
	$BinDir = Join-Path $RepoRoot 'DecaEngine.Editor.App\bin\x64\Debug\net10.0'
}
$Exe = Join-Path $BinDir 'DecaEngine.Editor.App.exe'
# Not "probe-baseline": .gitignore drops probe-*/ at any level (there are ~150 directories with probe
# output), and the baselines would silently never make it into a commit.
$BaselineDir = Join-Path $PSScriptRoot 'baselines'
$OutRoot = Join-Path $RepoRoot '_probeout\suite'

# The probes look for EditorAssets next to the exe, so model paths are relative to bin.
$Sponza = 'EditorAssets/models/Sponza.gltf'
$Fox = 'EditorAssets/models/Fox.glb'

$Scenarios = @(
	@{
		Name = 'sponza-base'
		Desc = 'Model load, materials, shadows, SSAO/SSGI, probe GI - the main preview path'
		Args = @('--preview-probe', $Sponza, '<OUT>')
		Env  = @{}
	},
	@{
		Name = 'sponza-interior'
		Desc = 'Interior frame with a punctual light: probe GI traces the lamps'
		Args = @('--preview-probe', $Sponza, '<OUT>')
		Env  = @{ DECA_PROBE_EYE = '-0.5,3,0.4'; DECA_PROBE_TARGET = '30,4,0.4'; DECA_PROBE_POINT = '1' }
	},
	@{
		Name = 'sponza-gi-gpu'
		Desc = 'Probe GI GPU path against the CPU baseline'
		Args = @('--preview-probe', $Sponza, '<OUT>')
		Env  = @{ DECA_PROBE_GIGPU = '1'; DECA_PROBE_EYE = '-0.5,3,0.4'; DECA_PROBE_TARGET = '30,4,0.4' }
	},
	@{
		Name = 'fox-animation'
		Desc = 'Skeleton and clip import, skinning - numeric animation report'
		Args = @('--preview-probe', $Fox, '<OUT>')
		Env  = @{ DECA_PROBE_ANIMREPORT = '1' }
	},
	@{
		Name = 'fox-humanoid'
		Desc = 'Automatic humanoid avatar mapping from skeleton topology'
		Args = @('--preview-probe', $Fox, '<OUT>')
		Env  = @{ DECA_PROBE_HUMANOID = '1' }
	},
	@{
		Name = 'physics'
		Desc = 'Bepu world: gravity, ground contact, fixed step'
		Args = @('--preview-probe', $Fox, '<OUT>')
		Env  = @{ DECA_PROBE_PHYSICS = '1' }
	},
	@{
		Name = 'gameplay'
		Desc = 'Gameplay systems: character moving in a circle'
		Args = @('--preview-probe', $Fox, '<OUT>')
		Env  = @{ DECA_PROBE_GAMEPLAY = '1' }
	},
	@{
		Name = 'full-loop'
		Desc = 'Both render graphs in the real EditorManager order + feature toggling on the fly'
		Args = @('--full-loop', $Sponza, '300', '<BACKEND>')
		Env  = @{ DECA_LOOP_TOGGLE = '1' }
		<#
			The scenario's final line carries the frame numbers at which the model finished
			loading. That measures the MACHINE's speed, not engine behaviour: between warm runs
			they drift by a frame, and on the first run after a rebuild by five at once (cold JIT
			and a cold shader cache shift the whole async load). Widening the tolerance until it
			goes green just means checking nothing at all - so these fields are dropped by name.

			The claim under test remains: the model loaded, without error, within 300 frames, and
			streaming REACHED the end (the neighbouring `still streaming 0/69` line).
		#>
		IgnoreFields = @('finalized', 'texturesReady', 'visible', 'streamingComplete')
	}
)

<#
	Selection is not a whitelist of lines but a set of traits: any tagged line with a number is a
	metric until proven otherwise. A whitelist would have to be extended for every new field in a
	probe, and it would silently lose exactly the metrics added last - that is, the most interesting
	ones.

	What is dropped and why:
	  ` ms`   - timings jitter with machine load; comparing them means a red run every other time,
	            and a suite that lies stops being run;
	  `:\`    - absolute paths, they would tie the baseline to one specific machine;
	  compile / pso - shader compile counters depend on the state of DECA_SHADER_CACHE: a cold and
	            a warm cache give different numbers, and that has nothing to do with refactoring;
	  [diligent-*] - the driver's own log. --full-loop subscribes to ALL levels, and dynamic memory
	            page addresses pour out of it: they do not even match between two back-to-back runs
	            of the same code;
	  `[...] frame N:` - async streaming milestones. The frame number at which a texture finished
	            loading drifts by a frame or two due to background decode threads. The final lines
	            (`done:`, `final texture quality:`) stay - they hold the actual facts.
#>
$MetricNoise = @(
	' ms\b',
	':\\',
	'^\[probe\] (final |load )?(compile|pso)\b',
	'^\[probe\]\s+pso\b',
	'^\[diligent-',
	'^\[\w+\] frame \d+:'
)

function Get-Metrics {
	param([string[]]$Lines)

	$kept = foreach ($line in $Lines) {
		$trimmed = $line.TrimEnd()

		if ($trimmed -notmatch '^\[[\w-]+\]') { continue }
		if ($trimmed -notmatch '\d') { continue }

		$noisy = $false
		foreach ($pattern in $MetricNoise) {
			if ($trimmed -match $pattern) { $noisy = $true; break }
		}

		if (-not $noisy) { $trimmed }
	}

	return @($kept)
}

<#
	Comparing a metric line: numbers with a tolerance, everything else exactly. Parsing by numbers
	rather than by key=value pairs, because the probes' output formats differ, and adapting to each
	one means breaking the comparison every time a field is added to a probe.
#>
function Compare-MetricLine {
	param([string]$Expected, [string]$Actual, [double]$Tolerance, [string[]]$IgnoreFields)

	$numberPattern = '-?\d+(?:[.,]\d+)?'

	# Values of the listed fields are masked in BOTH lines, so only the number drops out of the
	# comparison, while the presence of the field itself is still checked by the line's shape.
	foreach ($field in $IgnoreFields) {
		$mask = "$([regex]::Escape($field))=$numberPattern"
		$Expected = [regex]::Replace($Expected, $mask, "$field=~")
		$Actual = [regex]::Replace($Actual, $mask, "$field=~")
	}
	$expectedShape = [regex]::Replace($Expected, $numberPattern, '#')
	$actualShape = [regex]::Replace($Actual, $numberPattern, '#')

	if ($expectedShape -ne $actualShape) {
		return "line shape changed"
	}

	$expectedNumbers = [regex]::Matches($Expected, $numberPattern)
	$actualNumbers = [regex]::Matches($Actual, $numberPattern)

	for ($i = 0; $i -lt $expectedNumbers.Count; $i++) {
		$e = [double]($expectedNumbers[$i].Value -replace ',', '.')
		$a = [double]($actualNumbers[$i].Value -replace ',', '.')
		$scale = [Math]::Max([Math]::Abs($e), 1.0)

		if ([Math]::Abs($e - $a) / $scale -gt $Tolerance) {
			return "number #$($i + 1): baseline $e, got $a"
		}
	}

	return $null
}

function Invoke-Scenario {
	param([hashtable]$Case)

	$outDir = Join-Path $OutRoot $Case.Name
	if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
	New-Item -ItemType Directory -Path $outDir -Force | Out-Null

	$arguments = foreach ($a in $Case.Args) {
		switch ($a) {
			'<OUT>' { $outDir }
			'<BACKEND>' { $Backend }
			default { $a }
		}
	}

	$restore = @{}
	try {
		foreach ($key in $Case.Env.Keys) {
			$restore[$key] = [Environment]::GetEnvironmentVariable($key)
			[Environment]::SetEnvironmentVariable($key, $Case.Env[$key])
		}

		# The backend is always passed to the scenario: hardware ray tracing is not compiled on
		# vulkan, and a baseline taken on one backend is meaningless to compare on another.
		$restore['DECA_PROBE_BACKEND'] = [Environment]::GetEnvironmentVariable('DECA_PROBE_BACKEND')
		[Environment]::SetEnvironmentVariable('DECA_PROBE_BACKEND', $Backend)

		Push-Location $BinDir
		try {
			$output = & $Exe @arguments 2>&1 | ForEach-Object { "$_" }
			$exitCode = $LASTEXITCODE
		}
		finally {
			Pop-Location
		}
	}
	finally {
		foreach ($key in $restore.Keys) {
			[Environment]::SetEnvironmentVariable($key, $restore[$key])
		}
	}

	$logPath = Join-Path $outDir 'probe.log'
	$output | Out-File -FilePath $logPath -Encoding utf8

	return [PSCustomObject]@{
		ExitCode = $exitCode
		Output   = $output
		LogPath  = $logPath
	}
}

# ---------------------------------------------------------------------------------------------

if (-not $SkipBuild) {
	Write-Host "[suite] building the editor (x64)..." -ForegroundColor Cyan
	& dotnet build (Join-Path $RepoRoot 'DecaEngine.Editor\DecaEngine.Editor.csproj') `
		-c Debug -p:Platform=x64 --nologo -v q | Out-Null
	if ($LASTEXITCODE -ne 0) {
		Write-Host "[suite] BUILD FAILED - nothing to run" -ForegroundColor Red
		exit 1
	}
}

if (-not (Test-Path $Exe)) {
	Write-Host "[suite] no $Exe - build the editor with -p:Platform=x64" -ForegroundColor Red
	exit 1
}

New-Item -ItemType Directory -Path $BaselineDir -Force | Out-Null
New-Item -ItemType Directory -Path $OutRoot -Force | Out-Null

# The outer @() is mandatory: PowerShell unwraps a single-element array, and $selected would become
# the scenario hashtable itself - whose .Count counts KEYS, which made a single scenario report as
# "1 of 4".
$selected = @(if ($Scenario) {
	$match = $Scenarios | Where-Object { $_.Name -eq $Scenario }
	if (-not $match) {
		Write-Host "[suite] no scenario '$Scenario'. Available: $(($Scenarios.Name) -join ', ')" -ForegroundColor Red
		exit 1
	}
	$match
} else {
	$Scenarios
})

$failures = @()
$recorded = 0

foreach ($case in $selected) {
	Write-Host ""
	Write-Host "[suite] $($case.Name) - $($case.Desc)" -ForegroundColor Cyan

	$run = Invoke-Scenario -Case $case

	if ($run.ExitCode -ne 0) {
		Write-Host "  FAILED (code $($run.ExitCode)), log: $($run.LogPath)" -ForegroundColor Red
		$run.Output | Select-Object -Last 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
		$failures += "$($case.Name): non-zero exit code"
		continue
	}

	$metrics = Get-Metrics -Lines $run.Output
	if ($metrics.Count -eq 0) {
		Write-Host "  the probe ran but printed NOT A SINGLE metric - nothing to compare" -ForegroundColor Yellow
		$failures += "$($case.Name): no metrics in the output"
		continue
	}

	$baselinePath = Join-Path $BaselineDir "$($case.Name).metrics"

	if ($Baseline) {
		$metrics | Out-File -FilePath $baselinePath -Encoding utf8
		Write-Host "  baseline recorded: $($metrics.Count) metrics" -ForegroundColor Green
		$recorded++
		continue
	}

	if (-not (Test-Path $baselinePath)) {
		Write-Host "  no baseline - run with -Baseline" -ForegroundColor Yellow
		$failures += "$($case.Name): no baseline"
		continue
	}

	$expected = @(Get-Content $baselinePath -Encoding utf8)
	$diffs = @()

	if ($expected.Count -ne $metrics.Count) {
		$diffs += "metric count was $($expected.Count), now $($metrics.Count)"
	}

	$caseTolerance = if ($case.ContainsKey('Tolerance')) { [double]$case.Tolerance } else { $Tolerance }
	$caseIgnored = if ($case.ContainsKey('IgnoreFields')) { [string[]]$case.IgnoreFields } else { @() }

	$common = [Math]::Min($expected.Count, $metrics.Count)
	for ($i = 0; $i -lt $common; $i++) {
		$problem = Compare-MetricLine -Expected $expected[$i] -Actual $metrics[$i] `
			-Tolerance $caseTolerance -IgnoreFields $caseIgnored
		if ($problem) {
			$diffs += "  baseline: $($expected[$i])"
			$diffs += "  actual  : $($metrics[$i])"
			$diffs += "  -> $problem"
		}
	}

	if ($diffs.Count -eq 0) {
		Write-Host "  matched: $($metrics.Count) metrics" -ForegroundColor Green
	} else {
		Write-Host "  MISMATCH" -ForegroundColor Red
		$diffs | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
		$failures += "$($case.Name): metrics diverged"
	}
}

Write-Host ""
if ($Baseline) {
	Write-Host "[suite] baseline recorded for $recorded scenarios in $BaselineDir" -ForegroundColor Green
	exit 0
}

if ($failures.Count -gt 0) {
	Write-Host "[suite] FAILED ($($failures.Count) of $($selected.Count)):" -ForegroundColor Red
	$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
	exit 1
}

Write-Host "[suite] all matched: $($selected.Count) scenarios" -ForegroundColor Green
exit 0
