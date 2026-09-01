using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Core;

/// <summary>
/// Render-graph pass that clears/binds the back buffer and culls + draws every camera view.
/// Runs after <see cref="ShadowPass"/> so shadow maps are already populated.
/// </summary>
public sealed class ForwardPass : RenderGraphPass<ForwardPass.PassData>
{
	public override string Name => "Forward Pass";

	private readonly IBatchRenderer _batchRenderer;
	private readonly RenderCamerasData _renderScene;
	private readonly Ref<Vector2> _viewPortRef;
	private readonly IGpuTexture? _colorTarget;
	private readonly IGpuTexture? _depthTarget;
	private readonly IGpuTexture? _sceneCopy;
	private readonly SkyPassResources? _sky;
	private readonly SsaoPassResources? _ssao;
	private readonly IGpuTexture? _normalRoughTarget;
	private readonly IGpuTexture? _envFactorTarget;
	private readonly Vector4 _clearColor;

	/// <summary>Инлайн-оверлей поверх геометрии (дебаг-вид проб и т.п.): рисуется в конце каждого
	/// вида, в УЖЕ привязанный render target - оверлей честно тестируется депт-буфером сцены.
	///
	/// Геттер, а не значение: команды графа заморожены, но перезаписываются по InvalidateGraph -
	/// геттер даёт вызывающему включать/выключать оверлей без пересоздания пасса (см.
	/// GraphicsPipelineSimple.InlineOverlay). null-результат = оверлея нет.</summary>
	private readonly Func<Action<ICommandBuffer>?>? _overlay;

	public struct PassData
	{
	}

	public ForwardPass(IBatchRenderer batchRenderer, RenderCamerasData renderScene, Ref<Vector2> viewPortRef)
		: this(batchRenderer, renderScene, viewPortRef, null, null, new Vector4(0.1f, 0.1f, 0.1f, 1f))
	{
	}

	/// <summary>
	/// Overload used by off-screen consumers (see <see cref="DecaEngine.Editor.ModelPreviewViewport"/>)
	/// that need to draw into their own persistent color/depth targets instead of the swap chain -
	/// e.g. a separate, isolated render-graph instance rendering a .gltf/.glb preview for the
	/// Inspector, independent from the main Game View. When <paramref name="colorTarget"/> is null
	/// this behaves exactly like the swap-chain-writing constructor above.
	/// </summary>
	public ForwardPass(IBatchRenderer batchRenderer, RenderCamerasData renderScene, Ref<Vector2> viewPortRef,
		IGpuTexture? colorTarget, IGpuTexture? depthTarget, Vector4 clearColor, IGpuTexture? sceneCopy = null,
		SkyPassResources? sky = null,
		SsaoPassResources? ssao = null, Func<Action<ICommandBuffer>?>? overlay = null,
		IGpuTexture? normalRoughTarget = null, IGpuTexture? envFactorTarget = null)
	{
		_sky = sky;
		_overlay = overlay;

		// Тонкий G-buffer отражений (см. PipelineRenderTargets.NormalRoughnessTarget): геометрия
		// пишет его вторым/третьим MRT-слотом. Требует офскрин-таргета. Оба таргета обязаны прийти
		// вместе: PSO геометрии собраны под три слота (DiligentBatchRenderer.GeometryTargetFormats),
		// и на Vulkan биндить меньше нельзя.
		if (colorTarget is not null && normalRoughTarget is not null && envFactorTarget is not null)
		{
			_normalRoughTarget = normalRoughTarget;
			_envFactorTarget = envFactorTarget;
		}

		// AO рисуется инлайн МЕЖДУ opaque- и transmissive-дроу (см. SsaoPassResources.
		// WriteInlineCommands): стекло преломляет уже затенённый фон, но само экранным AO не
		// глушится - окклюзия рассеянного амбиента к преломлённому свету неприменима. Требует
		// refraction-пути (sceneCopy: композит читает снапшот), поэтому для swap-chain-пути
		// игнорируется вместе с ним.
		_ssao = colorTarget is not null && sceneCopy is not null ? ssao : null;

		_batchRenderer = batchRenderer;
		_renderScene = renderScene;
		_viewPortRef = viewPortRef;
		_colorTarget = colorTarget;
		_depthTarget = depthTarget;
		_clearColor = clearColor;

		// Refraction-пасс имеет смысл только с явным офскрин-таргетом: back buffer свопчейна
		// копировать нечем/незачем в этом движке, так что для swap-chain-пути sceneCopy игнорируется.
		_sceneCopy = colorTarget is not null ? sceneCopy : null;
	}

