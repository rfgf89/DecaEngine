<#
.SYNOPSIS
	Полная проверка дерева: сборка решения, юнит-тесты, регрессия проб.

.DESCRIPTION
	Одна команда на весь рефакторинг. Перенос кода между проектами и распил файлов не должны
	менять поведение - значит, после каждого шага это должно быть зелёным, и если не зелёное,
	шаг был не механическим.

	Три уровня, от дешёвого к дорогому, и останов на первом упавшем: гонять пробы на дереве,
	которое не собирается, смысла нет.

.PARAMETER SkipProbes
	Только сборка и юнит-тесты (секунды вместо минут). Для проверки на ходу; перед коммитом
	прогонять полностью.

.PARAMETER SkipEditor
	Не запускать редактор. Запуск открывает окно на несколько секунд - мешает, если проверка идёт
	фоном.

.PARAMETER Backend
	Бэкенд для проб: d3d12 (по умолчанию) или vulkan.

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

# Diligent не работает на AnyCPU - платформа задаётся везде явно.
Write-Step "Сборка решения (x64)"
& dotnet build (Join-Path $RepoRoot 'DecaEngine.sln') -c Debug -p:Platform=x64 --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) {
	Write-Host "СБОРКА УПАЛА" -ForegroundColor Red
	exit 1
}
Write-Host "  собралось" -ForegroundColor Green

Write-Step "Юнит-тесты"
$testOutput = & dotnet test (Join-Path $RepoRoot 'DecaEngine.Tests\DecaEngine.Tests.csproj') `
	-c Debug -p:Platform=x64 --nologo -v q --no-build 2>&1
$testExit = $LASTEXITCODE
$summary = $testOutput | Select-String -Pattern 'Passed!|Failed!|error'
if ($testExit -ne 0) {
	Write-Host "ТЕСТЫ УПАЛИ" -ForegroundColor Red
	$testOutput | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" }
	exit 1
}
$summary | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }

if ($SkipProbes) {
	Write-Host ""
	Write-Host "Пробы пропущены (-SkipProbes). Перед коммитом прогнать полностью." -ForegroundColor Yellow
	exit 0
}

Write-Step "Регрессия проб"
& (Join-Path $PSScriptRoot 'Run-ProbeSuite.ps1') -SkipBuild -Backend $Backend
$probeExit = $LASTEXITCODE

if ($probeExit -eq 0 -and -not $SkipEditor) {
	<#
		Пробники поднимают графику, но НЕ поднимают ImGui: у них нет окна. Из-за этого весь набор
		оставался зелёным, пока редактор вообще не стартовал - шейдеры ImGui приезжали в вывод из
		проекта СЭМПЛОВ, ссылку на который убрали как ненужную, и первый же запуск падал на
		NullReferenceException при создании PSO без вершинного шейдера.

		238 метрик о таком молчат по построению. Поэтому редактор здесь просто ЗАПУСКАЕТСЯ:
		проверяется ровно то, что не покрывают пробы, - что процесс доходит до кадров и не падает.
	#>
	Write-Step "Запуск редактора"

	$editorExe = Join-Path $RepoRoot 'DecaEngine.Editor.App\bin\x64\Debug\net10.0\DecaEngine.Editor.App.exe'
	if (-not (Test-Path $editorExe)) {
		Write-Host "  нет $editorExe" -ForegroundColor Red
		$probeExit = 1
	}
	else {
		$outFile = Join-Path $env:TEMP 'deca_verify_editor_out.txt'
		$errFile = Join-Path $env:TEMP 'deca_verify_editor_err.txt'
		$proc = Start-Process -FilePath $editorExe -WorkingDirectory (Split-Path $editorExe) `
			-PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile

		# Инициализация графики, ImGui и первые кадры укладываются в несколько секунд; падение на
		# старте происходит гораздо раньше.
		Start-Sleep -Seconds 12

		if ($proc.HasExited) {
			Write-Host "  РЕДАКТОР УПАЛ НА СТАРТЕ (код $($proc.ExitCode))" -ForegroundColor Red
			Get-Content $errFile -Tail 25 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "      $_" }
			Get-Content $outFile -Tail 25 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "      $_" }
			$probeExit = 1
		}
		else {
			Stop-Process -Id $proc.Id -Force
			Write-Host "  стартовал и держится" -ForegroundColor Green
		}
	}
}

$stopwatch.Stop()
Write-Host ""
if ($probeExit -ne 0) {
	Write-Host "ПРОВЕРКА НЕ ПРОШЛА за $([int]$stopwatch.Elapsed.TotalSeconds) с" -ForegroundColor Red
	exit 1
}

Write-Host "Всё зелёное за $([int]$stopwatch.Elapsed.TotalSeconds) с" -ForegroundColor Green
exit 0
