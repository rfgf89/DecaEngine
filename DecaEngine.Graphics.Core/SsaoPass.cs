using System.Numerics;
using DecaEngine.Graphics;
using DecaEngine.Core;

namespace DecaEngine.Graphics;

/// <summary>Техника экранной амбиентной окклюзии AO-пасса превью. Выбирает пиксельный шейдер в
/// <see cref="SsaoPassResources"/> (SsaoCommon.hlsl против GtaoCommon.hlsl) - весь остальной
/// конвейер (таргет, композит, инлайн-дроу в <see cref="ForwardPass"/>) общий для обеих техник.</summary>
public enum AmbientOcclusionMode
{
	/// <summary>Классический спиральный SSAO - счёт заслоняющих тапов (SsaoCommon.hlsl).</summary>
	Ssao,

	/// <summary>Ground Truth AO - горизонты по срезам + аналитический интеграл косинус-взвешенной
	/// видимости (GtaoCommon.hlsl). Меньше серого налёта на плоскостях, чуть дороже.</summary>
	Gtao,
}

/// <summary>Owns the GPU resources for the SSAO post-process: the AO render target plus the two
/// fullscreen materials (depth -&gt; occlusion estimate, then multiplicative composite back into the
/// color target). Created once by <see cref="GraphicsPipelineSimple"/> when SSAO is enabled; drawn
/// inline by <see cref="ForwardPass"/> between the opaque and transmissive draws - see
/// <see cref="WriteInlineCommands"/>.
///
/// В режиме <see cref="AmbientOcclusionMode.Gtao"/> «оценка AO» - это не один дроу, а конвейер
/// XeGTAO из трёх звеньев (см. GtaoCommon.hlsl): префильтр глубины с мип-цепочкой, сам GTAO и
/// краесохраняющий денойзер. Всё это остаётся деталью реализации техники - наружу пасс по-прежнему
/// отдаёт один grayscale-таргет и композитится тем же шейдером.</summary>
public sealed class SsaoPassResources : IReleaseObject
{
	public IRenderTarget AoTarget { get; }
	internal IMaterialObject AoMaterial { get; }
	internal IMaterialObject CompositeMaterial { get; }

	/// <summary>Звеньев в мип-цепочке линейных глубин GTAO, считая полное разрешение (mip 0).
	/// Пять - как в XeGTAO (XE_GTAO_DEPTH_MIP_LEVELS); должно совпадать с GTAO_DEPTH_MIP_LEVELS
	/// в GtaoShared.hlsl и с числом слотов _AoDepth0.._AoDepth4 в GtaoCommon.hlsl.</summary>
	private const int DepthMipLevels = 5;

	// Всё ниже - только для AmbientOcclusionMode.Gtao, в режиме SSAO остаётся null.
	private readonly IRenderTarget[]? _gtaoDepth;
	private readonly IRenderTarget? _gtaoDenoiseTarget;
	private readonly IMaterialObject? _gtaoPrefilterMaterial;
	private readonly IMaterialObject[]? _gtaoMipMaterials;
	private readonly IMaterialObject? _gtaoDenoiseMaterial;

