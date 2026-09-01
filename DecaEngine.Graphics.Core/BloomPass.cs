using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Owns the GPU resources for bloom: the progressive down/up mip chain plus the materials
/// that drive it (см. BloomCommon.hlsl). Created once by <see cref="GraphicsPipelineSimple"/> when
/// bloom is enabled - тот же паттерн владения, что у <see cref="SsgiPassResources"/>.
///
/// Цепочка, а не одно широкое размытие: большое гауссово ядро на полном разрешении и стоит дорого,
/// и мерцает на субпиксельных бликах. Прогрессивная цепочка (Jimenez, SIGGRAPH 2014) даёт тот же
/// широкий ореол за долю цены и устойчива к движению камеры.</summary>
public sealed unsafe class BloomPassResources : IReleaseObject
{
	/// <summary>Сколько звеньев цепочки. Пять - это ореол шириной примерно в треть экрана (нижний
	/// уровень идёт в 1/64 разрешения): дальше расширять нечего, а каждое лишнее звено - ещё два
	/// полноэкранных прохода по мелкому таргету.</summary>
	public const int MaxLevels = 5;

	/// <summary>Ниже этого размера звено не заводится: таргет в считанные тексели не несёт
	/// информации, зато каждый стоит своего дроу и своего перехода состояний.</summary>
	private const uint MinLevelSize = 8;

	// Цепочка вниз: [0] - половина экрана, дальше каждый вдвое меньше.
	private readonly IRenderTarget[] _down = new IRenderTarget[MaxLevels];

	// Цепочка вверх. Верхний уровень цепочки вниз накоплению не подлежит (с него всё начинается),
	// поэтому здесь на одно звено меньше и индексация совпадает с _down.
	private readonly IRenderTarget[] _up = new IRenderTarget[MaxLevels - 1];

	private readonly IMaterialObject _prefilter;
	private readonly IMaterialObject[] _downMaterials = new IMaterialObject[MaxLevels - 1];
	private readonly IMaterialObject[] _upMaterials = new IMaterialObject[MaxLevels - 1];
	private readonly IMaterialObject _composite;

	// Кбуферы со СВОЕЙ unmanaged-памятью, заливаемые командой UpdateBuffer из пасса - ровно как в
	// EyeAdaptationPassResources и по той же причине: размеры звеньев меняются при каждом ресайзе
	// вьюпорта, а IMaterialObject.SetConstant попутно переустанавливает переменную в SRB, и
	// обновление дескриптор-сета при кадре в полёте роняет валидацию Vulkan.
	private readonly IBufferHandle[] _constantBuffers;
	private readonly BloomConstantsData* _constants;

	private readonly int _materialCount;
	private int _levels;

	/// <summary>Сколько звеньев реально живо при текущем разрешении (см. <see cref="MinLevelSize"/>).</summary>
	public int Levels => _levels;

	private const int PrefilterIndex = 0;
	private int DownIndex(int i) => 1 + i;
	private int UpIndex(int i) => 1 + (MaxLevels - 1) + i;
	private int CompositeIndex => 1 + 2 * (MaxLevels - 1);

