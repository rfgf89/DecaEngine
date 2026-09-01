using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using Diligent;

namespace DecaEngine.Core;

/// <summary>Owns the GPU resources for stochastic screen-space reflections (см. SsrTracePS.hlsl /
/// SsrResolvePS.hlsl / SsrCompositePS.hlsl): трейс-таргет (rgb хита + confidence), пара
/// resolve/history для темпоральной аккумуляции и три фуллскрин-материала. Создаётся лениво
/// <see cref="GraphicsPipelineSimple"/> при первом включении <see cref="PipelineFeatures.Ssr"/> -
/// та же схема владения, что у <see cref="SsgiPassResources"/>.
///
/// Требует тонкого G-buffer-а отражений (<see cref="PipelineRenderTargets.NormalRoughnessTarget"/>,
/// пишется opaque-дроу под FEATURE_REFLECTION_GBUFFER) и буфера векторов движения (репроекция
/// истории).
///
/// <paramref name="rayTraced"/> собирает трейс-материал вариантом FEATURE_RT_REFLECTIONS (inline
/// RayQuery по TLAS сцены для лучей, промахнувшихся мимо экрана). Вызывающий, включивший это,
/// ОБЯЗАН привязать сцену через <see cref="SetRayScene"/> до первого кадра - тот же контракт, что
/// у <see cref="ModelLoadOptions.RtShadows"/>.</summary>
public sealed unsafe class SsrPassResources : IReleaseObject
{
	public IRenderTarget TraceTarget { get; }

	/// <summary>Hit-буфер трейса (RT1): экранный UV хита либо октаэдральное направление RT-хита +
	/// PDF луча + маска - вход ratio estimator-а резолва (см. SsrResolvePS).</summary>
	public IRenderTarget RayHitTarget { get; }
	public IRenderTarget ResolveTarget { get; }
	public IRenderTarget HistoryTarget { get; }

	/// <summary>Полукадровая размытая копия снимка сцены (SsrSceneBlurPS) - «конусный» уровень
	/// для шероховатых лучей (см. SsrSceneColor в SsrTracePS).</summary>
	public IRenderTarget SceneBlurTarget { get; }
	internal IMaterialObject BlurMaterial { get; }

	// Рендер-размер - пассу для вьюпорта блюра (полукадрового) при записи команд; граф
	// перезаписывает команды после ресайза (Invalidate), так что значения не протухают.
	internal uint Width { get; private set; }
	internal uint Height { get; private set; }

	internal IMaterialObject TraceMaterial { get; }
	internal IMaterialObject ResolveMaterial { get; }
	internal IMaterialObject CompositeMaterial { get; }

	/// <summary>Собраны ли материалы под RT-фолбэк (FEATURE_RT_REFLECTIONS) - смена требует
	/// пересоздания ресурсов, как техника AO (см. GraphicsPipelineSimple.EnsureResources).</summary>
	public bool RayTraced { get; }

	/// <summary>Режим текстурного альбедо RT-хитов, с которым собраны материалы: 0 - выкл,
	/// 1 - атлас плиток (FEATURE_RT_HIT_ATLAS), 2 - bindless-массив полноразмерных текстур
	/// (FEATURE_RT_HIT_BINDLESS). Смена, как и <see cref="RayTraced"/>, требует пересоздания.
	/// Включивший обязан привязать текстуры через <see cref="SetHitTextures"/>.</summary>
	public int HitTextureMode { get; }

	/// <summary>Размер массива Texture2D в bindless-режиме и потолок слоёв атласа - обязан
	/// совпадать с `_SceneHitTex[64]` в SsrTracePS.hlsl (и с ним сшит
	/// ProbeInstancedGeometry.MaxHitTextures на стороне редактора).</summary>
	public const int MaxHitTextures = 64;

	// Плейсхолдер слота _SceneHitAtlas (Texture2DArray обязан биндиться массивом же - см.
	// плейсхолдер shadow map-ов) - 1x1x2 белый, живёт вместе с ресурсами.
	private readonly IGpuTexture? _hitAtlasPlaceholder;

	// Филлер СВОБОДНЫХ и осиротевших слотов bindless-массива - 1x1 белый. Именно белый, а не
	// env-карта: слот с валидным textureIndex, оставшийся на филлере (набор ещё не привязан),
	// рендерится плоским цветом фактора, а env-карта по UV мешей давала «кашу» тёмного HDR.
	private readonly IGpuTexture? _hitTexPlaceholder;

