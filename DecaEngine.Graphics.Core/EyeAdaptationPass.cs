using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;

namespace DecaEngine.Core;

/// <summary>Owns the GPU resources for auto-exposure ("eye adaptation"): the log-luminance reduction
/// chain over the HDR frame plus the 1x1 ping-pong pair holding the temporally smoothed luminance
/// the <see cref="TonemapPassResources"/> divides by. Created once by
/// <see cref="GraphicsPipelineSimple"/> when eye adaptation is enabled - same ownership pattern as
/// <see cref="SsaoPassResources"/>/<see cref="SsgiPassResources"/>: the pass itself is rebuilt every
/// frame, the resources live here and take live knob pushes through
/// <see cref="SetParams"/>/<see cref="SetDeltaTime"/> (работает и с заморожённым командным буфером).
/// </summary>
public sealed unsafe class EyeAdaptationPassResources : IReleaseObject
{
	/// <summary>Сторона первого звена цепочки редукции. 64 -> 8 -> 1: два звена по 8x8 тапов
	/// покрывают его ЦЕЛИКОМ, так что усреднение точное, без подвыборки (см. LuminanceReducePS.hlsl).</summary>
	private const uint LuminanceSize = 64;

	private readonly IRenderTarget _lum64;
	private readonly IRenderTarget _lum8;
	private readonly IRenderTarget _lum1;

	// Пинг-понг адаптации: два 1x1-таргета, за кадр рисуются оба (A->B, B->A) - чередовать привязки
	// по чётности кадра нельзя, командный буфер графа заморожен (см. WriteCommands ниже).
	private readonly IRenderTarget _adaptA;
	private readonly IRenderTarget _adaptB;

	private readonly IMaterialObject _initMaterial;
	private readonly IMaterialObject _reduce64Material;
	private readonly IMaterialObject _reduce8Material;
	private readonly IMaterialObject _adaptAtoB;
	private readonly IMaterialObject _adaptBtoA;

	// Кбуфер "EyeAdaptation" - СВОЙ на каждое звено (у них разные размеры таргета/источника) и с
	// собственной unmanaged-памятью под данные: параметры пишутся туда без единого обращения к GPU,
	// а заливает их сам пасс командой UpdateBuffer по этому указателю (заморожённый буфер
	// перечитывает память при каждом реплее - см. IBatchRenderer.SetupViewData, тот же приём).
	//
	// Именно так, а НЕ через IMaterialObject.SetConstant: тот попутно переустанавливает переменную в
	// SRB, и обновление дескриптор-сета, пока предыдущий кадр ещё в полёте, роняет Vulkan-валидацию
	// ("bound VkDescriptorSet was destroyed or updated"). Настройкам, которые меняет человек раз в
	// сессию (см. SsaoPassResources), это сходит с рук; покадровому dt - нет.
	private const int MaterialCount = 5;
	private const int InitIndex = 0, Reduce64Index = 1, Reduce8Index = 2, AdaptAtoBIndex = 3, AdaptBtoAIndex = 4;

	private readonly IBufferHandle[] _constantBuffers = new IBufferHandle[MaterialCount];
	private readonly EyeAdaptationConstantsData* _constants;

	/// <summary>1x1 RGBA32F с адаптированной яркостью кадра - то, что читает тонемап. Всегда
	/// <see cref="_adaptA"/>: за кадр рисуются оба шага пинг-понга (A->B, затем B->A), так что
	/// последним записан именно он.</summary>
	public IGpuTexture AdaptationTarget => _adaptA;