	/// <summary>Объявляет графу таргеты, которых пасс касается (см.
	/// <see cref="IRenderGraphBuilder.ImportTexture"/>): создаёт и владеет ими конвейер, но зная, кто
	/// что читает и пишет, граф строит настоящие рёбра зависимостей вместо порядка добавления, а окно
	/// отладки показывает времена жизни и вес ресурсов кадра.
	///
	/// Собственные таргеты AO/GTAO сюда не попадают намеренно: они целиком внутри ЭТОГО пасса
	/// (композит рисуется инлайн, см. SsaoPassResources.WriteInlineCommands), и графу от их
	/// объявления ни зависимостей, ни времён жизни не прибавится.</summary>
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		if (_colorTarget is not null)
		{
			builder.WriteTarget(builder.ImportTexture(_colorTarget));
		}

		if (_depthTarget is not null)
		{
			builder.WriteTarget(builder.ImportTexture(_depthTarget));
		}

		if (_sceneCopy is not null)
		{
			// И пишется (снимок opaque-сцены), и читается - transmissive-материалы сэмплируют его
			// как "_SceneColor".
			var sceneCopy = builder.ImportTexture(_sceneCopy);
			builder.WriteTarget(sceneCopy);
			builder.ReadTarget(sceneCopy);
		}

		if (_normalRoughTarget is not null)
		{
			builder.WriteTarget(builder.ImportTexture(_normalRoughTarget));
			builder.WriteTarget(builder.ImportTexture(_envFactorTarget!));
		}