	// Кбуфер со СВОЕЙ unmanaged-памятью, заливаемый UpdateBuffer из пасса, - как у
	// MotionVectorPassResources и по той же причине: счётчик кадров (фаза шума) меняется КАЖДЫЙ
	// кадр, а SetConstant переустанавливал бы переменную SRB под летящим кадром.
	private readonly IBufferHandle _constantBuffer;
	private readonly SsrConstantsData* _constants;

	// Плейсхолдер слотов probe-атласов, пока поля нет (объявленный слот обязан быть привязан -
	// Vulkan VUID-08114); env-карта подходит типом и живёт дольше SSR-ресурсов.
	private readonly IGpuTexture _environmentMap;

	/// <summary>Дефолты ручек SSR - ими же инициализируются поля EditorSettings.</summary>
	public const float DefaultIntensity = 1.0f;
	public const float DefaultMaxRoughness = 1.0f;
	public const int DefaultRaysPerPixel = 2;
	public const float DefaultThickness = 0.35f;
	public const float DefaultMaxDistance = 30.0f;
	public const float DefaultHistoryWeight = 0.9f;

	/// <summary>Отскоков RT-луча всего по умолчанию: 2 = историческое поведение (первичный +
	/// один зеркальный отскок с металлических хитов).</summary>
	public const int DefaultRtBounces = 2;

	/// <summary>Layout кбуфера "SsrConstants" в SsrCommon.hlsl - 64 байта, зеркалить только парой.</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct SsrConstantsData
	{
		public float FrameIndex;
		public float MaxRoughness;
		public float Thickness;
		public float MaxDistance;
		public float EnvYaw;
		public float HistoryWeight;
		public float DebugView;
		public float Intensity;
		public Vector4 SunDirWorld;
		public Vector4 SunColor;
		public float RaysPerPixel;

		/// <summary>ВСЕГО отскоков RT-луча (1..4): 1 - только первичный, 2+ - зеркальные
		/// продолжения с металлических хитов (зеркало ssrBounces в SsrCommon.hlsl).</summary>
		public float Bounces;

		/// <summary>0 - экранный марш, затем RT для промахов; 1 - сразу RT (марш пропускается).
		/// Зеркало ssrTraceMode в SsrCommon.hlsl; действует только в RT-варианте трейса.</summary>
		public float TraceMode;
		public float Pad2;
		public Vector4 ProbeOrigin;
		public Vector4 ProbeCell;
		public Vector4 ProbeCounts;

		/// <summary>viewProj ПРОШЛОГО кадра - репроекция виртуального образа отражения у зеркал
		/// (см. SsrResolvePS; защёлкивается раз в кадр из GraphicsPipelineSimple.Execute, как у
		/// MotionVectorPassResources.UpdateFromView). До первой защёлки - единичная.</summary>
		public Matrix4x4 PrevViewProj;
	}

