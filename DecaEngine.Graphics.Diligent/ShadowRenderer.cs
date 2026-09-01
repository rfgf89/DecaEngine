using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Diligent.RenderGraph;
using Diligent;
using ClearDepthStencilFlags = DecaEngine.Graphics.ClearDepthStencilFlags;
using ResourceState = DecaEngine.Graphics.ResourceState;
using SetVertexBuffersFlags = DecaEngine.Graphics.SetVertexBuffersFlags;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent
{
	public class ShadowRenderer : IReleaseObject
	{
		private readonly DiligentGraphicsApi _api;
		private IMaterialObject _shadowMaterial;
		private IMaterialObject _punctualShadowMaterial;
		private ISamplerObject _shadowComparisonSampler;
		private ISamplerObject _shadowPointSampler;
		private IRenderTarget _shadowMaps;
		private IRenderTarget _punctualShadowMaps;

		/// <summary>1x1x1 D32-заглушка слотов ShadowMaps/PunctualShadowMaps у материалов,
		/// зарегистрированных ДО первого настоящего теневого дроу (см. EnsureShadowMaps,
		/// EnsurePunctualShadowMaps). Vulkan требует валидный дескриптор на каждый объявленный в
		/// шейдере слот уже на Register() - заводить ради этого полные 4096х4 / 1024х16 массива
		/// значило бы платить их память в ЛЮБОМ окружении, включая те, что тени не рисуют вовсе
		/// (например asset-browser icon baker: shadows=false, ShadowPass в граф не входит).</summary>
		private IRenderTarget _shadowMapPlaceholder;

		public const int MaxCascades = 4;
		public const int ShadowMapSize = 4096;

		/// <summary>Поле каймы у ортокаскада, в текселях shadow map: орто-матрица описывается не
		/// вокруг сферы каскада, а вокруг сферы ПЛЮС эта кайма (см. UpdateCascades /
		/// SimpleCullingAndRenderSystem.BuildLightData).
		///
		/// Без каймы объём каскада ложится в карту ровно от края до края, а шейдер точки у края
		/// НЕ БЕРЁТ: SampleWorldLightShadow отбрасывает каскад, пока точка не ушла внутрь на
		/// SUN_CASCADE_MARGIN_TEXELS (иначе тапы PCF адресуются за карту и читают краевой тексель,
		/// то есть глубину другого места сцены). Получалось, что кайма отъедалась ИЗНУТРИ объёма:
		/// точка проваливалась в следующий каскад - грубее и с другим байасом, - а на последнем в
		/// «освещено». Это и есть просветы по границам каскадов.
		///
		/// Восемь = 3 (отступ шейдера) + 1 (тап PCF) + 1.5 (normal-offset) + 1 (снап центра к сетке
		/// текселей двигает саму сферу) с запасом. Цена - 0.4% линейного разрешения каскада.</summary>
		public const float CascadeMarginTexels = 8f;

		/// <summary>Реальный каскадный массив, если уже заведён (был хотя бы один каскадный дроу),
		/// иначе временная заглушка - геттер безопасен ДО первого кадра теней: его дергает КАЖДЫЙ
		/// батчинг-дроу (см. DiligentBatchRenderer.ExecuteDrawBatching) вне зависимости от того,
		/// рисуются ли тени в этом окружении вообще.</summary>
		public IRenderTarget ShadowMapsTarget => _shadowMaps ?? _shadowMapPlaceholder;

		/// <summary>Texture array теней punctual-светов (спот - слайс, точечный - шесть граней куба;
		/// раскладка кадра - <see cref="DecaEngine.Graphics.Diligent.LightClusters.MaxShadowSlices"/>,
		/// см. PunctualShadowScheduler). Та же конвенция глубины, что у каскадов: обычный Z, запись
		/// Less, сравнение LessEqual. Заглушка до первого punctual-дроу - см. <see cref="ShadowMapsTarget"/>.</summary>
		public IRenderTarget PunctualShadowMapsTarget => _punctualShadowMaps ?? _shadowMapPlaceholder;
		public ISamplerObject ShadowComparisonSampler => _shadowComparisonSampler;
		public ISamplerObject ShadowPointSampler => _shadowPointSampler;

		// Материалы с альфа-тестом при записи тени (листва, ажурные решётки) - СВОЙ материал на
		// каждый, а не один общий с перепривязкой текстуры перед дроу: SetTexture обновляет SRB, а
		// трогать дескриптор-сет, пока предыдущий кадр ещё в полёте, роняет валидацию Vulkan
		// ("bound VkDescriptorSet was destroyed or updated") - та же причина, по которой у
		// FogPassResources отдельный кбуфер вместо SetConstant.
		//
		// Материалов здесь единицы: критерий отбора (см. ModelViewportGeometry) пропускает только
		// реально «дырявую» геометрию, а не всё, что экспортер пометил MASK.
		private readonly Dictionary<int, IMaterialObject> _alphaTestedMaterials = new();

		/// <summary>Punctual-аналог <see cref="_alphaTestedMaterials"/> - свой PSO с уменьшенным
		/// байасом (см. EnsurePunctualShadowMaterial), заводится лениво при первом punctual-дроу.</summary>
		private readonly Dictionary<int, IMaterialObject> _punctualAlphaTestedMaterials = new();

		/// <summary>Параметры, которыми был зарегистрирован каждый масочный материал - нужны, чтобы
		/// собрать его punctual-вариант ПОЗЖЕ, при первом реальном punctual-дроу (см.
		/// EnsurePunctualShadowMaterial), если он ещё не существовал на момент RegisterAlphaTestedMaterial.</summary>
		private readonly Dictionary<int, AlphaTestedMaterialParams> _alphaTestedMaterialParams = new();

		/// <summary>Материалы, получившие SetShadowResources, - когда заглушка сменяется на реальный
		/// массив (EnsureShadowMaps/EnsurePunctualShadowMaps), их слоты перепривязываются на месте
		/// тем же SetTexture, что и горячая замена текстур при стриминге (см. ModelLoader.cs,
		/// "SRB живого материала обновляется на месте" / ModelStore.PumpTextureUpgrades) - безопасный
		/// для этого движка приём, не тот единичный-в-кадре случай, которого боится комментарий выше.</summary>
		private readonly List<IMaterialObject> _shadowBoundMaterials = new();

		private IBufferHandle _viewConstants, _lightConstants, _gpuInstances;

		/// <summary>Сколько материалов пишут тень с альфа-тестом (см. <see cref="RegisterAlphaTestedMaterial"/>).
		/// Ноль на сцене с листвой означает, что критерий отбора её не признал, - без этого числа
		/// отличить «крона монолитная» от «крона не помечена» можно только в RenderDoc.</summary>
		public int AlphaTestedMaterialCount => _alphaTestedMaterials.Count;

		/// <summary>Материалы, которые в shadow map не пишутся вовсе - см.
		/// <see cref="IBatchRenderer.SetMaterialShadowCasting"/>.</summary>
		private readonly HashSet<int> _nonCastingMaterials = new();

		/// <summary>Сколько материалов исключено из кастеров. Парная к
		/// <see cref="AlphaTestedMaterialCount"/> диагностика: «декали перестали отбрасывать тень» и
		/// «декалей в сцене не нашлось» на картинке выглядят одинаково.</summary>
		public int NonCastingMaterialCount => _nonCastingMaterials.Count;

		/// <summary>См. <see cref="IBatchRenderer.SetMaterialShadowCasting"/>.</summary>
		public void SetMaterialShadowCasting(int materialId, bool casts)
		{
			if (casts)
			{
				_nonCastingMaterials.Remove(materialId);
			}
			else
			{
				_nonCastingMaterials.Add(materialId);
			}
		}

		private readonly struct AlphaTestedMaterialParams
		{
			public readonly IGpuTexture BaseColorTexture;
			public readonly ISamplerObject BaseColorSampler;
			public readonly float AlphaCutoff;
			public readonly ModelLoader.StreamedTexture Stream;

			public AlphaTestedMaterialParams(IGpuTexture baseColorTexture, ISamplerObject baseColorSampler,
				float alphaCutoff, ModelLoader.StreamedTexture stream)
			{
				BaseColorTexture = baseColorTexture;
				BaseColorSampler = baseColorSampler;
				AlphaCutoff = alphaCutoff;
				Stream = stream;
			}
		}

		public ShadowRenderer(DiligentGraphicsApi api)
		{
			_api = api;
			CreateSamplers();
			CreatePlaceholder();

			// Большие массивы (_shadowMaps/_punctualShadowMaps) и базовые теневые материалы/PSO -
			// НЕ здесь: см. EnsureShadowMaps/EnsurePunctualShadowMaps/EnsureShadowMaterial/
			// EnsurePunctualShadowMaterial, заводятся при первом настоящем теневом дроу.
		}

		/// <summary>Заводит альфа-тестовый теневой материал для <paramref name="materialId"/>: тот же
		/// depth-only PSO, что у сплошной геометрии, плюс пиксельный шейдер с clip() по альфе
		/// базовой текстуры (см. ShadowMaskedPS.hlsl). Пока такой материал не заведён, геометрия
		/// пишет в shadow map сплошные квады - прежнее поведение.
		///
		/// <paramref name="stream"/> - запись стриминга этой текстуры (null вне режима стриминга):
		/// теневой материал добавляется в её список привязок, иначе крона осталась бы с белым
		/// 1x1-филлером (альфа 1 - clip не срабатывает) навсегда, пока экранный материал получал бы
		/// ступени качества.</summary>
		public void RegisterAlphaTestedMaterial(int materialId, IGpuTexture baseColorTexture,
			ISamplerObject baseColorSampler, float alphaCutoff, ModelLoader.StreamedTexture stream)
		{
			if (baseColorTexture is null || _alphaTestedMaterials.ContainsKey(materialId))
			{
				return;
			}

			var parms = new AlphaTestedMaterialParams(baseColorTexture, baseColorSampler, alphaCutoff, stream);
			_alphaTestedMaterialParams[materialId] = parms;

			_alphaTestedMaterials[materialId] =
				CreateAlphaTestedShadowMaterial(materialId, "Shadow Masked", GetBaseState(), parms);

			// Punctual-вариант - только если punctual-набор УЖЕ поднят (см. EnsurePunctualShadowMaterial);
			// иначе он соберётся из _alphaTestedMaterialParams при первом настоящем punctual-дроу, чтобы
			// не тащить punctual PSO/шейдеры в окружение, которое punctual-тени вообще не рисует.
			if (_punctualShadowMaterial != null)
			{
				_punctualAlphaTestedMaterials[materialId] =
					CreateAlphaTestedShadowMaterial(materialId, "Punctual Shadow Masked", GetPunctualBaseState(), parms);
			}
		}

		/// <summary>Общее тело для каскадного и punctual масочного теневого материала - тот же
		/// шейдер/раскладка, разный только базовый rasterizer state (байас, см. GetBaseState vs
		/// GetPunctualBaseState).</summary>
		private IMaterialObject CreateAlphaTestedShadowMaterial(int materialId, string namePrefix,
			GraphicsStateInfo stateInfo, AlphaTestedMaterialParams parms)
		{
			// СВОИ экземпляры шейдеров - как в SsaoPassResources и по той же причине: шареный
			// освобождался бы дважды. Реальной компиляции это почти не стоит, у загрузчика есть
			// кэш вариантов (см. "load compile: N вызовов, M РЕАЛЬНЫХ").
			var vs = new DiligentShader(_api, $"{namePrefix} VS {materialId}", "EditorAssets/shader",
				"ShadowVS.hlsl", ShaderObjectType.Vertex, "Main");
			var ps = new DiligentShader(_api, $"{namePrefix} PS {materialId}", "EditorAssets/shader",
				"ShadowMaskedPS.hlsl", ShaderObjectType.Pixel, "Main");

			var material = new DiligentMaterial($"{namePrefix} Material {materialId}", _api);
			material.SetShader(vs, ps);

			stateInfo.Name = $"{namePrefix} PSO {materialId}";
			stateInfo.DepthStencilFormat = TextureObjectFormat.D32Float;
			stateInfo.DepthStencilState.DepthFunc = ComparisonFunctionType.Less;
			material.SetState(_api.CreateGraphicsState(stateInfo));

			// Буферов может ещё не быть (модель регистрируется до первой реаллокации инстансов) -
			// тогда их привяжет ближайший UpdateMaterialResources, он же зовётся при каждой
			// реаллокации.
			BindFrameBuffers(material);

			material.SetTexture("_MainTex", parms.BaseColorTexture);
			material.SetImmutableSampler("_MainTex", parms.BaseColorSampler);

			// Единственный пуш константы - ДО первого кадра (см. комментарий в ShadowMaskedPS.hlsl).
			var cutoffConstant = new Vector4(MathF.Max(parms.AlphaCutoff, 1e-3f), 0f, 0f, 0f);
			material.SetConstant("ShadowMaterial", ref cutoffConstant);

			parms.Stream?.Bindings.Add((material, "_MainTex"));

			return material;
		}

		private void CreatePlaceholder()
		{
			// arraySize = 2, а не 1: DiligentRenderTarget выводит TextureType из arraySize
			// (Texture2DArray только при arraySize > 1, см. DiligentRenderTarget.cs) - заглушка
			// биндится в те же слоты ShadowMaps/PunctualShadowMaps, что и реальные массивы, а те
			// объявлены в шейдере как Texture2DArray. Texture2D под тем же именем прошёл бы
			// компиляцию материала, но был бы несовместимого измерения дескриптора.
			_shadowMapPlaceholder = _api.CreateRenderTarget(new TextureInfo
			{
				name = "Shadow Map Placeholder",
				width = 1,
				height = 1,
				format = TextureObjectFormat.D32Float,
				arraySize = 2,
			});
		}

		private void CreateSamplers()
		{
			// Sampler for hardware PCF comparison
			_shadowComparisonSampler = _api.CreateSampler(
				"Shadow Comparison Sampler",
				TextureFilter.ComparisonLinear,
				TextureAddress.Clamp,
				CompFunction.LessEqual, // Correct for reversed-Z depth buffer (clear value 0.0, depth func Greater)
				new Vector4(1.0f, 1.0f, 1.0f, 1.0f));

			// Sampler for raw depth reads (blocker search in PCSS)
			_shadowPointSampler = _api.CreateSampler(
				"Shadow Point Sampler",
				TextureFilter.Point,
				TextureAddress.Clamp,
				CompFunction.LessEqual,
				new Vector4(1.0f, 1.0f, 1.0f, 1.0f));
		}

		/// <summary>Заводит каскадный массив теней (4096^2 x MaxCascades) при первом настоящем
		/// каскадном дроу (см. ExecuteDrawShadows) и перепривязывает слот ShadowMaps у всех уже
		/// зарегистрированных материалов (см. _shadowBoundMaterials) с заглушки на реальный массив.</summary>
		private void EnsureShadowMaps()
		{
			if (_shadowMaps != null)
			{
				return;
			}

			_shadowMaps = _api.CreateRenderTarget(new TextureInfo
			{
				name = "Shadow Map",
				width = ShadowMapSize,
				height = ShadowMapSize,
				format = TextureObjectFormat.D32Float,
				arraySize = MaxCascades,
			});

			foreach (var material in _shadowBoundMaterials)
			{
				material.SetTexture("ShadowMaps", _shadowMaps);
			}
		}

		/// <summary>Punctual-аналог <see cref="EnsureShadowMaps"/> - заводится независимо, при первом
		/// настоящем punctual-дроу (см. ExecuteDrawPunctualShadow), не при каскадном.</summary>
		private void EnsurePunctualShadowMaps()
		{
			if (_punctualShadowMaps != null)
			{
				return;
			}

			_punctualShadowMaps = _api.CreateRenderTarget(new TextureInfo
			{
				name = "Punctual Shadow Maps",
				width = LightClusters.ShadowMapSize,
				height = LightClusters.ShadowMapSize,
				format = TextureObjectFormat.D32Float,
				arraySize = LightClusters.MaxShadowSlices,
			});

			foreach (var material in _shadowBoundMaterials)
			{
				material.SetTexture("PunctualShadowMaps", _punctualShadowMaps);
			}
		}

		private void EnsureShadowMaterial()
		{
			if (_shadowMaterial != null)
			{
				return;
			}

			_shadowMaterial = new DiligentMaterial("Shadow Material", _api);

			var shadowVs = new DiligentShader(_api, "Shadow VS", "EditorAssets/shader", "ShadowVS.hlsl", ShaderObjectType.Vertex, "Main");
			_shadowMaterial.SetShader(shadowVs);

			var stateInfo = GetBaseState();
			stateInfo.Name = "Shadow PSO";
			stateInfo.RenderTargetFormats = [];
			stateInfo.DepthStencilFormat = TextureObjectFormat.D32Float;
			// Using Less (with reversed-Z this behaves as "closer to light") during shadow map generation
			stateInfo.DepthStencilState.DepthFunc = ComparisonFunctionType.Less;

			_shadowMaterial.SetState(_api.CreateGraphicsState(stateInfo));

			BindFrameBuffers(_shadowMaterial);
		}

		/// <summary>Punctual-аналог <see cref="EnsureShadowMaterial"/>: свой PSO с уменьшенным
		/// байасом (см. GetPunctualBaseState) плюс punctual-варианты уже зарегистрированных масочных
		/// материалов, которых до этой точки не существовало (см. RegisterAlphaTestedMaterial).</summary>
		private void EnsurePunctualShadowMaterial()
		{
			if (_punctualShadowMaterial != null)
			{
				return;
			}

			_punctualShadowMaterial = new DiligentMaterial("Punctual Shadow Material", _api);

			var shadowVs = new DiligentShader(_api, "Punctual Shadow VS", "EditorAssets/shader", "ShadowVS.hlsl", ShaderObjectType.Vertex, "Main");
			_punctualShadowMaterial.SetShader(shadowVs);

			var stateInfo = GetPunctualBaseState();
			stateInfo.Name = "Punctual Shadow PSO";
			stateInfo.RenderTargetFormats = [];
			stateInfo.DepthStencilFormat = TextureObjectFormat.D32Float;
			stateInfo.DepthStencilState.DepthFunc = ComparisonFunctionType.Less;

			_punctualShadowMaterial.SetState(_api.CreateGraphicsState(stateInfo));

			BindFrameBuffers(_punctualShadowMaterial);

			foreach (var kvp in _alphaTestedMaterialParams)
			{
				if (!_punctualAlphaTestedMaterials.ContainsKey(kvp.Key))
				{
					_punctualAlphaTestedMaterials[kvp.Key] = CreateAlphaTestedShadowMaterial(
						kvp.Key, "Punctual Shadow Masked", GetPunctualBaseState(), kvp.Value);
				}
			}
		}

		public void UpdateMaterialResources(IBufferHandle viewConstants, IBufferHandle lightConstants, IBufferHandle gpuInstances)
		{
			// Запоминаются и для альфа-тестовых материалов: те заводятся ПОЗЖЕ, при регистрации
			// модели, и своих ручек к этим буферам не имеют.
			_viewConstants = viewConstants;
			_lightConstants = lightConstants;
			_gpuInstances = gpuInstances;

			// Базовые теневые материалы могут ещё не существовать (лениво заводятся первым реальным
			// дроу, см. EnsureShadowMaterial/EnsurePunctualShadowMaterial) - тогда их привяжет
			// собственный BindFrameBuffers внутри Ensure*, вызванный позже с уже актуальными полями.
			if (_shadowMaterial != null)
			{
				BindFrameBuffers(_shadowMaterial);
			}

			if (_punctualShadowMaterial != null)
			{
				BindFrameBuffers(_punctualShadowMaterial);
			}

			// Перепривязка масочных материалов - ОБЯЗАТЕЛЬНА, а не подстраховка: этот метод зовётся
			// при КАЖДОЙ реаллокации инстанс-буферов (см. DiligentBatchRenderer), и буфер инстансов
			// там пересоздаётся заново. Материал, схвативший старый на момент регистрации модели,
			// уходит в дроу с мёртвым дескриптором - Vulkan валит это как VUID-08114, а картинка
			// пропадает целиком.
			foreach (var material in _alphaTestedMaterials.Values)
			{
				BindFrameBuffers(material);
			}

			foreach (var material in _punctualAlphaTestedMaterials.Values)
			{
				BindFrameBuffers(material);
			}
		}

		/// <summary>Покадровые буферы теневого дроу. View в ShadowVS.hlsl не объявлен и привязка в
		/// него - no-op; она сохранена ради симметрии с остальными материалами движка.</summary>
		private void BindFrameBuffers(IMaterialObject material)
		{
			if (_viewConstants is null)
			{
				return;
			}

			material.SetBuffer("View", _viewConstants, HandleAccess.Vertex);
			material.SetBuffer("Light", _lightConstants, HandleAccess.Vertex);
			material.SetBuffer("GPURenderInstances", _gpuInstances, HandleAccess.Vertex);
		}

		public void SetShadowResources(IMaterialObject material)
		{
			material.SetTexture("ShadowMaps", ShadowMapsTarget);
			material.SetSampler("ShadowMaps_sampler", ShadowComparisonSampler);
			material.SetSampler("ShadowMaps_sampler_point", ShadowPointSampler);

			// Тени punctual-светов - тем же PCF-сэмплером сравнения. Шейдеры без этих объявлений
			// (фуллскрин-пассы через BindShadowResources) просто игнорируют привязку.
			material.SetTexture("PunctualShadowMaps", PunctualShadowMapsTarget);
			material.SetSampler("PunctualShadowMaps_sampler", ShadowComparisonSampler);

			// И ShadowMaps, и PunctualShadowMaps сейчас смотрят на заглушку (см. ShadowMapsTarget/
			// PunctualShadowMapsTarget), если соответствующий массив ещё не заведён, - запоминаем
			// материал, чтобы EnsureShadowMaps/EnsurePunctualShadowMaps перепривязали слот на
			// реальный массив, когда он появится.
			_shadowBoundMaterials.Add(material);
		}

		public void ExecuteDrawShadows(
			ICommandBuffer cmd,
			DiligentBufferHandle megaVertexBufferGPU,
			DiligentBufferHandle megaIndexBufferGPU,
			CullResult cullResult,
			uint cascadeIndex)
		{
			if (cascadeIndex < 0 || cascadeIndex >= MaxCascades) return;

			// Первый настоящий каскадный дроу - здесь и только здесь заводится реальный массив и
			// базовый PSO (см. EnsureShadowMaps/EnsureShadowMaterial); окружения без ShadowPass
			// (shadows=false, см. ModelViewportEnvironment) сюда вообще не заходят.
			EnsureShadowMaps();
			EnsureShadowMaterial();

			cmd.TransitionResource(_shadowMaps, ResourceState.DepthWrite);

			cmd.TransitionResource(cullResult.FinallyInstancesBuffer, ResourceState.VertexBuffer);
			cmd.TransitionResource(cullResult.GpuInstancesDataBuffer, ResourceState.ShaderResource);
			cmd.TransitionResource(cullResult.IndirectArgsBuffers, ResourceState.IndirectArgument);

			cmd.SetRenderTarget(null, _shadowMaps, 0, cascadeIndex);
			cmd.SetViewport(ShadowMapSize, ShadowMapSize);
			// Clear depth to 0.0 for reversed-Z
			cmd.ClearDepthStencil(_shadowMaps, ClearDepthStencilFlags.Depth, 1.0f, 0, cascadeIndex);

			cmd.SetVertexBuffers(0, [megaVertexBufferGPU, cullResult.FinallyInstancesBuffer], [0ul, 0ul], SetVertexBuffersFlags.Reset);
			cmd.SetIndexBuffer(megaIndexBufferGPU, 0);

			// Быстрый путь: масочных материалов в сцене нет вовсе - вся геометрия рисуется ОДНИМ
			// indirect-дроу, ровно как раньше. Диапазоны материалов лежат непрерывно от нуля (см.
			// DiligentBatchRenderer.UpdateDrawRangesCache), поэтому суммы каунтов достаточно.
			//
			// Разделение по материалам стоит по дроу-коллу на материал НА КАЖДЫЙ каскад, и платить
			// эту цену на сцене без листвы не за что.
			if (_alphaTestedMaterials.Count == 0 && _nonCastingMaterials.Count == 0)
			{
				cmd.SetPipelineState(_shadowMaterial);
				cmd.CommitShaderResources(_shadowMaterial);

				var drawRange = new MaterialDrawRange();
				foreach (var kvp in cullResult.MaterialDrawRanges)
				{
					drawRange.DrawCount += kvp.Value.DrawCount;
				}

				cmd.DrawIndexedIndirect(cullResult.IndirectArgsBuffers, drawRange, IndexType.UInt32);
			}
			else
			{
				// Есть масочные - каждому материалу свой дроу со своим PSO. Соседние сплошные
				// диапазоны НЕ склеиваются: они непрерывны, но склейка потребовала бы сортировки и
				// учёта дыр, а выигрыш - единицы дроу-коллов на каскад.
				foreach (var kvp in cullResult.MaterialDrawRanges)
				{
					if (kvp.Value.DrawCount == 0)
					{
						continue;
					}

					// Материал вообще не кастер (BLEND-накладки) - его дроу просто не делаем.
					if (_nonCastingMaterials.Contains(kvp.Key))
					{
						continue;
					}

					var material = _alphaTestedMaterials.TryGetValue(kvp.Key, out var masked)
						? masked
						: _shadowMaterial;

					cmd.SetPipelineState(material);
					cmd.CommitShaderResources(material);
					cmd.DrawIndexedIndirect(cullResult.IndirectArgsBuffers, kvp.Value, IndexType.UInt32);
				}
			}

			// DepthRead, а не общий ShaderResource: SRV депт-текстуры на Vulkan биндится с лейаутом
			// DEPTH_STENCIL_READ_ONLY_OPTIMAL (VUID-VkDescriptorImageInfo-imageLayout-00344).
			cmd.TransitionResource(_shadowMaps, ResourceState.DepthRead);
		}

		/// <summary>Пишет ОДИН слайс теней punctual-света (см. PunctualShadowScheduler) - та же
		/// механика, что у каскада: ShadowVS трансформирует по CascadeMatrix[0] текущего Light-кбуфера,
		/// который вызывающий залил матрицей слайса. Мёртвый слайс (drawCount = 0 в культе) чистится и
		/// рисует пусто - замороженная петля ForwardPass зовёт это для ВСЕХ слайсов кадра.</summary>
		public void ExecuteDrawPunctualShadow(
			ICommandBuffer cmd,
			DiligentBufferHandle megaVertexBufferGPU,
			DiligentBufferHandle megaIndexBufferGPU,
			CullResult cullResult,
			uint sliceIndex)
		{
			if (sliceIndex >= LightClusters.MaxShadowSlices) return;

			// Первый настоящий punctual-дроу - заводит punctual-массив и punctual-PSO (свой, меньший
			// байас - см. GetPunctualBaseState) НЕЗАВИСИМО от каскадного набора: окружение может
			// рисовать каскад солнца без punctual-теней или наоборот.
			EnsurePunctualShadowMaps();
			EnsurePunctualShadowMaterial();

			cmd.TransitionResource(_punctualShadowMaps, ResourceState.DepthWrite);

			cmd.TransitionResource(cullResult.FinallyInstancesBuffer, ResourceState.VertexBuffer);
			cmd.TransitionResource(cullResult.GpuInstancesDataBuffer, ResourceState.ShaderResource);
			cmd.TransitionResource(cullResult.IndirectArgsBuffers, ResourceState.IndirectArgument);

			cmd.SetRenderTarget(null, _punctualShadowMaps, 0, sliceIndex);
			cmd.SetViewport(LightClusters.ShadowMapSize, LightClusters.ShadowMapSize);
			cmd.ClearDepthStencil(_punctualShadowMaps, ClearDepthStencilFlags.Depth, 1.0f, 0, sliceIndex);

			cmd.SetVertexBuffers(0, [megaVertexBufferGPU, cullResult.FinallyInstancesBuffer], [0ul, 0ul], SetVertexBuffersFlags.Reset);
			cmd.SetIndexBuffer(megaIndexBufferGPU, 0);

			// Та же логика масочных материалов, что у каскадов (см. ExecuteDrawShadows), но по
			// punctual-набору - _shadowMaterial/_alphaTestedMaterials калиброваны под 4096^2
			// ортографический каскад и дают акне/peter-panning на 1024^2 перспективном срезе (см.
			// GetPunctualBaseState).
			if (_punctualAlphaTestedMaterials.Count == 0 && _nonCastingMaterials.Count == 0)
			{
				cmd.SetPipelineState(_punctualShadowMaterial);
				cmd.CommitShaderResources(_punctualShadowMaterial);

				var drawRange = new MaterialDrawRange();
				foreach (var kvp in cullResult.MaterialDrawRanges)
				{
					drawRange.DrawCount += kvp.Value.DrawCount;
				}

				cmd.DrawIndexedIndirect(cullResult.IndirectArgsBuffers, drawRange, IndexType.UInt32);
			}
			else
			{
				foreach (var kvp in cullResult.MaterialDrawRanges)
				{
					if (kvp.Value.DrawCount == 0)
					{
						continue;
					}

					// Не кастер - не кастер и для punctual-светов (см. ExecuteDrawShadows).
					if (_nonCastingMaterials.Contains(kvp.Key))
					{
						continue;
					}

					var material = _punctualAlphaTestedMaterials.TryGetValue(kvp.Key, out var masked)
						? masked
						: _punctualShadowMaterial;

					cmd.SetPipelineState(material);
					cmd.CommitShaderResources(material);
					cmd.DrawIndexedIndirect(cullResult.IndirectArgsBuffers, kvp.Value, IndexType.UInt32);
				}
			}
		}

		/// <summary>Переводит массив теней punctual-светов в состояние чтения из шейдера. Зовётся
		/// КАЖДЫМ ForwardPass перед камерными дроу - в том числе когда слайсы не рисовались вовсе
		/// (превью без светов): текстура объявлена в PS безусловно, и лейаут обязан быть валиден
		/// (VUID-08114/00344), пусть содержимое и не читается при ShadowParams.x = -1. Через геттер, а
		/// не поле напрямую: до первого punctual-дроу массив ещё не заведён и транзишен идёт по
		/// заглушке (см. PunctualShadowMapsTarget) - дешёвый no-op layout transition на 1х1 текстуре.</summary>
		public void TransitionPunctualShadowsForRead(ICommandBuffer cmd)
		{
			cmd.TransitionResource(PunctualShadowMapsTarget, ResourceState.DepthRead);
		}

		private GraphicsStateInfo GetBaseState() => GetBaseState(2000, 2f);

		/// <summary>Байас для punctual-среза (1024^2 ПЕРСПЕКТИВНАЯ проекция). Основную анти-акне работу
		/// делает шейдерный байас в масштабе текселя (UnlitInstancedPS.hlsl, мировые единицы, переведён
		/// в NDC под глубину приёмника) - здесь гасится только квантование самого растра.
		///
		/// Прежние 100 единиц были назначены "на порядок меньше каскадных" по аналогии, и это не
		/// работает: у D32_FLOAT константный байас масштабируется как bias * 2^(exp(z) - 23), где exp -
		/// экспонента максимальной глубины примитива. У перспективного слайса вся геометрия жмётся к
		/// единице (замеренный кадр: лампа near 0.05 / far 6.4, пол на 5.15 даёт ndc 0.998), то есть
		/// exp = -1 и шаг равен 2^-24. Сотня таких шагов - 6e-6 NDC, а это на той же дистанции ~3 мм
		/// против следа текселя в ~10 мм. У ортокаскада глубина размазана по всему [0,1], экспонента в
		/// среднем много меньше, и его 2000 единиц весят несопоставимо больше. Тысяча даёт ~3 см на
		/// пяти метрах - ниже порога заметного peter-panning и выше кванта растра.</summary>
		/// <summary>У punctual-слайсов ближний клип ВКЛЮЧЁН, в отличие от каскадов. У орто-каскада
		/// глаз оттянут от сцены на радиус, и кастер перед ближней плоскостью - легитимный окклюдер,
		/// его надо клампить, а не резать (см. depthClipDisable ниже). У ПЕРСПЕКТИВНОЙ грани
		/// точечного света "перед ближней плоскостью" означает "вплотную к лампе или за ней":
		/// треугольник, пересекающий плоскость глаза грани (w=0), при отключённом клипе
		/// растеризуется размазанной проекцией с глубиной, прижатой к near, и закрашивает огромный
		/// кусок карты грани - ВЕСЬ конус этой грани уходит в тень, на приёмниках это круглая "дыра"
		/// в свете, чей радиус не зависит от Range (репро: вертикальная панель в 0.3 от лампы,
		/// пересекающая её высоту). Кастер честно ближе near (0.05..0.25, см.
		/// PunctualShadowScheduler.SliceNearPlane) при включённом клипе тени не пишет - и не должен:
		/// он практически внутри светильника.</summary>
		private GraphicsStateInfo GetPunctualBaseState() => GetBaseState(1000, 2f, depthClipDisable: false);

		private GraphicsStateInfo GetBaseState(int depthBias, float slopeScaledDepthBias,
			bool depthClipDisable = true)
		{
			return new GraphicsStateInfo
			{
				Name = "Shadow PSO",
				RenderTargetFormats = [],
				PrimitiveTopology = PrimitiveTopologyType.TriangleList,
				RasterizerState = new RasterizerStateInfo
				{
					// БЕЗ отсечения: прежний Front-cull (глубина задних граней, классическая
					// анти-акне конвенция) делал ОДНОСТОРОННЮЮ геометрию прозрачной для света -
					// у планок крыши/ткани/листвы нет задних граней, и солнце прошивало крышу
					// полосами света на пол двора (Sponza-двор, "тени линиями сквозь объекты").
					// Акне от записи лицевых граней давится байасами ниже + normal-offset на
					// сэмплинге (см. UnlitInstancedPS.SampleWorldLightShadow).
					CullMode = CullModeType.None,
					DepthBias = depthBias,
					SlopeScaledDepthBias = slopeScaledDepthBias,

					// Кастеры перед ближней плоскостью не отсекаются, а кламмятся к ней - см.
					// RasterizerStateInfo.DepthClipDisable. Оттяжка глаза каскада считается от его
					// РАДИУСА, а до реальных окклюдеров расстояние своё у сцены, поэтому первому,
					// самому мелкому каскаду ближняя плоскость режет кастеров раньше всех - в его
					// тени появляются дыры, которых у крупных каскадов на том же месте нет.
					// У punctual-слайсов клип ВКЛЮЧЁН - см. GetPunctualBaseState.
					DepthClipDisable = depthClipDisable,
				},
				DepthStencilState = new DepthStencilStateInfo
				{
					DepthEnable = true,
					DepthFunc = ComparisonFunctionType.Less
				},
				InputLayout =
				[
					new InputLayoutElementInfo { InputIndex = 0, NumComponents = 3, ValueType = InputElementValueType.Float32, IsNormalized = false },
					new InputLayoutElementInfo { InputIndex = 1, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
					new InputLayoutElementInfo { InputIndex = 2, NumComponents = 3, ValueType = InputElementValueType.Float32, IsNormalized = false },
					// Unused by ShadowVS.hlsl (it only reads position), but must still be declared: this
					// PSO reads from the same mega vertex buffer as DiligentBatchRenderer's (see
					// GetBaseState there), and Diligent auto-computes each buffer slot's stride from its
					// declared layout elements - omitting Tangent/Color here would under-report slot 0's
					// true per-vertex stride and misalign every vertex after the first.
					new InputLayoutElementInfo { InputIndex = 4, NumComponents = 4, ValueType = InputElementValueType.Float32, IsNormalized = false },
					new InputLayoutElementInfo { InputIndex = 5, NumComponents = 4, ValueType = InputElementValueType.Float32, IsNormalized = false },
					new InputLayoutElementInfo { InputIndex = 6, NumComponents = 2, ValueType = InputElementValueType.Float32, IsNormalized = false },
					new InputLayoutElementInfo
					{
						InputIndex = 3, BufferSlot = 1, NumComponents = 1, ValueType = InputElementValueType.Int32,
						IsNormalized = false,
						Frequency = InputElementFrequencyType.PerInstance
					}
				]
			};
		}

		public void Release()
		{
			_shadowMaps?.Release();
			_punctualShadowMaps?.Release();
			_shadowMapPlaceholder?.Release();

			_shadowMaterial?.Release();
			_punctualShadowMaterial?.Release();

			foreach (var material in _alphaTestedMaterials.Values)
			{
				material.Release();
			}
			_alphaTestedMaterials.Clear();
			_nonCastingMaterials.Clear();

			foreach (var material in _punctualAlphaTestedMaterials.Values)
			{
				material.Release();
			}
			_punctualAlphaTestedMaterials.Clear();

			_alphaTestedMaterialParams.Clear();
			_shadowBoundMaterials.Clear();
		}
	}
}