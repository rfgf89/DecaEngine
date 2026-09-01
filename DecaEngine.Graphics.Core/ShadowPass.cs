using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Graphics;

/// <summary>
/// Покадровое расписание перерисовки теневых каскадов, разделяемое между системой сборки видов
/// (она решает, какие каскады перерисовываются в этом кадре, и ТОЛЬКО для них рефитит матрицы -
/// см. CullingAndRenderSystem.ComputeCascadeUpdateMask) и <see cref="ShadowPass"/> (его колбэк
/// при реплее замороженного графа исполняет суб-буферы только запланированных каскадов).
///
/// Инвариант: матрица каскада в Light-кбуфере (сэмплинг) обязана оставаться той, которой каскад
/// РЕНДЕРИЛСЯ в последний раз, - поэтому решение "перерисовать" и "перефитить" принимается одной
/// маской в одном месте. Дефолт - все каскады каждый кадр: конвейеры, где маску никто не пишет
/// (превью через SimpleCullingAndRenderSystem), сохраняют прежнее поведение.
/// </summary>
public sealed class ShadowCascadeSchedule
{
	public const int AllCascades = ~0;

	/// <summary>Стаггеринг ВКЛЮЧЁН по умолчанию: DECA_SHADOW_STAGGER=0 выключает его обратно на
	/// "все каскады каждый кадр" (запись команд ShadowPass прямо в буфер графа, без суб-буферов).
	///
	/// Историческая причина отключения по умолчанию - AV в DiligentMaterial.SetPipelineState при
	/// переключении фич конвейера (первым замеченным триггером была смена AO Ssao/Gtao). Тогда его
	/// приписали стаггерингу/суб-буферам ShadowPass, потому что баг ПРОЯВЛЯЛСЯ именно в теневой
	/// суб-команде пользовательского стека. Расследование (см. DECA_LOOP_TOGGLE=1 харнесс в
	/// FullLoopProbe) прогнало тот же тоггл с DECA_SHADOW_STAGGER=0 И =1 - падало ОДИНАКОВО в обоих
	/// случаях, то есть стаггеринг ни при чём: он лишь совпал по времени в самом первом отчёте.
	///
	/// Настоящая причина - DiligentPsoManager.CreateGraphicsPipelineState: дисковая PSO-библиотека
	/// D3D12 хранит записи по ИМЕНИ на весь процесс, а материалы пост-обработки (SsaoPassResources и
	/// подобные) при пересоздании (смена AO-техники, смена бэкенда/качества апскейлера) заводятся
	/// заново под тем же литеральным именем ("SSAO Material", "SSAO Composite Material" и т.п.).
	/// Повторная регистрация того же имени в библиотеке возвращает невалидный PSO без единой ошибки
	/// и без null - тот же класс дефекта, что уже был пойман и обойдён для КОМПЬЮТ-PSO (см. комментарий
	/// у DiligentPsoManager.CreateComputePipelineState), просто graphics-путь ещё не пересоздавал PSO
	/// с повторным именем В ПРЕДЕЛАХ ОДНОГО запуска до появления живых тумблеров фич. Починено там же
	/// (кэш используется только для ПЕРВОГО создания каждого имени за процесс); стаггеринг к падению
	/// отношения не имел и теперь безопасен так же, как был бы безопасен всегда.</summary>
	public static readonly bool StaggerEnabled =
		Environment.GetEnvironmentVariable("DECA_SHADOW_STAGGER") != "0";

	private int _renderMask = AllCascades;

	/// <summary>Маска кадра: бит i - каскад i перерисовывается. Пишется системой сборки видов ДО
	/// исполнения графа, читается колбэком <see cref="ShadowPass"/> при реплее (тот же поток).</summary>
	public void SetRenderMask(int mask) => _renderMask = mask;

	/// <summary>Следующий реплей перерисует все каскады - зовётся при каждой перезаписи команд
	/// графа (см. <see cref="ShadowPass.WriteCommands"/>).</summary>
	public void ForceAll() => _renderMask = AllCascades;

	public bool ShouldRender(int cascadeIndex) => (_renderMask & (1 << cascadeIndex)) != 0;
}