	public SsrPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string colorTargetName,
		uint width, uint height, IGpuTexture depthTarget, IGpuTexture normalRoughTarget,
		IGpuTexture envFactorTarget, IGpuTexture sceneCopyTarget, IGpuTexture motionTarget,
		IGpuTexture environmentMap, bool rayTraced,
		TextureObjectFormat colorFormat = TextureObjectFormat.R16G16B16A16Float,
		int hitTextures = 0)
	{
		RayTraced = rayTraced;
		_environmentMap = environmentMap;
		Width = width;
		Height = height;

		// Режим текстур хитов живёт только внутри RT-варианта. Атласу нужен плейсхолдер-массив
		// ДО первого дроу (объявленный Texture2DArray-слот не примет обычную 2D-текстуру), и на
		// бэкенде без CreateTextureArray режим честно гаснет ещё до выбора кейвордов.
		hitTextures = rayTraced ? Math.Clamp(hitTextures, 0, 2) : 0;
		if (hitTextures == 1)
		{
			var white = new byte[] { 255, 255, 255, 255 };
			_hitAtlasPlaceholder = graphicsApi.CreateTextureArray(
				colorTargetName + " SSR HitAtlas Placeholder", 1, 1, new[] { white, white });
			if (_hitAtlasPlaceholder is null)
			{
				hitTextures = 0;
			}
		}
		else if (hitTextures == 2)
		{
			_hitTexPlaceholder = graphicsApi.CreateTexture2DWithMips(
				colorTargetName + " SSR HitTex Placeholder",
				new[] { new byte[] { 255, 255, 255, 255 } }, 1, 1);
		}

		HitTextureMode = hitTextures;

		TraceTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSR Trace",
			width = width,
			height = height,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		RayHitTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSR RayHit",
			width = width,
			height = height,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		ResolveTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSR Resolve",
			width = width,
			height = height,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		HistoryTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSR History",
			width = width,
			height = height,
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		SceneBlurTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSR Scene Blur",
			width = Math.Max(1u, width / 2),
			height = Math.Max(1u, height / 2),
			format = TextureObjectFormat.R16G16B16A16Float,
		});

		// У КАЖДОГО материала свой экземпляр VS - см. комментарий в SsaoPassResources (двойной
		// Release шареного шейдера при пересоздании окружения).
		//
		// RT-вариант: кейворд и на ВЕРШИННИК - DXC-паритет стадий, как у RT-теней моделей (см.
		// ModelLoader/PrecompileShaderVariants): пиксельный шейдер с RayQuery собирается DXC/SM6.5,
		// и PSO с FXC-вершинником не создаётся.
		// Кейворды и суффикс имени варианта трейса: режим текстур хитов меняет только пиксельный
		// шейдер, но имена держим раздельными по всем вариантам - живой диагностике проще.
		var traceKeywords = HitTextureMode switch
		{
			1 => new[] { "FEATURE_RT_REFLECTIONS", "FEATURE_RT_HIT_ATLAS" },
			2 => new[] { "FEATURE_RT_REFLECTIONS", "FEATURE_RT_HIT_BINDLESS" },
			_ => new[] { "FEATURE_RT_REFLECTIONS" },
		};
		var traceSuffix = HitTextureMode switch { 1 => " [RT+Atlas]", 2 => " [RT+Bindless]", _ => " [RT]" };

		var traceVs = rayTraced
			? graphicsApi.CreateShader("SSR Trace Fullscreen VS [RT]", "EditorAssets/shader", "SkyBackgroundVS.hlsl",
				ShaderObjectType.Vertex, "Main", new[] { "FEATURE_RT_REFLECTIONS" })
			: graphicsApi.CreateShader("SSR Trace Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var resolveVs = graphicsApi.CreateShader("SSR Resolve Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var blurVs = graphicsApi.CreateShader("SSR Blur Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var compositeVs = graphicsApi.CreateShader("SSR Composite Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);

		// Трейс пишет двумя MRT: цвет+conf и hit-буфер (см. SsrTracePS).
		var traceState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSR Trace PSO",
			RenderTargetFormats = [TextureObjectFormat.R16G16B16A16Float, TextureObjectFormat.R16G16B16A16Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var ssrTargetState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSR Resolve PSO",
			RenderTargetFormats = [TextureObjectFormat.R16G16B16A16Float],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		var compositeState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSR Composite PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		_constantBuffer = graphicsApi.CreateBuffer(new BufferInfo
		{
			name = "SsrConstants",
			dynamic = false,
			type = BufferHandleType.Constant,
			access = HandleAccess.Pixel,
			sizeInBytes = (uint)sizeof(SsrConstantsData),
		});

		_constants = (SsrConstantsData*)NativeMemory.AllocZeroed(1, (nuint)sizeof(SsrConstantsData));
		_constants->MaxRoughness = DefaultMaxRoughness;
		_constants->Thickness = DefaultThickness;
		_constants->MaxDistance = DefaultMaxDistance;
		_constants->HistoryWeight = DefaultHistoryWeight;
		_constants->Intensity = DefaultIntensity;
		_constants->SunDirWorld = new Vector4(0f, 1f, 0f, 0f);
		_constants->SunColor = new Vector4(0f, 0f, 0f, 0.55f);
		_constants->RaysPerPixel = DefaultRaysPerPixel;
		_constants->Bounces = DefaultRtBounces;
		_constants->PrevViewProj = Matrix4x4.Identity;

		var linearClamp = graphicsApi.CreateSampler(
			name: "SSR Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		var tracePs = rayTraced
			? graphicsApi.CreateShader("SSR Trace PS" + traceSuffix, "EditorAssets/shader", "SsrTracePS.hlsl",
				ShaderObjectType.Pixel, "Main", traceKeywords)
			: graphicsApi.CreateShader("SSR Trace PS", "EditorAssets/shader", "SsrTracePS.hlsl", ShaderObjectType.Pixel);
		TraceMaterial = graphicsApi.CreateMaterial("SSR Trace Material");
		TraceMaterial.SetShader(traceVs, tracePs);
		TraceMaterial.SetState(traceState);
		batchRenderer.BindViewConstants(TraceMaterial);
		// Light-кбуфер и пул punctual-светов - лампы в шейдинге RT-хитов (см. SsrTracePS);
		// экранный вариант этих объявлений не имеет и привязку игнорирует.
		batchRenderer.BindShadowResources(TraceMaterial);
		TraceMaterial.SetBuffer("SsrConstants", _constantBuffer, HandleAccess.Pixel);
		TraceMaterial.SetTexture("_DepthTex", depthTarget);
		TraceMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		TraceMaterial.SetTexture("_EnvFactorTex", envFactorTarget);
		TraceMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		TraceMaterial.SetTexture("_SceneBlurTex", SceneBlurTarget);
		TraceMaterial.SetImmutableSampler("_SceneBlurTex", linearClamp);
		// Билинейный сэмплер снимка сцены: цвет хита берётся по суб-пиксельному UV уточнённого
		// пересечения (см. SsrTracePS) - Load по целому пикселю мерцал на глянце.
		TraceMaterial.SetImmutableSampler("_SceneTex", linearClamp);
		// Env-карта - шейдинг RT-хитов (диффузная иррадианса по нормали хита); в экранном варианте
		// слот объявлен, но не читается - привязка нужна всё равно (Vulkan VUID-08114).
		TraceMaterial.SetTexture("_EnvMap", environmentMap);
		TraceMaterial.SetImmutableSampler("_EnvMap", linearClamp);
		// Слоты probe-атласов - плейсхолдером до привязки поля (см. SetProbeField).
		TraceMaterial.SetTexture("_ProbeSh0", environmentMap);
		TraceMaterial.SetTexture("_ProbeSh1", environmentMap);
		TraceMaterial.SetTexture("_ProbeSh2", environmentMap);
		TraceMaterial.SetTexture("_ProbeSh3", environmentMap);

		// Слоты текстур RT-хитов - плейсхолдерами до привязки набора (см. SetHitTextures):
		// объявленный слот обязан держать живой дескриптор ещё до первого дроу.
		if (HitTextureMode == 1)
		{
			var hitWrap = graphicsApi.CreateSampler(
				name: "SSR HitAtlas Sampler",
				filter: TextureFilter.Linear,
				address: TextureAddress.Wrap,
				comparisonFunction: CompFunction.Always,
				border: Vector4.Zero);
			TraceMaterial.SetImmutableSampler("_SceneHitAtlas", hitWrap);
			TraceMaterial.SetTexture("_SceneHitAtlas", _hitAtlasPlaceholder!);
		}
		else if (HitTextureMode == 2)
		{
			SetHitTextures(null, null);
		}

		// Полукадровый блюр снимка сцены - формат тот же RGBA16F, PSO резолва подходит.
		var blurPs = graphicsApi.CreateShader("SSR Scene Blur PS", "EditorAssets/shader", "SsrSceneBlurPS.hlsl", ShaderObjectType.Pixel);
		BlurMaterial = graphicsApi.CreateMaterial("SSR Scene Blur Material");
		BlurMaterial.SetShader(blurVs, blurPs);
		BlurMaterial.SetState(ssrTargetState);
		batchRenderer.BindViewConstants(BlurMaterial);
		BlurMaterial.SetBuffer("SsrConstants", _constantBuffer, HandleAccess.Pixel);
		BlurMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		BlurMaterial.SetImmutableSampler("_SceneTex", linearClamp);

		var resolvePs = graphicsApi.CreateShader("SSR Resolve PS", "EditorAssets/shader", "SsrResolvePS.hlsl", ShaderObjectType.Pixel);
		ResolveMaterial = graphicsApi.CreateMaterial("SSR Resolve Material");
		ResolveMaterial.SetShader(resolveVs, resolvePs);
		ResolveMaterial.SetState(ssrTargetState);
		batchRenderer.BindViewConstants(ResolveMaterial);
		ResolveMaterial.SetBuffer("SsrConstants", _constantBuffer, HandleAccess.Pixel);
		ResolveMaterial.SetTexture("_DepthTex", depthTarget);
		ResolveMaterial.SetTexture("_TraceTex", TraceTarget);
		ResolveMaterial.SetTexture("_RayHitTex", RayHitTarget);
		// Нормали и глубина - вес переиспользования лучей соседей (см. SsrResolvePS).
		ResolveMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		ResolveMaterial.SetTexture("_HistoryTex", HistoryTarget);
		ResolveMaterial.SetImmutableSampler("_HistoryTex", linearClamp);
		ResolveMaterial.SetTexture("_MotionTex", motionTarget);

		var compositePs = graphicsApi.CreateShader("SSR Composite PS", "EditorAssets/shader", "SsrCompositePS.hlsl", ShaderObjectType.Pixel);
		CompositeMaterial = graphicsApi.CreateMaterial("SSR Composite Material");
		CompositeMaterial.SetShader(compositeVs, compositePs);
		CompositeMaterial.SetState(compositeState);
		batchRenderer.BindViewConstants(CompositeMaterial);
		CompositeMaterial.SetBuffer("SsrConstants", _constantBuffer, HandleAccess.Pixel);
		CompositeMaterial.SetTexture("_DepthTex", depthTarget);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetTexture("_SsrTex", ResolveTarget);
		CompositeMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		CompositeMaterial.SetTexture("_EnvFactorTex", envFactorTarget);
		CompositeMaterial.SetTexture("_EnvMap", environmentMap);
		CompositeMaterial.SetImmutableSampler("_EnvMap", linearClamp);
	}

	/// <summary>Живые ручки SSR - окно Graphics (см. GraphicsSettingsWindow). Пишут в CPU-память
	/// кбуфера, доезжающую с каждым реплеем (см. WriteConstants).</summary>
	public void SetParams(float intensity, float maxRoughness, float thickness, float maxDistance,
		float historyWeight, int raysPerPixel, int debugView, int rtBounces = DefaultRtBounces,
		int traceMode = 0)
	{
		_constants->Intensity = intensity;
		_constants->MaxRoughness = Math.Clamp(maxRoughness, 0.05f, 1f);
		_constants->Thickness = Math.Max(0.01f, thickness);
		_constants->MaxDistance = Math.Max(0.5f, maxDistance);
		_constants->HistoryWeight = Math.Clamp(historyWeight, 0f, 0.97f);
		_constants->RaysPerPixel = Math.Clamp(raysPerPixel, 1, 4);
		_constants->DebugView = debugView;
		_constants->Bounces = Math.Clamp(rtBounces, 1, 4);

		// Режим «сразу RT» имеет смысл только когда RT-вариант собран: иначе экранный марш -
		// единственный источник отражений, и его выключение оставило бы пустой кадр.
		_constants->TraceMode = RayTraced ? Math.Clamp(traceMode, 0, 1) : 0;
	}

	/// <summary>Поворот env-карты (тот же PbrEnvYaw, что уходит материалам и небу) - композит
	/// обязан вычитать ровно тот env-цвет, который вложил форвард-пасс.</summary>
	public void SetEnvironmentYaw(float yawRadians)
	{
		_constants->EnvYaw = yawRadians;
	}

	/// <summary>Солнце для шейдинга RT-хитов (только вариант <see cref="RayTraced"/>):
	/// направление НА солнце, цвет с интенсивностью и ambient-уровень вне экрана.</summary>
	public void SetSun(Vector3 dirTowardSun, Vector3 color, float ambient, float sunTanHalfAngle = 0f)
	{
		// w - ТАНГЕНС половинного углового размера солнца: теневой луч RT-хитов рассеивается в
		// этом конусе (мягкий край тени, как у PCSS прямого вида; без него край был бинарным).
		_constants->SunDirWorld = new Vector4(dirTowardSun, Math.Max(sunTanHalfAngle, 0f));
		_constants->SunColor = new Vector4(color, ambient);
	}

	/// <summary>Сдвигает фазу шума - РОВНО ОДИН РАЗ за кадр, из
	/// <see cref="GraphicsPipelineSimple.Execute"/> (команды графа заморожены, счётчик в пассе
	/// застыл бы - см. MotionVectorPassResources.UpdateFromView, тот же приём).</summary>
	public void AdvanceFrame()
	{
		_constants->FrameIndex = (_constants->FrameIndex + 1f) % 4096f;
	}

	/// <summary>Защёлкивает viewProj кадра: В КБУФЕР уезжает матрица ПРОШЛОГО кадра (ей резолв
	/// репроецирует виртуальный образ отражения у зеркал), текущая запоминается на следующий.
	/// Раз в кадр из Execute, после отрисовки констант это безопасно - кбуфер перечитывается
	/// на каждом реплее (см. WriteConstants).</summary>
	public void UpdateFromView(in Matrix4x4 viewProj)
	{
		_constants->PrevViewProj = _hasPrevViewProj ? _prevViewProj : viewProj;
		_prevViewProj = viewProj;
		_hasPrevViewProj = true;
	}

	private Matrix4x4 _prevViewProj;
	private bool _hasPrevViewProj;

	/// <summary>Свет RT-хитов из probe-поля: SH-атласы базового объёма + его сетка (те же
	/// значения, что уходят материалам в ProbeGrid*, см. ProbeGiViewportShared.PushGrid).
	/// null-атласы = поля нет: слоты возвращаются на плейсхолдер, ветка в шейдере гаснет
	/// (origin.w = 0). ОБЯЗАТЕЛЕН перед освобождением атласов - SRB иначе держал бы
	/// уничтоженные текстуры.</summary>
	public void SetProbeField(IGpuTexture? sh0, IGpuTexture? sh1, IGpuTexture? sh2, IGpuTexture? sh3,
		Vector4 origin, Vector4 cell, Vector4 counts)
	{
		bool present = sh0 is not null && sh1 is not null && sh2 is not null && sh3 is not null;
		_constants->ProbeOrigin = present ? origin : Vector4.Zero;
		_constants->ProbeCell = present ? cell : new Vector4(1f, 1f, 1f, 0f);
		_constants->ProbeCounts = present ? counts : new Vector4(2f, 2f, 2f, 0f);

		TraceMaterial.SetTexture("_ProbeSh0", present ? sh0! : _environmentMap);
		TraceMaterial.SetTexture("_ProbeSh1", present ? sh1! : _environmentMap);
		TraceMaterial.SetTexture("_ProbeSh2", present ? sh2! : _environmentMap);
		TraceMaterial.SetTexture("_ProbeSh3", present ? sh3! : _environmentMap);
	}

	/// <summary>Текстурное альбедо RT-хитов (см. <see cref="HitTextureMode"/>): атлас-массив плиток
	/// (режим 1) либо список полноразмерных текстур для bindless-массива (режим 2, не больше
	/// <see cref="MaxHitTextures"/>; свободные и null-слоты добиваются env-картой - каждый слот
	/// массива обязан держать живой дескриптор). null-аргументы возвращают слоты на плейсхолдеры -
	/// ОБЯЗАТЕЛЬНО перед освобождением атласа/текстур, SRB иначе держал бы мёртвые view (та же
	/// дисциплина, что у <see cref="SetProbeField"/>).</summary>
	public void SetHitTextures(IGpuTexture? atlas, IReadOnlyList<IGpuTexture?>? textures)
	{
		if (!RayTraced || HitTextureMode == 0)
		{
			return;
		}

		if (HitTextureMode == 1)
		{
			TraceMaterial.SetTexture("_SceneHitAtlas", atlas ?? _hitAtlasPlaceholder!);
			return;
		}

		var slots = new IGpuTexture[MaxHitTextures];
		for (int i = 0; i < MaxHitTextures; i++)
		{
			slots[i] = (textures != null && i < textures.Count ? textures[i] : null)
				?? _hitTexPlaceholder ?? _environmentMap;
		}

		TraceMaterial.SetTextureSrvArray("_SceneHitTex", slots);
	}

	/// <summary>Привязывает TLAS и таблицы атрибутов сцены (см. ProbeSceneAccel) RT-варианту
	/// трейса. Обязательна при <see cref="RayTraced"/>; пересборка TLAS привязку не протухает
	/// (дескриптор указывает на сам объект), а вот ПЕРЕСОЗДАНИЕ объекта требует повторного вызова.</summary>
	public void SetRayScene(ITopLevelAS tlas, IBuffer meshTriangles, IBuffer instances)
	{
		if (!RayTraced)
		{
			return;
		}

		TraceMaterial.SetAccelStructure("_SceneTlas", tlas);
		TraceMaterial.SetStructuredBufferSrv("_SceneMeshTriangles", meshTriangles);
		TraceMaterial.SetStructuredBufferSrv("_SceneInstances", instances);
	}

	/// <summary>Заливает кбуфер в командный буфер - зовётся пассом перед дроу; команда перечитывает
	/// CPU-память при каждом реплее (см. FogPassResources - тот же приём).</summary>
	internal void WriteConstants(ICommandBuffer cmd) => cmd.UpdateBuffer(_constantBuffer, 0, _constants);

	/// <summary>Перепривязывает ресайзабельные таргеты ПОСЛЕ Resize конвейера и ресайзит свои
	/// (см. ModelPreviewViewport.ResizeTargets). История после ресайза - мусор, но резолв зажимает
	/// её в AABB текущего кадра, так что отдельного сброса не требуется.</summary>
	public void RebindTargets(IGpuTexture depthTarget, IGpuTexture normalRoughTarget,
		IGpuTexture envFactorTarget, IGpuTexture sceneCopyTarget, IGpuTexture motionTarget,
		uint width, uint height)
	{
		Width = width;
		Height = height;
		TraceTarget.Resize(new Vector2(width, height));
		RayHitTarget.Resize(new Vector2(width, height));
		SceneBlurTarget.Resize(new Vector2(Math.Max(1u, width / 2), Math.Max(1u, height / 2)));
		ResolveTarget.Resize(new Vector2(width, height));
		HistoryTarget.Resize(new Vector2(width, height));

		TraceMaterial.SetTexture("_DepthTex", depthTarget);
		TraceMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		TraceMaterial.SetTexture("_EnvFactorTex", envFactorTarget);
		TraceMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		TraceMaterial.SetTexture("_SceneBlurTex", SceneBlurTarget);
		BlurMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		// Env-карта не перепривязывается: ресайз вьюпорта её не пересоздаёт.

		ResolveMaterial.SetTexture("_DepthTex", depthTarget);
		ResolveMaterial.SetTexture("_TraceTex", TraceTarget);
		ResolveMaterial.SetTexture("_RayHitTex", RayHitTarget);
		ResolveMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		ResolveMaterial.SetTexture("_HistoryTex", HistoryTarget);
		ResolveMaterial.SetTexture("_MotionTex", motionTarget);

		CompositeMaterial.SetTexture("_DepthTex", depthTarget);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetTexture("_SsrTex", ResolveTarget);
		CompositeMaterial.SetTexture("_NormalRoughTex", normalRoughTarget);
		CompositeMaterial.SetTexture("_EnvFactorTex", envFactorTarget);
	}

	public void Release()
	{
		TraceTarget.Release();
		RayHitTarget.Release();
		SceneBlurTarget.Release();
		BlurMaterial.Release();
		ResolveTarget.Release();
		HistoryTarget.Release();
		TraceMaterial.Release();
		ResolveMaterial.Release();
		CompositeMaterial.Release();
		_hitAtlasPlaceholder?.Release();
		_hitTexPlaceholder?.Release();
		_constantBuffer.Release();
		NativeMemory.Free(_constants);
	}
}

/// <summary>
/// Render-graph pass: стохастические экранные отражения. Идёт ПОСЛЕ <see cref="SsgiPass"/> (в
/// отражения попадает кадр со всем непрямым светом) и после <see cref="MotionVectorPass"/>
/// (резолву нужна репроекция истории), но ДО замера яркости - отражения входят в экспозицию
/// наравне с остальным светом. Порядок работ:
///   1. пересъём scene-copy финальным кадром (источник цвета хитов и вход композита);
///   2. трейс (SsrTracePS) в TraceTarget;
///   3. темпоральный резолв (SsrResolvePS) в ResolveTarget + копия в HistoryTarget;
///   4. композит (SsrCompositePS) в HDR-таргет - замена env-спекуляра трейсом.
/// </summary>
public sealed class SsrPass : RenderGraphPass<SsrPass.PassData>
{
	public override string Name => "SSR Pass";

	private readonly SsrPassResources _resources;
	private readonly IGpuTexture _colorTarget;
	private readonly IGpuTexture _sceneCopy;
	private readonly IGpuTexture _renderDepth;
	private readonly IGpuTexture _normalRough;
	private readonly IGpuTexture _envFactor;
	private readonly IGpuTexture _motionTarget;
	private readonly Ref<Vector2> _viewPortRef;

	public struct PassData
	{
	}

	public SsrPass(SsrPassResources resources, IGpuTexture colorTarget, IGpuTexture sceneCopy,
		IGpuTexture renderDepth, IGpuTexture normalRough, IGpuTexture envFactor,
		IGpuTexture motionTarget, Ref<Vector2> viewPortRef)
	{
		_resources = resources;
		_colorTarget = colorTarget;
		_sceneCopy = sceneCopy;
		_renderDepth = renderDepth;
		_normalRough = normalRough;
		_envFactor = envFactor;
		_motionTarget = motionTarget;
		_viewPortRef = viewPortRef;
	}

	/// <summary>Объявляет графу таргеты пасса - см. <see cref="ForwardPass.Setup"/>.</summary>
	public override PassData Setup(IRenderGraphBuilder builder)
	{
		var color = builder.ImportTexture(_colorTarget);
		builder.ReadTarget(color);
		builder.WriteTarget(color);

		// Копия кадра переснимается ЭТИМ пассом (как у SSGI) - и вход, и выход.
		var sceneCopy = builder.ImportTexture(_sceneCopy);
		builder.WriteTarget(sceneCopy);
		builder.ReadTarget(sceneCopy);

		builder.ReadTarget(builder.ImportTexture(_renderDepth));
		builder.ReadTarget(builder.ImportTexture(_normalRough));
		builder.ReadTarget(builder.ImportTexture(_envFactor));
		builder.ReadTarget(builder.ImportTexture(_motionTarget));

		var sceneBlur = builder.ImportTexture(_resources.SceneBlurTarget);
		builder.WriteTarget(sceneBlur);
		builder.ReadTarget(sceneBlur);

		var trace = builder.ImportTexture(_resources.TraceTarget);
		builder.WriteTarget(trace);
		builder.ReadTarget(trace);

		var rayHit = builder.ImportTexture(_resources.RayHitTarget);
		builder.WriteTarget(rayHit);
		builder.ReadTarget(rayHit);

		var resolve = builder.ImportTexture(_resources.ResolveTarget);
		builder.WriteTarget(resolve);
		builder.ReadTarget(resolve);

		var history = builder.ImportTexture(_resources.HistoryTarget);
		builder.WriteTarget(history);
		builder.ReadTarget(history);

		return default;
	}

	public override void WriteCommands(in PassData value, in IRenderGraphContext context)
	{
		var cmd = context.cmd;

		_resources.WriteConstants(cmd);

		// Свежий снимок кадра: в отражения обязаны попасть AO/GI-композиты и transmissive-дроу,
		// а не только opaque-снимок рефракции (см. SsgiPass - тот же пересъём).
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_colorTarget, _sceneCopy);
		cmd.TransitionResource(_sceneCopy, ResourceState.ShaderResource);

		// Именно DepthRead, а не ShaderResource - см. SsaoPassResources.WriteInlineCommands
		// (лейаут DEPTH_STENCIL_READ_ONLY_OPTIMAL на Vulkan).
		cmd.TransitionResource(_renderDepth, ResourceState.DepthRead);
		cmd.TransitionResource(_normalRough, ResourceState.ShaderResource);
		cmd.TransitionResource(_envFactor, ResourceState.ShaderResource);
		cmd.TransitionResource(_motionTarget, ResourceState.ShaderResource);

		// История прошлого кадра - на чтение резолвом. В самом первом кадре содержимое таргета
		// не определено, но резолв зажимает историю в AABB текущего кадра, так что мусор не
		// протекает дальше одного кадра.
		cmd.TransitionResource(_resources.HistoryTarget, ResourceState.ShaderResource);

		// Полукадровый блюр снимка сцены - «конусный» уровень шероховатых лучей (см.
		// SsrSceneBlurPS); вьюпорт СВОЙ, полукадровый - значения перезаписываются вместе с
		// командами графа после ресайза.
		cmd.SetRenderTarget(_resources.SceneBlurTarget, null);
		cmd.SetViewport(Math.Max(1u, _resources.Width / 2), Math.Max(1u, _resources.Height / 2));
		cmd.SetPipelineState(_resources.BlurMaterial);
		cmd.CommitShaderResources(_resources.BlurMaterial);
		cmd.Draw(3);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_resources.SceneBlurTarget, ResourceState.ShaderResource);

		// Трейс пишет двумя MRT: цвет+conf и hit-буфер ratio estimator-а (см. SsrTracePS).
		cmd.SetRenderTargets([_resources.TraceTarget, _resources.RayHitTarget], null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.TraceMaterial);
		cmd.CommitShaderResources(_resources.TraceMaterial);
		cmd.Draw(3);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(_resources.TraceTarget, ResourceState.ShaderResource);
		cmd.TransitionResource(_resources.RayHitTarget, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_resources.ResolveTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.ResolveMaterial);
		cmd.CommitShaderResources(_resources.ResolveMaterial);
		cmd.Draw(3);

		// Резолв целиком - в историю следующего кадра.
		cmd.SetRenderTarget(null, null);
		cmd.CopyTexture(_resources.ResolveTarget, _resources.HistoryTarget);
		cmd.TransitionResource(_resources.ResolveTarget, ResourceState.ShaderResource);

		cmd.SetRenderTarget(_colorTarget, null);
		cmd.SetViewport(_viewPortRef);
		cmd.SetPipelineState(_resources.CompositeMaterial);
		cmd.CommitShaderResources(_resources.CompositeMaterial);
		cmd.Draw(3);
	}
}
