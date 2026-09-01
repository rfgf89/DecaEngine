<#
.SYNOPSIS
	Прогоняет DECA_PROBE_*-гарнессы как регрессионный набор и сверяет их числа с эталоном.

.DESCRIPTION
	Тестов на рендер в проекте нет и быть не может: проверять картинку ассертами дороже, чем её
	рисовать. Зато пробы уже печатают числа, по которым видно ЧТО ИМЕННО сломалось - светимость
	кадра, число сущностей, совпадение BVH с кэшем. Скрипт превращает эти числа в эталон.

	Смысл именно в рефакторинге: перенос кода между проектами и распил файлов не должны менять
	НИ ОДНОГО из этих чисел. Если после переезда ProbeGi в свой модуль светимость поля уехала -
	переезд был не механическим, и это видно сразу, а не через неделю на скриншоте.

	Строки с временем (` ms`) в эталон не попадают: они пляшут от запуска к запуску и утопили бы
	сигнал. Числа сверяются с допуском, текст - точно.

.PARAMETER Baseline
	Записать текущие числа как эталон вместо сверки. Делать это можно ТОЛЬКО на заведомо
	исправном дереве - обычно сразу после коммита, который прошёл сверку.

.PARAMETER Scenario
	Прогнать один сценарий вместо всех. Имена - в $Scenarios ниже.

.PARAMETER Tolerance
	Относительный допуск для чисел, по умолчанию 1%. Светимость считается на GPU, и последний
	знак имеет право гулять.

.PARAMETER Backend
	d3d12 (по умолчанию) или vulkan. Аппаратная трассировка компилируется только на d3d12.