	/// <param name="colorFormat">Формат цветового таргета геометрии, в который композит рисует инлайн
	/// (RGBA16F в HDR-режиме превью) - PSO обязан совпадать с привязанным таргетом. Сама AO-оценка
	/// остаётся grayscale RGBA8: видимость - величина в [0..1], HDR-точность ей не нужна.</param>
	public SsaoPassResources(IGraphicsApi graphicsApi, IBatchRenderer batchRenderer, string colorTargetName,
		uint width, uint height, IGpuTexture depthTarget, IGpuTexture sceneCopyTarget,
		AmbientOcclusionMode aoMode = AmbientOcclusionMode.Ssao,
		TextureObjectFormat colorFormat = TextureObjectFormat.R8G8B8A8UNorm)
	{
		var gtao = aoMode == AmbientOcclusionMode.Gtao;
		AoTarget = graphicsApi.CreateRenderTarget(new TextureInfo
		{
			name = colorTargetName + " SSAO",
			width = width,
			height = height,
			format = TextureObjectFormat.R8G8B8A8UNorm,
		});

		// У КАЖДОГО материала свой экземпляр VS: DiligentMaterial.Release() освобождает свои
		// шейдеры, и шареный между материалами экземпляр при пересоздании окружения
		// освобождался бы дважды (AV в ComObject.Release - см. историю с RecreateEnvironment).
		var aoVs = graphicsApi.CreateShader("SSAO Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);
		var compositeVs = graphicsApi.CreateShader("SSAO Composite Fullscreen VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl", ShaderObjectType.Vertex);

		// PSO без депта: AO и композит - фуллскрин-треугольники по готовой глубине.
		var postProcessState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSAO PostProcess PSO",
			RenderTargetFormats = [TextureObjectFormat.R8G8B8A8UNorm],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		// Обе техники пишут grayscale в тот же таргет и композитятся тем же SsaoCompositePS.hlsl.
		// Разница в том, ЧТО читает оценка: SSAO - сам депт-буфер, GTAO - префильтрованную цепочку
		// линейных глубин.
		var aoShaderFile = gtao ? "GtaoPS.hlsl" : "SsaoPS.hlsl";
		var aoPs = graphicsApi.CreateShader("SSAO PS", "EditorAssets/shader", aoShaderFile, ShaderObjectType.Pixel);
		AoMaterial = graphicsApi.CreateMaterial("SSAO Material");
		AoMaterial.SetShader(aoVs, aoPs);
		AoMaterial.SetState(postProcessState);
		batchRenderer.BindViewConstants(AoMaterial);

		if (gtao)
		{
			// Цепочка линейных глубин: [0] - полное разрешение, дальше каждое звено вдвое меньше.
			// RGBA16F, а не одноканальный float: односоставных форматов у движка нет
			// (см. TextureObjectFormat), а half под вью-спейсную глубину хватает - тот же выбор,
			// что в XeGTAO (XE_GTAO_USE_HALF_FLOAT_PRECISION).
			_gtaoDepth = new IRenderTarget[DepthMipLevels];
			for (int i = 0; i < DepthMipLevels; i++)
			{
				var (w, h) = MipSize(width, height, i);
				_gtaoDepth[i] = graphicsApi.CreateRenderTarget(new TextureInfo
				{
					name = $"{colorTargetName} GTAO Depth {i}",
					width = w,
					height = h,
					format = TextureObjectFormat.R16G16B16A16Float,
				});
			}

			// Результат денойзера - отдельный таргет: фильтр читает окрестность 3x3 своего входа,
			// поэтому писать поверх него нельзя.
			_gtaoDenoiseTarget = graphicsApi.CreateRenderTarget(new TextureInfo
			{
				name = colorTargetName + " GTAO Denoised",
				width = width,
				height = height,
				format = TextureObjectFormat.R8G8B8A8UNorm,
			});

			var depthChainState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
			{
				Name = "GTAO Depth Chain PSO",
				RenderTargetFormats = [TextureObjectFormat.R16G16B16A16Float],
				DepthStencilFormat = TextureObjectFormat.Unknown,
				PrimitiveTopology = PrimitiveTopologyType.TriangleList,
				RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
				DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
				InputLayout = [],
			});

			// ТОЧЕЧНЫЙ сэмплер: билинейная фильтрация смешала бы соседние глубины внутри уровня, а
			// среднее между двумя поверхностями не лежит ни на одной из них - реконструированная
			// точка уехала бы в пустоту (в XeGTAO ровно та же оговорка про point_point_point).
			var pointSampler = graphicsApi.CreateSampler(
				name: "GTAO Depth Sampler",
				filter: TextureFilter.Point,
				address: TextureAddress.Clamp,
				comparisonFunction: CompFunction.Always,
				border: Vector4.Zero);

			_gtaoPrefilterMaterial = CreateFullscreenMaterial(graphicsApi, batchRenderer, depthChainState,
				"GTAO Depth Prefilter", "GtaoDepthPrefilterPS.hlsl");
			_gtaoPrefilterMaterial.SetTexture("_DepthTex", depthTarget);

			_gtaoMipMaterials = new IMaterialObject[DepthMipLevels - 1];
			for (int i = 0; i < DepthMipLevels - 1; i++)
			{
				var mip = CreateFullscreenMaterial(graphicsApi, batchRenderer, depthChainState,
					$"GTAO Depth Mip {i + 1}", "GtaoDepthMipPS.hlsl");
				mip.SetTexture("_SourceTex", _gtaoDepth[i]);
				mip.SetImmutableSampler("_SourceTex", pointSampler);
				_gtaoMipMaterials[i] = mip;
			}

			for (int i = 0; i < DepthMipLevels; i++)
			{
				AoMaterial.SetTexture($"_AoDepth{i}", _gtaoDepth[i]);
				AoMaterial.SetImmutableSampler($"_AoDepth{i}", pointSampler);
			}

			_gtaoDenoiseMaterial = CreateFullscreenMaterial(graphicsApi, batchRenderer, postProcessState,
				"GTAO Denoise", "GtaoDenoisePS.hlsl");
			_gtaoDenoiseMaterial.SetTexture("_AoTex", AoTarget);
			_gtaoDenoiseMaterial.SetImmutableSampler("_AoTex", pointSampler);
		}
		else
		{
			AoMaterial.SetTexture("_DepthTex", depthTarget);
		}

		// Композит рисует ВНУТРИ ForwardPass в текущий render-таргет геометрии - PSO обязан
		// совпадать с ним по формату.
		var compositeState = graphicsApi.CreateGraphicsState(new GraphicsStateInfo
		{
			Name = "SSAO Composite PSO",
			RenderTargetFormats = [colorFormat],
			DepthStencilFormat = TextureObjectFormat.Unknown,
			PrimitiveTopology = PrimitiveTopologyType.TriangleList,
			RasterizerState = new RasterizerStateInfo { CullMode = CullModeType.None },
			DepthStencilState = new DepthStencilStateInfo { DepthEnable = false },
			InputLayout = [],
		});

		// Композит билатеральный (размытие AO по глубине, см. SsaoCompositeCommon.hlsl).
		var compositePs = graphicsApi.CreateShader("SSAO Composite PS", "EditorAssets/shader", "SsaoCompositePS.hlsl", ShaderObjectType.Pixel);
		CompositeMaterial = graphicsApi.CreateMaterial("SSAO Composite Material");
		CompositeMaterial.SetShader(compositeVs, compositePs);
		CompositeMaterial.SetState(compositeState);
		batchRenderer.BindViewConstants(CompositeMaterial);

		var postProcessSampler = graphicsApi.CreateSampler(
			name: "SSAO Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);
		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetImmutableSampler("_SceneTex", postProcessSampler);

		// Композит читает ПОСЛЕДНЕЕ звено техники: у SSAO это сама оценка, у GTAO - её
		// отфильтрованная денойзером копия.
		CompositeMaterial.SetTexture("_AoTex", FinalAoTexture);
		CompositeMaterial.SetImmutableSampler("_AoTex", postProcessSampler);
		CompositeMaterial.SetTexture("_DepthTex", depthTarget);

		// Иначе cbuffer остался бы с мусором до первого пуша (кадрирование случается только после
		// загрузки модели, а AO-пасс рисует с первого кадра).
		SetWorldRange(0f);
		SetDebugView(false);
		SetMipLevelSizes(width, height);
	}

	/// <summary>Таргет, из которого композит берёт готовое AO.</summary>
	private IGpuTexture FinalAoTexture => _gtaoDenoiseTarget ?? (IGpuTexture)AoTarget;

	private static (uint W, uint H) MipSize(uint width, uint height, int level)
	{
		uint div = 1u << level;
		return (Math.Max(width / div, 1u), Math.Max(height / div, 1u));
	}

	/// <summary>Фуллскрин-материал одного звена GTAO-конвейера. У КАЖДОГО свой экземпляр VS - см.
	/// комментарий выше (шареный шейдер освобождался бы дважды при пересоздании окружения).</summary>
	private static IMaterialObject CreateFullscreenMaterial(IGraphicsApi graphicsApi,
		IBatchRenderer batchRenderer, IStateObject state, string name, string pixelShaderFile)
	{
		var vs = graphicsApi.CreateShader(name + " VS", "EditorAssets/shader", "SkyBackgroundVS.hlsl",
			ShaderObjectType.Vertex);
		var ps = graphicsApi.CreateShader(name + " PS", "EditorAssets/shader", pixelShaderFile,
			ShaderObjectType.Pixel);

		var material = graphicsApi.CreateMaterial(name + " Material");
		material.SetShader(vs, ps);
		material.SetState(state);
		batchRenderer.BindViewConstants(material);
		return material;
	}

	/// <summary>Layout of the "AoConstants" cbuffer в SsaoCommon.hlsl/GtaoCommon.hlsl - ровно 16
	/// байт (SetConstant грузит размер структуры, округлённый вверх до 16).</summary>
	private struct AoConstantsData
	{
		public float WorldRange;

		/// <summary>Контраст итоговой видимости (показатель степени, «интенсивность» AO).
		/// 0 = дефолт шейдера.</summary>
		public float Power;

		/// <summary>Нижний предел видимости - экранный AO не вправе гасить свет в ноль.
		/// Отрицательное = дефолт шейдера.</summary>
		public float Floor;

		public float Pad2;
	}

	/// <summary>Мировой радиус влияния AO. Пушится после кадрирования модели как доля её
	/// габаритного радиуса (см. ModelPreviewViewport.FrameAll) - с ним контактная тень не
	/// схлопывается при приближении камеры. 0 = легаси-поведение (радиус в долях экрана,
	/// falloff в долях глубины точки - см. SsaoCommon.hlsl/GtaoCommon.hlsl).</summary>
	public void SetWorldRange(float worldRange) => SetConstants(worldRange, _power, _floor);

	private float _power;
	private float _floor = -1f;

	/// <summary>Сила (степень контраста) и нижний предел AO - живые ручки окна Graphics
	/// (см. GraphicsSettingsWindow). Радиус сохраняется прежним.</summary>
	public void SetStrength(float power, float floor)
	{
		_power = power;
		_floor = floor;
		SetConstants(_worldRange, power, floor);
	}

	/// <summary>Layout кбуфера "AoComposite" в SsaoCompositePS.hlsl - те же 16 байт.</summary>
	private struct AoCompositeData
	{
		/// <summary>0 = обычный композит, 1 = вывести AO в grayscale вместо затенённого кадра.</summary>
		public float DebugView;

		/// <summary>Делать ли билатеральное размытие AO в самом композите. У GTAO выключено - его
		/// результат уже прошёл краесохраняющий денойзер (см. GtaoDenoisePS.hlsl).</summary>
		public float Blur;

		public float Pad1;
		public float Pad2;
	}

	/// <summary>Отладочный вид AO - живая ручка окна Graphics (чекбокс "AO debug view"): композит
	/// выводит саму видимость вместо умножения на неё кадра. Не трогает оценку AO, так что видно
	/// ровно то, чем светит шейдинг.</summary>
	public void SetDebugView(bool enabled)
	{
		var data = new AoCompositeData
		{
			DebugView = enabled ? 1f : 0f,
			Blur = _gtaoDenoiseTarget is null ? 1f : 0f,
		};
		CompositeMaterial.SetConstant("AoComposite", ref data);
	}

	/// <summary>Layout кбуфера "GtaoLevel" в GtaoDepthMipPS.hlsl - два float4 (32 байта).</summary>
	private struct GtaoLevelData
	{
		/// <summary>xy - размер звена, zw - 1/xy.</summary>
		public Vector4 TargetSize;

		/// <summary>xy - размер источника, zw - 1/xy.</summary>
		public Vector4 SourceSize;
	}

	/// <summary>Раздаёт звеньям мип-цепочки их размеры: собственный размер таргета шейдеру неоткуда
	/// взять (viewData.viewport несёт полное разрешение кадра и после SetViewport не меняется), а
	/// без размера источника нечем клампить выборку 2x2 на его границе.</summary>
	private void SetMipLevelSizes(uint width, uint height)
	{
		if (_gtaoMipMaterials is null)
		{
			return;
		}

		for (int i = 0; i < _gtaoMipMaterials.Length; i++)
		{
			var src = MipSize(width, height, i);
			var dst = MipSize(width, height, i + 1);
			var data = new GtaoLevelData
			{
				TargetSize = new Vector4(dst.W, dst.H, 1f / dst.W, 1f / dst.H),
				SourceSize = new Vector4(src.W, src.H, 1f / src.W, 1f / src.H),
			};
			_gtaoMipMaterials[i].SetConstant("GtaoLevel", ref data);
		}
	}

	private float _worldRange;

	private void SetConstants(float worldRange, float power, float floor)
	{
		_worldRange = worldRange;
		var data = new AoConstantsData { WorldRange = worldRange, Power = power, Floor = floor };
		AoMaterial.SetConstant("AoConstants", ref data);

		// Тот же кбуфер нужен и звеньям GTAO-конвейера: фильтр мипов взвешивает глубины ТЕМ ЖЕ
		// радиусом влияния, что и главный пасс (иначе мипы усредняли бы то, чего пасс на этой
		// дальности уже не видит), а денойзер применяет нижний предел видимости.
		if (_gtaoMipMaterials is not null)
		{
			foreach (var mip in _gtaoMipMaterials)
			{
				mip.SetConstant("AoConstants", ref data);
			}
		}

		_gtaoDenoiseMaterial?.SetConstant("AoConstants", ref data);
	}

	/// <summary>Перепривязывает ресайзабельные таргеты ПОСЛЕ Resize - Resize пересоздаёт нативные
	/// текстуры, и SRB иначе держали бы уничтоженные (см. ModelPreviewViewport.ResizeTargets).</summary>
	public void RebindTargets(IGpuTexture depthTarget, IGpuTexture sceneCopyTarget)
	{
		// Собственные таргеты GTAO-конвейера ресайзит этот метод: наружу торчит только AoTarget
		// (см. ModelViewportEnvironment.AoTarget), и его вызывающий уже привёл к новому размеру -
		// от него и пляшем.
		var size = AoTarget.Size;
		var width = (uint)size.X;
		var height = (uint)size.Y;

		if (_gtaoDepth is not null)
		{
			for (int i = 0; i < _gtaoDepth.Length; i++)
			{
				var (w, h) = MipSize(width, height, i);
				_gtaoDepth[i].Resize(new Vector2(w, h));
			}
		}

		_gtaoDenoiseTarget?.Resize(size);
		SetMipLevelSizes(width, height);

		if (_gtaoDepth is not null)
		{
			// Resize пересоздал нативные текстуры звеньев - все SRB, смотрящие на них, надо
			// перепривязать, иначе продолжат сэмплировать уничтоженные.
			_gtaoPrefilterMaterial!.SetTexture("_DepthTex", depthTarget);
			for (int i = 0; i < _gtaoMipMaterials!.Length; i++)
			{
				_gtaoMipMaterials[i].SetTexture("_SourceTex", _gtaoDepth[i]);
			}

			for (int i = 0; i < _gtaoDepth.Length; i++)
			{
				AoMaterial.SetTexture($"_AoDepth{i}", _gtaoDepth[i]);
			}

			_gtaoDenoiseMaterial!.SetTexture("_AoTex", AoTarget);
		}
		else
		{
			AoMaterial.SetTexture("_DepthTex", depthTarget);
		}

		CompositeMaterial.SetTexture("_SceneTex", sceneCopyTarget);
		CompositeMaterial.SetTexture("_AoTex", FinalAoTexture);
		CompositeMaterial.SetTexture("_DepthTex", depthTarget);
	}

	/// <summary>AO-оценка + мультипликативный композит, инлайн внутри <see cref="ForwardPass"/> -
	/// МЕЖДУ opaque-дроу и transmissive-дроу, а не поверх готового кадра. Так transmissive-поверхность
	/// не глушится AO ни сама, ни через преломлённый фон: экранная окклюзия аппроксимирует
	/// заслонённость рассеянного амбиента У ПОВЕРХНОСТИ, а свет от transmissive-поверхности - это
	/// преломлённый фон плюс френель, к которым она неприменима. <see cref="ForwardPass"/> намеренно
	/// НЕ переснимает scene copy после композита - почему именно, см. комментарий там же.
	/// Требования к вызывающему: opaque уже отрисован, снапшот сцены уже скопирован/отрезолвлен в
	/// sceneCopyTarget (композит читает его как _SceneTex), render-таргеты отвязаны.</summary>
	internal void WriteInlineCommands(ICommandBuffer cmd, IGpuTexture renderColor, IGpuTexture renderDepth,
		Ref<Vector2> viewPortRef)
	{
		// Именно DepthRead, а не ShaderResource: SRV депт-текстуры на Vulkan биндится с лейаутом
		// DEPTH_STENCIL_READ_ONLY_OPTIMAL (VUID-VkDescriptorImageInfo-imageLayout-00344). Обратно в
		// DepthWrite депт вернёт SetRenderTarget transmissive-дроу (см. DiligentCommandBuffer).
		cmd.TransitionResource(renderDepth, ResourceState.DepthRead);

		// GTAO: сначала линейная глубина и её мип-цепочка - главный пасс читает ТОЛЬКО их
		// (см. GtaoCommon.hlsl), депт-буфер дальше префильтра не идёт.
		if (_gtaoDepth is not null)
		{
			DrawToTarget(cmd, _gtaoPrefilterMaterial!, _gtaoDepth[0]);
			for (int i = 0; i < _gtaoMipMaterials!.Length; i++)
			{
				DrawToTarget(cmd, _gtaoMipMaterials[i], _gtaoDepth[i + 1]);
			}
		}

		cmd.SetRenderTarget(AoTarget, null);
		cmd.SetViewport(viewPortRef);
		cmd.SetPipelineState(AoMaterial);
		cmd.CommitShaderResources(AoMaterial);
		cmd.Draw(3);

		cmd.TransitionResource(AoTarget, ResourceState.ShaderResource);

		// GTAO: краесохраняющая фильтрация оценки - в ОТДЕЛЬНЫЙ таргет (фильтр читает окрестность
		// 3x3 своего входа, писать поверх него нельзя). Его-то композит и читает как _AoTex.
		if (_gtaoDenoiseTarget is not null)
		{
			cmd.SetRenderTarget(_gtaoDenoiseTarget, null);
			cmd.SetViewport(viewPortRef);
			cmd.SetPipelineState(_gtaoDenoiseMaterial!);
			cmd.CommitShaderResources(_gtaoDenoiseMaterial!);
			cmd.Draw(3);

			cmd.SetRenderTarget(null, null);
			cmd.TransitionResource(_gtaoDenoiseTarget, ResourceState.ShaderResource);
		}

		// Композит пишет в render-таргет геометрии (при MSAA - мультисемпловый, см. SampleCount
		// его PSO) без депта - фуллскрин-треугольнику депт-тест не нужен.
		cmd.SetRenderTarget(renderColor, null);
		cmd.SetViewport(viewPortRef);
		cmd.SetPipelineState(CompositeMaterial);
		cmd.CommitShaderResources(CompositeMaterial);
		cmd.Draw(3);
	}

	/// <summary>Дроу одного звена цепочки в свой таргет - со СВОИМ вьюпортом (звенья мельче кадра) и
	/// с переходом в ShaderResource: следующее звено читает результат предыдущего как SRV, и переход
	/// обязан случиться до его привязки.</summary>
	private static void DrawToTarget(ICommandBuffer cmd, IMaterialObject material, IRenderTarget target)
	{
		var size = target.Size;
		cmd.SetRenderTarget(target, null);
		cmd.SetViewport((uint)size.X, (uint)size.Y);
		cmd.SetPipelineState(material);
		cmd.CommitShaderResources(material);
		cmd.Draw(3);

		cmd.SetRenderTarget(null, null);
		cmd.TransitionResource(target, ResourceState.ShaderResource);
	}

	public void Release()
	{
		AoTarget.Release();
		AoMaterial.Release();
		CompositeMaterial.Release();

		_gtaoPrefilterMaterial?.Release();
		_gtaoDenoiseMaterial?.Release();
		_gtaoDenoiseTarget?.Release();

		if (_gtaoMipMaterials is not null)
		{
			foreach (var mip in _gtaoMipMaterials)
			{
				mip.Release();
			}
		}

		if (_gtaoDepth is not null)
		{
			foreach (var depth in _gtaoDepth)
			{
				depth.Release();
			}
		}
	}
}

