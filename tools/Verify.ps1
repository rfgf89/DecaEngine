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

.PARAMETER Backend
	Бэкенд для проб: d3d12 (по умолчанию) или vulkan.

.EXAMPLE
	.\tools\Verify.ps1
	.\tools\Verify.ps1 -SkipProbes
#>
[CmdletBinding()]
param(
	[switch]$SkipProbes,
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

$stopwatch.Stop()
Write-Host ""
if ($probeExit -ne 0) {
	Write-Host "ПРОВЕРКА НЕ ПРОШЛА за $([int]$stopwatch.Elapsed.TotalSeconds) с" -ForegroundColor Red
	exit 1
}

Write-Host "Всё зелёное за $([int]$stopwatch.Elapsed.TotalSeconds) с" -ForegroundColor Green
exit 0