.PARAMETER SkipBuild
	Не пересобирать редактор. Для повторного прогона после падения.

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
	[switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$RepoRoot = Split-Path -Parent $PSScriptRoot
# ЛОВУШКА ПУТИ: сборка с -p:Platform=x64 кладёт свежие DLL в bin\x64\..., а в bin\Debug\... лежит
# СТАРЫЙ комплект - там обновляется exe, но не DecaEngine.Graphics.Diligent.dll. Запуск оттуда
# тихо гоняет вчерашний код. Только bin\x64.
$BinDir = Join-Path $RepoRoot 'DecaEngine.Editor\bin\x64\Debug\net10.0'
$Exe = Join-Path $BinDir 'DecaEngine.Editor.exe'
# Не "probe-baseline": .gitignore выбрасывает probe-*/ на любом уровне (там ~150 каталогов
# с выводом проб), и эталоны молча не попадали бы в коммит.
$BaselineDir = Join-Path $PSScriptRoot 'baselines'
$OutRoot = Join-Path $RepoRoot '_probeout\suite'

# Пробы ищут EditorAssets рядом с exe, поэтому пути к моделям - относительные от bin.
$Sponza = 'EditorAssets/models/Sponza.gltf'
$Fox = 'EditorAssets/models/Fox.glb'

$Scenarios = @(
	@{
		Name = 'sponza-base'
		Desc = 'Загрузка модели, материалы, тени, SSAO/SSGI, probe GI - основной путь превью'
		Args = @('--preview-probe', $Sponza, '<OUT>')
		Env  = @{}
	},
	@{
		Name = 'sponza-interior'
		Desc = 'Интерьерный кадр с точечным светом: probe GI трассирует лампы'
		Args = @('--preview-probe', $Sponza, '<OUT>')
		Env  = @{ DECA_PROBE_EYE = '-0.5,3,0.4'; DECA_PROBE_TARGET = '30,4,0.4'; DECA_PROBE_POINT = '1' }
	},
	@{
		Name = 'sponza-gi-gpu'
		Desc = 'GPU-путь probe GI против CPU-эталона'
		Args = @('--preview-probe', $Sponza, '<OUT>')
		Env  = @{ DECA_PROBE_GIGPU = '1'; DECA_PROBE_EYE = '-0.5,3,0.4'; DECA_PROBE_TARGET = '30,4,0.4' }
	},
	@{
		Name = 'fox-animation'
		Desc = 'Импорт скелета и клипов, скиннинг - численный отчёт по анимации'
		Args = @('--preview-probe', $Fox, '<OUT>')
		Env  = @{ DECA_PROBE_ANIMREPORT = '1' }
	},
	@{
		Name = 'fox-humanoid'
		Desc = 'Автоматическая разметка гуманоидного аватара по топологии скелета'
		Args = @('--preview-probe', $Fox, '<OUT>')
		Env  = @{ DECA_PROBE_HUMANOID = '1' }
	},
	@{
		Name = 'physics'
		Desc = 'Мир Bepu: гравитация, контакт с полом, фиксированный шаг'
		Args = @('--preview-probe', $Fox, '<OUT>')
		Env  = @{ DECA_PROBE_PHYSICS = '1' }
	},
	@{
		Name = 'gameplay'
		Desc = 'Геймплейные системы: движение персонажа по кругу'
		Args = @('--preview-probe', $Fox, '<OUT>')
		Env  = @{ DECA_PROBE_GAMEPLAY = '1' }
	},
	@{
		Name = 'full-loop'
		Desc = 'Оба рендер-графа в реальном порядке EditorManager + переключение фич на лету'
		Args = @('--full-loop', $Sponza, '300', '<BACKEND>')
		Env  = @{ DECA_LOOP_TOGGLE = '1' }
		<#
			Итоговая строка сценария несёт номера кадров, на которых модель дозагрузилась. Это
			замер скорости МАШИНЫ, а не поведения движка: между тёплыми прогонами они гуляют на
			кадр, а на первом прогоне после пересборки - сразу на пять (холодные JIT и кэш
			шейдеров сдвигают всю асинхронную загрузку). Расширять допуск, пока не позеленеет,
			значит просто перестать что-либо проверять - поэтому эти поля выброшены поимённо.

			Проверяемое утверждение остаётся: модель загрузилась, без ошибки, за 300 кадров, и
			стриминг ДОШЁЛ до конца (соседняя строка `still streaming 0/69`).
		#>
		IgnoreFields = @('finalized', 'texturesReady', 'visible', 'streamingComplete')
	}
)

<#
	Отбор идёт не белым списком строк, а по признакам: любая тегированная строка с числом -
	метрика, пока не доказано обратное. Белый список пришлось бы дописывать под каждое новое поле
	в пробе, и он молча терял бы ровно те метрики, которые добавили последними, - то есть самые
	интересные.

	Что выбрасывается и почему:
	  ` ms`   - времена пляшут от загрузки машины; сверять их значит получать красный прогон через
	            раз, а набор, который врёт, перестают запускать;
	  `:\`    - абсолютные пути, они привязали бы эталон к конкретной машине;
	  compile / pso - счётчики компиляций шейдеров зависят от состояния DECA_SHADER_CACHE:
	            холодный и тёплый кэш дают разные числа, и к рефакторингу это отношения не имеет;
	  [diligent-*] - лог самого драйвера. --full-loop подписан на ВСЕ уровни, и оттуда сыплются
	            адреса страниц динамической памяти: они не совпадают даже между двумя подряд
	            запусками одного и того же кода;
	  `[...] frame N:` - вехи асинхронного стриминга. Номер кадра, на котором дозагрузилась
	            текстура, гуляет на кадр-другой от фоновых потоков декодирования. Итоговые строки
	            (`done:`, `final texture quality:`) остаются - в них лежат сами факты.
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
	Сверка строки метрики: числа - с допуском, всё остальное - точно. Разбор по числам, а не по
	парам ключ=значение, потому что формат вывода у проб разный, и подстраиваться под каждую
	значит ломать сверку каждый раз, когда в пробу добавили поле.
#>
function Compare-MetricLine {
	param([string]$Expected, [string]$Actual, [double]$Tolerance, [string[]]$IgnoreFields)

	$numberPattern = '-?\d+(?:[.,]\d+)?'

	# Значения перечисленных полей затираются в ОБЕИХ строках, поэтому из сверки уходит только
	# число, а само наличие поля по-прежнему проверяется формой строки.
	foreach ($field in $IgnoreFields) {
		$mask = "$([regex]::Escape($field))=$numberPattern"
		$Expected = [regex]::Replace($Expected, $mask, "$field=~")
		$Actual = [regex]::Replace($Actual, $mask, "$field=~")
	}
	$expectedShape = [regex]::Replace($Expected, $numberPattern, '#')
	$actualShape = [regex]::Replace($Actual, $numberPattern, '#')

	if ($expectedShape -ne $actualShape) {
		return "форма строки изменилась"
	}

	$expectedNumbers = [regex]::Matches($Expected, $numberPattern)
	$actualNumbers = [regex]::Matches($Actual, $numberPattern)

	for ($i = 0; $i -lt $expectedNumbers.Count; $i++) {
		$e = [double]($expectedNumbers[$i].Value -replace ',', '.')
		$a = [double]($actualNumbers[$i].Value -replace ',', '.')
		$scale = [Math]::Max([Math]::Abs($e), 1.0)

		if ([Math]::Abs($e - $a) / $scale -gt $Tolerance) {
			return "число #$($i + 1): эталон $e, получено $a"
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

		# Бэкенд задаётся сценарию всегда: на vulkan аппаратная трассировка не компилируется,
		# и эталон, снятый на одном бэкенде, на другом сверять бессмысленно.
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
	Write-Host "[suite] сборка редактора (x64)..." -ForegroundColor Cyan
	& dotnet build (Join-Path $RepoRoot 'DecaEngine.Editor\DecaEngine.Editor.csproj') `
		-c Debug -p:Platform=x64 --nologo -v q | Out-Null
	if ($LASTEXITCODE -ne 0) {
		Write-Host "[suite] СБОРКА УПАЛА - прогонять нечего" -ForegroundColor Red
		exit 1
	}
}