/// <summary>
/// Render-graph pass that culls and renders every shadow-map cascade (shared by all cameras).
/// Also owns the once-per-frame indirect-draw-buffer bookkeeping that both this pass and
/// <see cref="ForwardPass"/> depend on.
///
/// При заданном <see cref="ShadowCascadeSchedule"/> команды каждого каскада пишутся в СВОЙ
/// замороженный суб-буфер, а в буфер графа встаёт один Callback, который на каждом реплее
/// исполняет суб-буферы только запланированных каскадов. Пропущенный каскад не чистится и не
/// рисуется вовсе - его слайс shadow map хранит содержимое последнего рендера, а матрица
/// сэмплинга в Light-кбуфере остаётся той же (см. инвариант в ShadowCascadeSchedule).
/// </summary>
public sealed class ShadowPass : RenderGraphPass<ShadowPass.PassData>
{
	public override string Name => "Shadow Pass";

	private readonly IBatchRenderer _batchRenderer;
	private readonly DirectionalLightCascadeData _directionalLightCascadeData;
	private readonly ShadowCascadeSchedule? _schedule;
	private ICommandBuffer?[]? _cascadeCmds;

	public struct PassData
	{
	}

	public ShadowPass(IBatchRenderer batchRenderer, DirectionalLightCascadeData directionalLightCascadeData,
		ShadowCascadeSchedule? schedule = null)
	{
		_batchRenderer = batchRenderer;
		_directionalLightCascadeData = directionalLightCascadeData;

		// Выключенный стаггеринг = прежний путь записи, суб-буферы не заводятся вовсе.
		_schedule = ShadowCascadeSchedule.StaggerEnabled ? schedule : null;
	}

	public override PassData Setup(IRenderGraphBuilder builder)
	{
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_batchRenderer.CheckAndReallocateBuffers();

		var lights = _directionalLightCascadeData;
		if (!lights.IsCreated)
		{
			return;
		}

		if (_schedule is null)
		{
			// Прежнее поведение: все каскады каждый кадр, команды прямо в буфере графа.
			for (int i = 0; i < lights.viewData.Capacity; i++)
			{
				WriteCascadeCommands(cmd, i);
			}

			return;
		}

		// Любая перезапись команд графа - структурное изменение (новая сцена, реаллокация
		// инстанс-буферов, ресайз, тумблер фичи): первый реплей обязан перерисовать ВСЕ каскады,
		// иначе пропущенный каскад показывал бы содержимое, нарисованное до перестройки.
		_schedule.ForceAll();

		int cascadeCount = lights.viewData.Capacity;
		if (_cascadeCmds is null || _cascadeCmds.Length != cascadeCount)
		{
			_cascadeCmds = new ICommandBuffer[cascadeCount];
		}

		for (int i = 0; i < cascadeCount; i++)
		{
			// Суб-буферы переживают перекомпиляцию графа (BeginRecording сбрасывает замороженный);
			// их UpdateBuffer-команды, как и у буферов графа, хранят сырые указатели в нативную
			// память lights.* и перечитывают её на каждом реплее.
			var sub = _cascadeCmds[i] ??= context.Api.CreateCommandBuffer();
			sub.BeginRecording();
			WriteCascadeCommands(sub, i);
			sub.Freeze();

			// Гейт каскада - ЯВНАЯ команда в буфере графа (ICommandBuffer.ExecuteNested), а не
			// колбэк-замыкание над _cascadeCmds: sub/_schedule/i лежат как данные внутри записи
			// команды и потому обновляются той же дисциплиной BeginRecording/перезаписи, что и
			// SetPipelineState чуть выше в WriteCascadeCommands - если этот WriteCommands не
			// перезапишет их на следующей компиляции, они не переживут её ни в каком виде (см.
			// ICommandBuffer.ExecuteNested).
			cmd.ExecuteNested(sub, _schedule, i);
		}
	}

	private void WriteCascadeCommands(ICommandBuffer cmd, int cascadeIndex)
	{
		var lights = _directionalLightCascadeData;

		_batchRenderer.ClearIndirectDrawBuffers(cmd);

		_batchRenderer.SetupViewData(cmd, ref lights.viewData.GetRef(cascadeIndex, false));
		_batchRenderer.SetupCullData(cmd, ref lights.cullData.GetRef(cascadeIndex, false));
		_batchRenderer.SetupLightData(cmd, ref lights.lightData.GetRef(cascadeIndex, false));

		var shadowCullResult = _batchRenderer.ExecuteComputeCulling(cmd, cascadeIndex);
		_batchRenderer.ExecuteDrawShadows(cmd, shadowCullResult, cascadeIndex);
	}
}