		return default;
	}

	public override unsafe void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_batchRenderer.CheckAndReallocateBuffers();

		var punctualViews = _renderScene;

		// Тени punctual-светов - ДО привязки цветового таргета: каждый слайс биндит свой depth-слайс
		// массива теней. Петля фиксированная по ВСЕМ слайсам (команды замороженные): мёртвый слайс
		// несёт drawCount = 0 и рисует пусто (см. PunctualShadowScheduler).
		if (punctualViews.IsCreated)
		{
			_batchRenderer.SetupPunctualShadowMatrices(cmd, punctualViews.punctualShadowMatrices);

			for (int s = 0; s < punctualViews.punctualShadowCullData.Capacity; s++)
			{
				_batchRenderer.ClearIndirectDrawBuffers(cmd);
				_batchRenderer.SetupCullData(cmd, ref punctualViews.punctualShadowCullData.GetRef(s, false));
				_batchRenderer.SetupLightData(cmd, ref punctualViews.punctualShadowLightData.GetRef(s, false));

				var sliceCull = _batchRenderer.ExecuteComputeCulling(cmd);
				_batchRenderer.ExecuteDrawPunctualShadow(cmd, sliceCull, s);
			}

			// Всегда, даже без единого нарисованного слайса: текстура объявлена в PS безусловно,
			// лейаут обязан быть валиден.
			_batchRenderer.TransitionPunctualShadowsForRead(cmd);
		}

		var renderColor = _colorTarget;
		var renderDepth = _depthTarget;

		if (renderColor is not null)
		{
			cmd.SetRenderTarget(renderColor, renderDepth);
			cmd.ClearRenderTarget(renderColor, _clearColor);
			if (renderDepth is not null)
			{
				cmd.ClearDepthStencil(renderDepth, ClearDepthStencilFlags.Depth, 0.0f, 0);
			}

			// G-buffer отражений чистится нулями: нуль в w EnvFactor - «lit-путь не прошёл», такие
			// пиксели SSR-композит не трогает (небо, режимы превью без PBR).
			if (_normalRoughTarget is not null)
			{
				cmd.ClearRenderTarget(_normalRoughTarget, Vector4.Zero);
				cmd.ClearRenderTarget(_envFactorTarget!, Vector4.Zero);
			}
		}
		else
		{
			cmd.SetBackBufferTarget(context.Api);
			cmd.ClearBackBufferTarget(context.Api, _clearColor);
		}

		cmd.SetViewport(_viewPortRef);

		var views = _renderScene;
		if (views.IsCreated)
		{
			// Пул punctual-светов кадра - один на все камеры пасса (каждая берёт свой сегмент по
			// LightData.ClusterParams), заливается до пер-камерных диспатчей кластеризации.
			_batchRenderer.SetupPunctualLights(cmd, views.punctualLights);

			for (int i = 0; i < views.viewData.Capacity; i++)
			{
				// Свежие indirect-команды/счётчики батчей КАЖДОЙ камере: каллинг аллоцирует слоты
				// инстансов атомарным инкрементом и без сброса копил бы их между камерами - та же
				// аккумуляция, что мигала каскадами в ShadowPass (см. комментарий там).
				_batchRenderer.ClearIndirectDrawBuffers(cmd);

				_batchRenderer.SetupViewData(cmd, ref views.viewData.GetRef(i, false));
				_batchRenderer.SetupCullData(cmd, ref views.cullData.GetRef(i, false));
				_batchRenderer.SetupLightData(cmd, ref views.lightData.GetRef(i, false));

				// Раскладка сегмента светов ЭТОЙ камеры по фроксел-кластерам - читает свежезалитый
				// Light-кбуфер (ClusterParams), поэтому строго после SetupLightData.
				_batchRenderer.ExecuteLightClustering(cmd);

				// Фон-энвайронмент (см. SkyPassResources.Draw): в уже забинженный этим циклом render
				// target, ДО геометрии.
				_sky?.Draw(cmd);

				var cullResult = _batchRenderer.ExecuteComputeCulling(cmd);

				// PSO геометрии при G-buffer-е отражений собраны под три MRT-слота - все батч-дроу
				// идут с привязанной тройкой, а небо/AO/оверлеи (одиночные PSO) - с одиночным
				// таргетом; перепривязки ниже расставлены ровно по этим границам.
				if (_sceneCopy is null)
				{
					if (_normalRoughTarget is not null)
					{
						cmd.SetRenderTargets([renderColor!, _normalRoughTarget, _envFactorTarget!], renderDepth);
					}

					_batchRenderer.ExecuteDrawBatching(cmd, cullResult);

					if (_normalRoughTarget is not null)
					{
						cmd.SetRenderTarget(renderColor, renderDepth);
					}

					_overlay?.Invoke()?.Invoke(cmd);
					continue;
				}

				// Refraction-пасс: сначала opaque-материалы, затем снимок цветового таргета в
				// _sceneCopy, и только после - transmissive-материалы (см.
				// IBatchRenderer.SetMaterialTransparent), сэмплирующие этот снимок как "сцену за
				// стеклом" (_SceneColor в UnlitInstancedPS.hlsl). Копировать привязанный RT нельзя -
				// таргет отвязывается на время копии и привязывается обратно.
				//
				// Переход снимка в ShaderResource ДО opaque-дроу обязателен: _SceneColor статически
				// привязан в SRB всех материалов (в т.ч. opaque), и в первом кадре текстура ещё в
				// UNDEFINED - валидация Vulkan падает на самом первом дроу, не дойдя до копии.
				cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

				if (_normalRoughTarget is not null)
				{
					cmd.SetRenderTargets([renderColor!, _normalRoughTarget, _envFactorTarget!], renderDepth);
				}

				_batchRenderer.ExecuteDrawBatching(cmd, cullResult, BatchDrawFilter.OpaqueOnly);

				cmd.SetRenderTarget(null, null);
				cmd.CopyTexture(_colorTarget, _sceneCopy);
				cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

				// Экранное AO - здесь, а не пост-пассом поверх готового кадра: оценка по opaque-депту,
				// композит в render-таргет (читает снапшот выше как _SceneTex). Сам снапшот при этом
				// НЕ ПЕРЕСНИМАЕТСЯ, то есть transmissive-материалы преломляют кадр ДО композита AO.
				//
				// Раньше он переснимался - ради того, чтобы стекло преломляло уже затенённую сцену.
				// Замерено, что это давало: на прозрачных шторах Sponza (KHR_materials_transmission,
				// см. UnlitInstancedPS.hlsl, transmitted = lerp(backdrop, scene, scene.a)) сквозь
				// ткань проступало AO-поле стены и арки за ней - тёмные пятна формой по арке, тем
				// заметнее, чем контрастнее техника AO: с выключенным AO узор шторы ровный, с SSAO
				// лёгкая грязь, с GTAO уже сплошные пятна.
				//
				// Пятна тут - не «слишком сильный AO», а двойной учёт: экранное AO аппроксимирует
				// заслонённость рассеянного амбиента У ПОВЕРХНОСТИ, и переносить её на свет,
				// прошедший сквозь материал насквозь, оснований нет. Стекло теперь преломляет
				// незатенённый фон - это осознанный размен: контактная тень за стеклом сквозь него
				// не видна.
				if (_ssao is not null)
				{
					_ssao.WriteInlineCommands(cmd, renderColor!, renderDepth!, _viewPortRef);
				}

				if (_normalRoughTarget is not null)
				{
					cmd.SetRenderTargets([renderColor!, _normalRoughTarget, _envFactorTarget!], renderDepth);
				}
				else
				{
					cmd.SetRenderTarget(renderColor, renderDepth);
				}

				cmd.SetViewport(_viewPortRef);
				_batchRenderer.ExecuteDrawBatching(cmd, cullResult, BatchDrawFilter.TransparentOnly);

				// Оверлей - одиночным PSO, см. комментарий у первой перепривязки выше.
				if (_normalRoughTarget is not null)
				{
					cmd.SetRenderTarget(renderColor, renderDepth);
				}

				_overlay?.Invoke()?.Invoke(cmd);
			}
		}
	}
}