if (-not (Test-Path $Exe)) {
	Write-Host "[suite] нет $Exe - соберите редактор с -p:Platform=x64" -ForegroundColor Red
	exit 1
}

New-Item -ItemType Directory -Path $BaselineDir -Force | Out-Null
New-Item -ItemType Directory -Path $OutRoot -Force | Out-Null

# @() снаружи обязательно: PowerShell разворачивает массив из одного элемента, и $selected стал бы
# самой хеш-таблицей сценария - а .Count у неё считает КЛЮЧИ, из-за чего один сценарий отчитывался
# как «1 из 4».
$selected = @(if ($Scenario) {
	$match = $Scenarios | Where-Object { $_.Name -eq $Scenario }
	if (-not $match) {
		Write-Host "[suite] нет сценария '$Scenario'. Есть: $(($Scenarios.Name) -join ', ')" -ForegroundColor Red
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
		Write-Host "  УПАЛА (код $($run.ExitCode)), лог: $($run.LogPath)" -ForegroundColor Red
		$run.Output | Select-Object -Last 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
		$failures += "$($case.Name): ненулевой код возврата"
		continue
	}

	$metrics = Get-Metrics -Lines $run.Output
	if ($metrics.Count -eq 0) {
		Write-Host "  проба отработала, но не напечатала НИ ОДНОЙ метрики - сверять нечего" -ForegroundColor Yellow
		$failures += "$($case.Name): нет метрик в выводе"
		continue
	}

	$baselinePath = Join-Path $BaselineDir "$($case.Name).metrics"

	if ($Baseline) {
		$metrics | Out-File -FilePath $baselinePath -Encoding utf8
		Write-Host "  эталон записан: $($metrics.Count) метрик" -ForegroundColor Green
		$recorded++
		continue
	}

	if (-not (Test-Path $baselinePath)) {
		Write-Host "  эталона нет - запустите с -Baseline" -ForegroundColor Yellow
		$failures += "$($case.Name): нет эталона"
		continue
	}

	$expected = @(Get-Content $baselinePath -Encoding utf8)
	$diffs = @()

	if ($expected.Count -ne $metrics.Count) {
		$diffs += "метрик было $($expected.Count), стало $($metrics.Count)"
	}

	$caseTolerance = if ($case.ContainsKey('Tolerance')) { [double]$case.Tolerance } else { $Tolerance }
	$caseIgnored = if ($case.ContainsKey('IgnoreFields')) { [string[]]$case.IgnoreFields } else { @() }

	$common = [Math]::Min($expected.Count, $metrics.Count)
	for ($i = 0; $i -lt $common; $i++) {
		$problem = Compare-MetricLine -Expected $expected[$i] -Actual $metrics[$i] `
			-Tolerance $caseTolerance -IgnoreFields $caseIgnored
		if ($problem) {
			$diffs += "  эталон: $($expected[$i])"
			$diffs += "  стало : $($metrics[$i])"
			$diffs += "  -> $problem"
		}
	}

	if ($diffs.Count -eq 0) {
		Write-Host "  совпало: $($metrics.Count) метрик" -ForegroundColor Green
	} else {
		Write-Host "  РАСХОЖДЕНИЕ" -ForegroundColor Red
		$diffs | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
		$failures += "$($case.Name): метрики разошлись"
	}
}

Write-Host ""
if ($Baseline) {
	Write-Host "[suite] эталон записан для $recorded сценариев в $BaselineDir" -ForegroundColor Green
	exit 0
}

if ($failures.Count -gt 0) {
	Write-Host "[suite] ПРОВАЛ ($($failures.Count) из $($selected.Count)):" -ForegroundColor Red
	$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
	exit 1
}

Write-Host "[suite] всё сошлось: $($selected.Count) сценариев" -ForegroundColor Green
exit 0