	public EyeAdaptationPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string colorTargetName,
		IGpuTexture hdrColorTarget)
	{
		// Лог-яркость бывает отрицательной (тёмная сцена) и вылезает далеко за единицу - RGBA8 здесь
		// не годится, а одноканального float-формата в TextureObjectFormat нет.
		_lum64 = CreateLuminanceTarget(graphicsApi, colorTargetName + " Luminance 64", LuminanceSize);
		_lum8 = CreateLuminanceTarget(graphicsApi, colorTargetName + " Luminance 8", 8);
		_lum1 = CreateLuminanceTarget(graphicsApi, colorTargetName + " Luminance 1", 1);

		// Адаптация - в полной точности: значение живёт между кадрами, и накопленная ошибка half
		// проявлялась бы как «дребезг» экспозиции на статичной картинке.
		_adaptA = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Adaptation A",
			width = 1,
			height = 1,
			format = TextureObjectFormat.R32G32B32A32Float,
		});
		_adaptB = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " Adaptation B",
			width = 1,
			height = 1,
			format = TextureObjectFormat.R32G32B32A32Float,
		});

		var lumState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Eye Adaptation Luminance PSO",
			RenderTargetFormats = [TextureObjectFormat.R16G16B16A16Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var adaptState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Eye Adaptation PSO",
			RenderTargetFormats = [TextureObjectFormat.R32G32B32A32Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var sampler = graphicsApi.CreateSampler(
			name: "Eye Adaptation Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		// У КАЖДОГО материала свой экземпляр VS - см. комментарий в SsaoPassResources (шареный
		// шейдер освобождался бы дважды при пересоздании окружения).
		_initMaterial = CreateMaterial(graphicsApi, batchRenderer, "Luminance Init", "LuminanceInitPS.hlsl", lumState);
		_initMaterial.SetTexture("_SceneTex", hdrColorTarget);
		_initMaterial.SetImmutableSampler("_SceneTex", sampler);

		_reduce64Material = CreateMaterial(graphicsApi, batchRenderer, "Luminance Reduce 64", "LuminanceReducePS.hlsl", lumState);
		_reduce64Material.SetTexture("_LumTex", _lum64);
		_reduce64Material.SetImmutableSampler("_LumTex", sampler);

		_reduce8Material = CreateMaterial(graphicsApi, batchRenderer, "Luminance Reduce 8", "LuminanceReducePS.hlsl", lumState);
		_reduce8Material.SetTexture("_LumTex", _lum8);
		_reduce8Material.SetImmutableSampler("_LumTex", sampler);

		_adaptAtoB = CreateMaterial(graphicsApi, batchRenderer, "Eye Adaptation A->B", "EyeAdaptationPS.hlsl", adaptState);
		_adaptAtoB.SetTexture("_LumTex", _lum1);
		_adaptAtoB.SetTexture("_PrevTex", _adaptA);

		_adaptBtoA = CreateMaterial(graphicsApi, batchRenderer, "Eye Adaptation B->A", "EyeAdaptationPS.hlsl", adaptState);
		_adaptBtoA.SetTexture("_LumTex", _lum1);
		_adaptBtoA.SetTexture("_PrevTex", _adaptB);

		// Кбуферы создаются и привязываются РОВНО ОДИН раз - дальше меняется только их содержимое
		// (см. комментарий у _constants).
		_constants = (EyeAdaptationConstantsData*)NativeMemory.AllocZeroed(
			(nuint)MaterialCount, (nuint)sizeof(EyeAdaptationConstantsData));

		BindConstants(graphicsApi, InitIndex, _initMaterial);
		BindConstants(graphicsApi, Reduce64Index, _reduce64Material);
		BindConstants(graphicsApi, Reduce8Index, _reduce8Material);
		BindConstants(graphicsApi, AdaptAtoBIndex, _adaptAtoB);
		BindConstants(graphicsApi, AdaptBtoAIndex, _adaptBtoA);

		InitConstantSizes();

		// Дефолты - до первого пуша из окна Graphics: пасс рисует с первого кадра, и cbuffer иначе
		// остался бы с мусором (ровно та же причина, что в SsaoPassResources).
		// Пинг-понг НЕ очищается: разовая очистка вне графа ломает лейауты на Vulkan
		// (VUID-vkCmdClearColorImage-imageLayout-00004), а внутри графа она была бы не разовой -
		// командный буфер заморожен. Мусор свежесозданного таргета обезвреживает сам шейдер
		// адаптации (см. EyeAdaptationPS.hlsl: проверка «не > 0» плюс кламп в диапазон яркостей).
		SetParams(0.18f, 0.03f, 8f, 0f);
		SetDeltaTime(0f);
	}

	private void BindConstants(IGraphicsApi graphicsApi, int index, IMaterialObject material)
	{
		// dynamic = false: динамические буферы Diligent обновляет через Map, а нам нужен именно
		// UpdateBuffer из командного буфера (USAGE_DEFAULT).
		var buffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "EyeAdaptation",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(EyeAdaptationConstantsData),
		});

		_constantBuffers[index] = buffer;
		material.SetBuffer("EyeAdaptation", buffer, HandleAccess.Pixel);
	}

	private static IRenderTarget CreateLuminanceTarget(IGraphicsApi graphicsApi, string name, uint size)
	{
		return graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = name,
			width = size,
			height = size,
			format = TextureObjectFormat.R16G16B16A16Float,
		});
	}

	private static IMaterialObject CreateMaterial(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string name,
		string pixelShaderFile, IStateObject state)
	{
		var vs = graphicsApi.CreateShader(name + " VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader(name + " PS", "EditorAssets/shader", pixelShaderFile, ShaderObjectType.Pixel);

		var material = graphicsApi.CreateMaterial(name + " Material");
		material.SetShader(vs, ps);
		material.SetState(state);
		batchRenderer.BindViewConstants(material);
		return material;
	}

	/// <summary>Layout кбуфера "EyeAdaptation" в EyeAdaptationCommon.hlsl.</summary>
	private struct EyeAdaptationConstantsData
	{
		/// <summary>xy = размер таргета в пикселях, zw = 1/xy.</summary>
		public Vector4 Target;

		/// <summary>xy = размер источника в пикселях, zw = 1/xy.</summary>
		public Vector4 Source;

		/// <summary>x = key value, y = мин. яркость, z = макс. яркость, w = экспокоррекция (EV).</summary>
		public Vector4 Params;

		/// <summary>x = deltaTime, y = скорость адаптации к светлому, z = к тёмному, w - резерв.</summary>
		public Vector4 Params2;
	}

	private float _key = 0.18f;
	private float _minLuminance = 0.03f;
	private float _maxLuminance = 8f;
	private float _exposureCompensation;
	private float _speedUp = 3f;
	private float _speedDown = 1f;
	private float _deltaTime;

	/// <summary>Живые ручки окна Graphics: целевая средняя яркость кадра, границы измеренной яркости
	/// (защита от полностью чёрного/выжженного кадра) и экспокоррекция в стопах.</summary>
	public void SetParams(float key, float minLuminance, float maxLuminance, float exposureCompensation)
	{
		_key = key;
		_minLuminance = minLuminance;
		_maxLuminance = MathF.Max(maxLuminance, minLuminance);
		_exposureCompensation = exposureCompensation;
		UpdateConstants();
	}

	/// <summary>Скорости временной адаптации (1/сек): раздельные для «зажмуриться» и «привыкнуть к
	/// темноте» - глаз тоже адаптируется к свету заметно быстрее, чем к темноте.</summary>
	public void SetSpeed(float speedUp, float speedDown)
	{
		_speedUp = speedUp;
		_speedDown = speedDown;
		UpdateConstants();
	}

	/// <summary>Шаг времени кадра - пушится каждый кадр перед <see cref="EyeAdaptationPass"/>
	/// (см. ModelPreviewViewport.Update). Половина уходит в каждый из двух шагов пинг-понга, так что
	/// суммарная скорость адаптации от их числа не зависит.</summary>
	public void SetDeltaTime(float deltaTime)
	{
		_deltaTime = MathF.Max(deltaTime, 0f);
		UpdateConstants();
	}

	/// <summary>Размеры таргета/источника у каждого звена свои и не меняются никогда - пишутся один
	/// раз при создании (см. LuminanceInitPS.hlsl / LuminanceReducePS.hlsl: по ним считаются uv).</summary>
	private void InitConstantSizes()
	{
		SetSizes(InitIndex, LuminanceSize, LuminanceSize);
		SetSizes(Reduce64Index, 8, LuminanceSize);
		SetSizes(Reduce8Index, 1, 8);
		SetSizes(AdaptAtoBIndex, 1, 1);
		SetSizes(AdaptBtoAIndex, 1, 1);
	}

	private void SetSizes(int index, uint targetSize, uint sourceSize)
	{
		_constants[index].Target = new Vector4(targetSize, targetSize, 1f / targetSize, 1f / targetSize);
		_constants[index].Source = new Vector4(sourceSize, sourceSize, 1f / sourceSize, 1f / sourceSize);
	}

	/// <summary>Обновляет ТОЛЬКО CPU-копию кбуферов - на GPU их зальёт сам пасс (см.
	/// <see cref="WriteCommands"/>), поэтому вызывать можно когда угодно, в том числе каждый кадр.</summary>
	private void UpdateConstants()
	{
		var lumParams = new Vector4(_key, _minLuminance, _maxLuminance, _exposureCompensation);

		// Половина dt на каждый из двух шагов пинг-понга - суммарная скорость адаптации от их числа
		// не зависит.
		var lumParams2 = new Vector4(_deltaTime * 0.5f, _speedUp, _speedDown, 0f);

		for (int i = 0; i < MaterialCount; i++)
		{
			_constants[i].Params = lumParams;
			_constants[i].Params2 = lumParams2;
		}
	}

	/// <summary>Перепривязывает HDR-кадр ПОСЛЕ его Resize - Resize пересоздаёт нативную текстуру, и
	/// SRB иначе держал бы уничтоженную (см. ModelPreviewViewport.ResizeTargets). Сами таргеты
	/// цепочки фиксированного размера и ресайза не требуют.</summary>
	public void RebindTargets(IGpuTexture hdrColorTarget)
	{
		_initMaterial.SetTexture("_SceneTex", hdrColorTarget);
	}

	/// <summary>Цепочка редукции + два шага пинг-понга адаптации - см. <see cref="EyeAdaptationPass"/>.
	/// Вызывающий обязан отвязать render-таргеты и перевести HDR-кадр в ShaderResource.</summary>
	internal void WriteCommands(ICommandBuffer cmd)
	{
		// Оба таргета пинг-понга статически привязаны как _PrevTex, и в ПЕРВОМ кадре ни один ещё не
		// побывал render-таргетом: на Vulkan они лежат в UNDEFINED, и валидация падает на первом же
		// дроу (VUID-vkCmdDraw-None-09600). Ровно та же история, что со снимком сцены в ForwardPass.
		cmd.TransitionResource(_adaptA, ResourceState.ShaderResource);
		cmd.TransitionResource(_adaptB, ResourceState.ShaderResource);

		Draw(cmd, InitIndex, _initMaterial, _lum64, LuminanceSize);
		Draw(cmd, Reduce64Index, _reduce64Material, _lum8, 8);
		Draw(cmd, Reduce8Index, _reduce8Material, _lum1, 1);

		// Два шага одного кадра: A->B, затем B->A. Каждый берёт половину dt (см. UpdateConstants), а
		// «текущей» адаптацией остаётся A - его и читает тонемап.
		Draw(cmd, AdaptAtoBIndex, _adaptAtoB, _adaptB, 1);
		Draw(cmd, AdaptBtoAIndex, _adaptBtoA, _adaptA, 1);
	}

	private void Draw(ICommandBuffer cmd, int index, IMaterialObject material, IRenderTarget target, uint size)
	{
		// Заливка кбуфера - командой, а не SetConstant: команда перечитывает CPU-память при КАЖДОМ
		// реплее заморожённого буфера, так что покадровый dt доезжает без пересборки графа и без
		// обновления дескрипторов на лету (см. комментарий у _constants).
		cmd.UpdateBuffer(_constantBuffers[index], 0, _constants + index);

		cmd.SetRenderTarget(target, null);
		cmd.SetViewport(size, size);
		cmd.SetPipelineState(material);
		cmd.CommitShaderResources(material);
		cmd.Draw(3);

		// Следующее звено читает этот таргет как SRV - переход нужен ДО его привязки в SRB-дроу.
		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(target, ResourceState.ShaderResource);
	}

	public void Release()
	{
		_lum64.Release();
		_lum8.Release();
		_lum1.Release();
		_adaptA.Release();
		_adaptB.Release();
		_initMaterial.Release();
		_reduce64Material.Release();
		_reduce8Material.Release();
		_adaptAtoB.Release();
		_adaptBtoA.Release();

		foreach (var buffer in _constantBuffers)
		{
			buffer?.Release();
		}

		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Render-graph pass that measures the HDR frame's average (log-)luminance and folds it into the
/// temporally adapted exposure value the <see cref="TonemapPass"/> then applies. Runs after
/// <see cref="ForwardPass"/> and <see cref="SsgiPass"/> - то есть меряет ГОТОВЫЙ линейный кадр, со
/// всем непрямым светом, - и перед тонемапом. Not added to the graph at all when eye adaptation is
/// disabled (см. <see cref="GraphicsPipelineSimple"/>).
/// </summary>
public sealed class EyeAdaptationPass : RenderGraphPass<EyeAdaptationPass.PassData>
{
	public override string Name => "Eye Adaptation Pass";

	private readonly EyeAdaptationPassResources _resources;
	private readonly IGpuTexture _hdrColorTarget;

	public struct PassData
	{
	}

	public EyeAdaptationPass(EyeAdaptationPassResources resources, IGpuTexture hdrColorTarget)
	{
		_resources = resources;
		_hdrColorTarget = hdrColorTarget;
	}

	/// <summary>Объявляет графу кадр, который пасс замеряет - см. <see cref="ForwardPass.Setup"/>.
	/// Цепочка редукции и пинг-понг адаптации живут внутри пасса и графу не видны.</summary>
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		builder.ReadTarget(builder.ImportTexture(_hdrColorTarget));
		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_hdrColorTarget, ResourceState.ShaderResource);

		_resources.WriteCommands(cmd);
	}
}