	/// <param name="adaptationTarget">1x1-таргет авто-экспозиции. null в LDR-конвейере - тогда порог
	/// трактуется как абсолютный, а в слот _AdaptTex всё равно привязывается плейсхолдер: слот
	/// объявлен в шейдере безусловно, и пустой дескриптор роняет валидацию Vulkan (VUID-08114).</param>
	public BloomPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string colorTargetName,
		uint width, uint height, IGpuTexture sceneCopyTarget, TextureObjectFormat colorFormat,
		IGpuTexture? adaptationTarget)
	{
		_materialCount = CompositeIndex + 1;
		_constantBuffers = new IBufferHandle[_materialCount];

		for (int i = 0; i < MaxLevels; i++)
		{
			var (w, h) = LevelSize(width, height, i);
			_down[i] = graphicsApi.CreateRenderTarget(new TextureInfo
			{
				name = $"{colorTargetName} Bloom Down {i}",
				width = w,
				height = h,
				format = colorFormat,
			});

			if (i < MaxLevels - 1)
			{
				_up[i] = graphicsApi.CreateRenderTarget(new TextureInfo
				{
					name = $"{colorTargetName} Bloom Up {i}",
					width = w,
					height = h,
					format = colorFormat,
				});
			}
		}

		// Без депта и без MSAA: вся цепочка живёт в 1x по уже разрешённому кадру.
		var state = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "Bloom PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		// Linear + Clamp обязательны: вся цепочка держится на билинейной выборке между текселями
		// (см. BloomDownPS), а Wrap затянул бы яркий край экрана на противоположный.
		var sampler = graphicsApi.CreateSampler(
			name: "Bloom Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		_constants = (BloomConstantsData*)NativeMemory.AllocZeroed(
			(nuint)_materialCount, (nuint)sizeof(BloomConstantsData));

		var placeholder = adaptationTarget ?? sceneCopyTarget;

		_prefilter = CreateMaterial(graphicsApi, batchRenderer, state, sampler, "Bloom Prefilter",
			"BloomPrefilterPS.hlsl", PrefilterIndex, sceneCopyTarget, null, placeholder);

		for (int i = 0; i < MaxLevels - 1; i++)
		{
			_downMaterials[i] = CreateMaterial(graphicsApi, batchRenderer, state, sampler,
				$"Bloom Down {i}", "BloomDownPS.hlsl", DownIndex(i), _down[i], null, placeholder);

			// Апсэмпл звена i читает НАКОПЛЕННЫЙ уровень снизу и «свой» уровень цепочки вниз.
			// Снизу для самого нижнего звена - это _down[MaxLevels-1] (накапливать там ещё нечего),
			// для остальных - результат предыдущего апсэмпла.
			var lower = i == MaxLevels - 2 ? _down[MaxLevels - 1] : (IGpuTexture)_up[i + 1];
			_upMaterials[i] = CreateMaterial(graphicsApi, batchRenderer, state, sampler,
				$"Bloom Up {i}", "BloomUpPS.hlsl", UpIndex(i), _down[i], lower, placeholder);
		}

		_composite = CreateMaterial(graphicsApi, batchRenderer, state, sampler, "Bloom Composite",
			"BloomCompositePS.hlsl", CompositeIndex, sceneCopyTarget, _up[0], placeholder);

		// Дефолты - до первого пуша из окна Graphics: пасс рисует с первого кадра, и кбуферы иначе
		// остались бы с мусором (та же причина, что в SsaoPassResources).
		SetParams(DefaultThreshold, DefaultKnee, DefaultRadius, DefaultIntensity);
		SetExposure(adaptationTarget is not null, 0.18f);
		Resize(width, height);
	}

	/// <summary>Дефолты ручек блума - ими же инициализируются кбуферы и они же служат стартовыми
	/// значениями в <see cref="DecaEngine.Editor.EditorSettings"/>.
	///
	/// Порог задан в ОТОБРАЖАЕМЫХ единицах (см. BloomCommon.BloomExposure), где 1.0 - ровно то, что
	/// дисплей уже не может показать ярче. Физически правильным дефолтом была бы единица, но она
	/// замеренно даёт НОЛЬ на обычном интерьере: на Sponza с порогом 1.0 средняя яркость кадра
	/// не сдвинулась вообще (36.3 с блумом и без), при 0.8 - тоже, и только с 0.6 эффект становится
	/// видным. Фича, которая по умолчанию не делает ничего, читается как сломанная, поэтому дефолт
	/// чуть ниже белого. Если и на нём ореола нет - в сцене просто нет ярких мест, и это вопрос
	/// к свету, а не к блуму.</summary>
	public const float DefaultThreshold = 0.6f;
	public const float DefaultKnee = 0.5f;
	public const float DefaultRadius = 1.0f;
	public const float DefaultIntensity = 0.8f;

	/// <summary>Layout кбуфера "BloomConstants" в BloomCommon.hlsl - четыре float4 (64 байта).</summary>
	private struct BloomConstantsData
	{
		/// <summary>xy - размер таргета, zw - 1/xy.</summary>
		public Vector4 Target;

		/// <summary>xy - размер источника, zw - 1/xy. У композита x переиспользован под ЧИСЛО
		/// ЖИВЫХ ЗВЕНЬЕВ - на него делится интенсивность (см. BloomCompositePS).</summary>
		public Vector4 Source;

		/// <summary>x - порог, y - колено, z - радиус тента, w - интенсивность.</summary>
		public Vector4 Params;

		/// <summary>x - порог относительно экспозиции, y - key value.</summary>
		public Vector4 Exposure;
	}

	private static (uint W, uint H) LevelSize(uint width, uint height, int level)
	{
		uint div = (uint)(2 << level);
		return (Math.Max(width / div, 1u), Math.Max(height / div, 1u));
	}

	private IMaterialObject CreateMaterial(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer,
		IStateObject state, ISamplerObject sampler, string name, string pixelShaderFile, int index,
		IGpuTexture source, IGpuTexture? lower, IGpuTexture adaptation)
	{
		// У КАЖДОГО материала свой экземпляр VS - см. комментарий в SsaoPassResources (шареный
		// шейдер освобождался бы дважды при пересоздании окружения).
		var vs = graphicsApi.CreateShader(name + " VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader(name + " PS", "EditorAssets/shader", pixelShaderFile,
			ShaderObjectType.Pixel);

		var material = graphicsApi.CreateMaterial(name + " Material");
		material.SetShader(vs, ps);
		material.SetState(state);
		batchRenderer.BindViewConstants(material);

		material.SetTexture("_SourceTex", source);
		material.SetImmutableSampler("_SourceTex", sampler);

		// Слоты объявлены в шейдере безусловно - у звеньев, которым нижний уровень не нужен,
		// привязывается сам источник (пустой дескриптор роняет валидацию Vulkan, VUID-08114).
		material.SetTexture("_LowerTex", lower ?? source);
		material.SetImmutableSampler("_LowerTex", sampler);
		material.SetTexture("_AdaptTex", adaptation);

		// dynamic = false: динамические буферы Diligent обновляет через Map, а нам нужен именно
		// UpdateBuffer из командного буфера (USAGE_DEFAULT).
		var buffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "BloomConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(BloomConstantsData),
		});

		_constantBuffers[index] = buffer;
		material.SetBuffer("BloomConstants", buffer, HandleAccess.Pixel);
		return material;
	}

	/// <summary>Живые ручки окна Graphics: порог яркости (в отображаемых единицах), ширина мягкого
	/// колена, радиус тента апсэмпла и общая интенсивность ореола.</summary>
	public void SetParams(float threshold, float knee, float radius, float intensity)
	{
		var p = new Vector4(MathF.Max(threshold, 0f), MathF.Max(knee, 1e-4f),
			MathF.Max(radius, 0f), MathF.Max(intensity, 0f));

		for (int i = 0; i < _materialCount; i++)
		{
			_constants[i].Params = p;
		}
	}

	/// <summary>Привязка порога к авто-экспозиции - включена ровно тогда, когда жив HDR-конвейер.
	/// <paramref name="key"/> обязан совпадать с тем, что уходит в
	/// <see cref="EyeAdaptationPassResources.SetParams"/> и <see cref="TonemapPassResources.SetParams"/>.</summary>
	public void SetExposure(bool exposureRelative, float key)
	{
		var e = new Vector4(exposureRelative ? 1f : 0f, MathF.Max(key, 1e-4f), 0f, 0f);
		for (int i = 0; i < _materialCount; i++)
		{
			_constants[i].Exposure = e;
		}
	}

	/// <summary>Переключает ТОЛЬКО режим, сохраняя key - см.
	/// <see cref="FogPassResources.SetExposureRelative"/>.</summary>
	public void SetExposureRelative(bool exposureRelative)
	{
		for (int i = 0; i < _materialCount; i++)
		{
			_constants[i].Exposure.X = exposureRelative ? 1f : 0f;
		}
	}

	/// <summary>Пересчитывает размеры звеньев под новое разрешение кадра и ресайзит их таргеты.
	/// Зовётся из <see cref="RebindTargets"/> и при ресайзе вьюпорта (см.
	/// ModelPreviewViewport.ResizeTargets).</summary>
	public void Resize(uint width, uint height)
	{
		// Число ЖИВЫХ звеньев - от разрешения: на маленьком вьюпорте нижние уровни выродились бы в
		// считанные тексели, где 13-тапное ядро читает один и тот же тексель тринадцать раз.
		_levels = 0;
		for (int i = 0; i < MaxLevels; i++)
		{
			var (w, h) = LevelSize(width, height, i);
			if (i > 0 && (w < MinLevelSize || h < MinLevelSize))
			{
				break;
			}

			_levels = i + 1;
		}

		for (int i = 0; i < MaxLevels; i++)
		{
			var (w, h) = LevelSize(width, height, i);
			var size = new Vector2(w, h);
			_down[i].Resize(size);
			if (i < MaxLevels - 1)
			{
				_up[i].Resize(size);
			}
		}

		// Prefilter: источник - полный кадр, таргет - половина.
		var half = LevelSize(width, height, 0);
		SetSizes(PrefilterIndex, half.W, half.H, width, height);

		for (int i = 0; i < MaxLevels - 1; i++)
		{
			var src = LevelSize(width, height, i);
			var dst = LevelSize(width, height, i + 1);

			// Вниз: из уровня i в уровень i+1.
			SetSizes(DownIndex(i), dst.W, dst.H, src.W, src.H);

			// Вверх: таргет - уровень i, источник нижнего звена - уровень i+1.
			SetSizes(UpIndex(i), src.W, src.H, dst.W, dst.H);
		}

		SetSizes(CompositeIndex, width, height, width, height);

		// У композита x источника переиспользован под число живых звеньев - на него делится
		// интенсивность, иначе она значила бы разное при разной длине цепочки.
		_constants[CompositeIndex].Source.X = Math.Max(_levels, 1);
	}

	private void SetSizes(int index, uint targetW, uint targetH, uint sourceW, uint sourceH)
	{
		_constants[index].Target = new Vector4(targetW, targetH, 1f / targetW, 1f / targetH);
		_constants[index].Source = new Vector4(sourceW, sourceH, 1f / sourceW, 1f / sourceH);
	}

	/// <summary>Перепривязывает копию кадра ПОСЛЕ её Resize - Resize пересоздаёт нативную текстуру,
	/// и SRB иначе держал бы уничтоженную. Таргеты самой цепочки принадлежат этому объекту, их
	/// ресайзит <see cref="Resize"/>, но привязки на них тоже надо обновить.</summary>
	public void RebindTargets(IGpuTexture sceneCopyTarget, uint width, uint height)
	{
		Resize(width, height);

		_prefilter.SetTexture("_SourceTex", sceneCopyTarget);
		_composite.SetTexture("_SourceTex", sceneCopyTarget);
		_composite.SetTexture("_LowerTex", _up[0]);

		for (int i = 0; i < MaxLevels - 1; i++)
		{
			_downMaterials[i].SetTexture("_SourceTex", _down[i]);
			_upMaterials[i].SetTexture("_SourceTex", _down[i]);
			_upMaterials[i].SetTexture("_LowerTex",
				i == MaxLevels - 2 ? _down[MaxLevels - 1] : (IGpuTexture)_up[i + 1]);
		}
	}

	/// <summary>Цепочка целиком - см. <see cref="BloomPass"/>. Вызывающий обязан подготовить копию
	/// кадра и отвязать таргеты.</summary>
	internal void WriteCommands(ICommandBuffer cmd, IGpuTexture colorTarget, Ref<Vector2> viewPortRef)
	{
		Draw(cmd, PrefilterIndex, _prefilter, _down[0]);

		for (int i = 0; i < _levels - 1; i++)
		{
			Draw(cmd, DownIndex(i), _downMaterials[i], _down[i + 1]);
		}

		// Вверх - от предпоследнего живого звена к нулевому. Самое нижнее звено ни во что не
		// апсэмплится: оно и есть начало накопления.
		for (int i = _levels - 2; i >= 0; i--)
		{
			Draw(cmd, UpIndex(i), _upMaterials[i], _up[i]);
		}

		// Композит - в САМ кадр, поэтому таргет и вьюпорт задаёт вызывающий (у него полное
		// разрешение и свой Ref, а не размер звена).
		cmd.UpdateBuffer(_constantBuffers[CompositeIndex], 0, _constants + CompositeIndex);
		cmd.SetRenderTarget(colorTarget, null);
		cmd.SetViewport(viewPortRef);
		cmd.SetPipelineState(_composite);
		cmd.CommitShaderResources(_composite);
		cmd.Draw(3);
	}

	private void Draw(ICommandBuffer cmd, int index, IMaterialObject material, IRenderTarget target)
	{
		// Заливка кбуфера командой, а не SetConstant: команда перечитывает CPU-память при КАЖДОМ
		// реплее заморожённого буфера, так что новые размеры звеньев доезжают после ресайза без
		// пересборки графа (см. EyeAdaptationPassResources - тот же приём).
		cmd.UpdateBuffer(_constantBuffers[index], 0, _constants + index);

		var desc = target.Size;
		cmd.SetRenderTarget(target, null);
		cmd.SetViewport((uint)desc.X, (uint)desc.Y);
		cmd.SetPipelineState(material);
		cmd.CommitShaderResources(material);
		cmd.Draw(3);

		// Следующее звено читает этот таргет как SRV - переход нужен ДО его привязки в дроу.
		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(target, ResourceState.ShaderResource);
	}

	public void Release()
	{
		_prefilter.Release();
		_composite.Release();
		foreach (var m in _downMaterials)
		{
			m?.Release();
		}

		foreach (var m in _upMaterials)
		{
			m?.Release();
		}

		foreach (var t in _down)
		{
			t?.Release();
		}

		foreach (var t in _up)
		{
			t?.Release();
		}

		foreach (var b in _constantBuffers)
		{
			b?.Release();
		}

		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Render-graph pass that adds bloom (optical glare) to the finished linear frame.
///
/// Место в кадре - ПОСЛЕ тумана и ПОСЛЕ замера яркости, но ДО тонемапа:
/// - после тумана, потому что рассеяние в оптике происходит уже с тем светом, который дошёл до
///   объектива, включая свечение самой дымки;
/// - после замера - по той же причине, что и туман: порог блума привязан к адаптации, и стой пасс
///   до замера, ореол раздувал бы ровно ту величину, на которую сам же и опирается;
/// - до тонемапа, потому что складывать свет можно только в линейном пространстве.
/// </summary>
public sealed class BloomPass : RenderGraphPass<BloomPass.PassData>
{
	public override string Name => "Bloom Pass";

	private readonly BloomPassResources _resources;
	private readonly IGpuTexture _colorTarget;
	private readonly IGpuTexture _sceneCopy;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public BloomPass(BloomPassResources resources, IGpuTexture colorTarget, IGpuTexture sceneCopy,
		Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_colorTarget = colorTarget;
		_sceneCopy = sceneCopy;
		_viewPortRef = viewPortRef;
	}

	/// <summary>Объявляет графу таргеты пасса - см. <see cref="ForwardPass.Setup"/>. Пирамида блума
	/// внутри пасса и графу не видна (как и мип-цепочка GTAO) - объявлять её незачем.</summary>
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		var color = builder.ImportTexture(_colorTarget);
		builder.ReadTarget(color);
		builder.WriteTarget(color);

		var sceneCopy = builder.ImportTexture(_sceneCopy);
		builder.WriteTarget(sceneCopy);
		builder.ReadTarget(sceneCopy);

		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		// Копия кадра снимается ЗДЕСЬ, а не переиспользуется от тумана: тот успел дописать в кадр
		// дымку, и старый снимок вернул бы кадр без неё - блум светился бы от несуществующего света.
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_colorTarget, _sceneCopy);
		cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

		_resources.WriteCommands(cmd, _colorTarget, _viewPortRef);
	}
}
