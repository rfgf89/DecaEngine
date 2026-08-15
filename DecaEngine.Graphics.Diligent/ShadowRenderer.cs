using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent.RenderGraph;
using Diligent;
using ClearDepthStencilFlags = DecaEngine.Core.ClearDepthStencilFlags;
using ResourceState = DecaEngine.Core.ResourceState;
using SetVertexBuffersFlags = DecaEngine.Core.SetVertexBuffersFlags;
using ValueType = Diligent.ValueType;

namespace DecaEngine.Graphics.Diligent
{
	public class ShadowRenderer : IReleaseObject
	{
		private readonly DiligentGraphicsApi _api;
		private readonly IMaterialObject _shadowMaterial;
		private ISamplerObject _shadowComparisonSampler;
		private ISamplerObject _shadowPointSampler;
		private IRenderTarget _shadowMaps;
		private IRenderTarget _punctualShadowMaps;

		public const int MaxCascades = 4;
		public const int ShadowMapSize = 4096;

		public IRenderTarget ShadowMapsTarget => _shadowMaps;

		/// <summary>Texture array теней punctual-светов (спот - слайс, точечный - шесть граней куба;
		/// раскладка кадра - <see cref="DecaEngine.Graphics.Diligent.LightClusters.MaxShadowSlices"/>,
		/// см. PunctualShadowScheduler). Та же конвенция глубины, что у каскадов: обычный Z, запись
		/// Less, сравнение LessEqual.</summary>
		public IRenderTarget PunctualShadowMapsTarget => _punctualShadowMaps;
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

		private IBufferHandle _viewConstants, _lightConstants, _gpuInstances;

		/// <summary>Сколько материалов пишут тень с альфа-тестом (см. <see cref="RegisterAlphaTestedMaterial"/>).
		/// Ноль на сцене с листвой означает, что критерий отбора её не признал, - без этого числа
		/// отличить «крона монолитная» от «крона не помечена» можно только в RenderDoc.</summary>
		public int AlphaTestedMaterialCount => _alphaTestedMaterials.Count;

		public ShadowRenderer(DiligentGraphicsApi api)
		{
			_api = api;
			CreateShadowMapAndSamplers();

			_shadowMaterial = new DiligentMaterial("Shadow Material", api);
			CreateShadowPso();
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

			// СВОИ экземпляры шейдеров - как в SsaoPassResources и по той же причине: шареный
			// освобождался бы дважды. Реальной компиляции это почти не стоит, у загрузчика есть
			// кэш вариантов (см. "load compile: N вызовов, M РЕАЛЬНЫХ").
			var vs = new DiligentShader(_api, $"Shadow Masked VS {materialId}", "EditorAssets/shader",
				"ShadowVS.hlsl", ShaderObjectType.Vertex, "Main");
			var ps = new DiligentShader(_api, $"Shadow Masked PS {materialId}", "EditorAssets/shader",
				"ShadowMaskedPS.hlsl", ShaderObjectType.Pixel, "Main");

			var material = new DiligentMaterial($"Shadow Masked Material {materialId}", _api);
			material.SetShader(vs, ps);

			var stateInfo = GetBaseState();
			stateInfo.Name = $"Shadow Masked PSO {materialId}";
			stateInfo.DepthStencilFormat = TextureObjectFormat.D32Float;
			stateInfo.DepthStencilState.DepthFunc = ComparisonFunctionType.Less;
			material.SetState(_api.CreateGraphicsState(stateInfo));

			// Буферов может ещё не быть (модель регистрируется до первой реаллокации инстансов) -
			// тогда их привяжет ближайший UpdateMaterialResources, он же зовётся при каждой
			// реаллокации.
			BindFrameBuffers(material);

			material.SetTexture("_MainTex", baseColorTexture);
			material.SetImmutableSampler("_MainTex", baseColorSampler);

			// Единственный пуш константы - ДО первого кадра (см. комментарий в ShadowMaskedPS.hlsl).
			var cutoffConstant = new Vector4(MathF.Max(alphaCutoff, 1e-3f), 0f, 0f, 0f);
			material.SetConstant("ShadowMaterial", ref cutoffConstant);

			stream?.Bindings.Add((material, "_MainTex"));

			_alphaTestedMaterials[materialId] = material;
		}

		private void CreateShadowMapAndSamplers()
		{
			_shadowMaps = _api.CreateRenderTarget(new TextureInfo
			{
				name = $"Shadow Map",
				width = ShadowMapSize,
				height = ShadowMapSize,
				format = TextureObjectFormat.D32Float,
				arraySize = MaxCascades,
			});

			_punctualShadowMaps = _api.CreateRenderTarget(new TextureInfo
			{
				name = "Punctual Shadow Maps",
				width = LightClusters.ShadowMapSize,
				height = LightClusters.ShadowMapSize,
				format = TextureObjectFormat.D32Float,
				arraySize = LightClusters.MaxShadowSlices,
			});

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

		private void CreateShadowPso()
		{
			var shadowVs = new DiligentShader(_api, "Shadow VS", "EditorAssets/shader", "ShadowVS.hlsl", ShaderObjectType.Vertex, "Main");
			_shadowMaterial.SetShader(shadowVs);

			var stateInfo = GetBaseState();
			stateInfo.Name = "Shadow PSO";
			stateInfo.RenderTargetFormats = [];
			stateInfo.DepthStencilFormat = TextureObjectFormat.D32Float;
			// Using Less (with reversed-Z this behaves as "closer to light") during shadow map generation
			stateInfo.DepthStencilState.DepthFunc = ComparisonFunctionType.Less;

			_shadowMaterial.SetState(_api.CreateGraphicsState(stateInfo));
		}

		public void UpdateMaterialResources(IBufferHandle viewConstants, IBufferHandle lightConstants, IBufferHandle gpuInstances)
		{
			// Запоминаются и для альфа-тестовых материалов: те заводятся ПОЗЖЕ, при регистрации
			// модели, и своих ручек к этим буферам не имеют.
			_viewConstants = viewConstants;
			_lightConstants = lightConstants;
			_gpuInstances = gpuInstances;

			BindFrameBuffers(_shadowMaterial);

			// Перепривязка масочных материалов - ОБЯЗАТЕЛЬНА, а не подстраховка: этот метод зовётся
			// при КАЖДОЙ реаллокации инстанс-буферов (см. DiligentBatchRenderer), и буфер инстансов
			// там пересоздаётся заново. Материал, схвативший старый на момент регистрации модели,
			// уходит в дроу с мёртвым дескриптором - Vulkan валит это как VUID-08114, а картинка
			// пропадает целиком.
			foreach (var material in _alphaTestedMaterials.Values)
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
			material.SetTexture("PunctualShadowMaps", _punctualShadowMaps);
			material.SetSampler("PunctualShadowMaps_sampler", ShadowComparisonSampler);
		}

		public void ExecuteDrawShadows(
			ICommandBuffer cmd,
			DiligentBufferHandle megaVertexBufferGPU,
			DiligentBufferHandle megaIndexBufferGPU,
			CullResult cullResult,
			uint cascadeIndex)
		{
			if (cascadeIndex < 0 || cascadeIndex >= MaxCascades) return;

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
			if (_alphaTestedMaterials.Count == 0)
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

			cmd.TransitionResource(_punctualShadowMaps, ResourceState.DepthWrite);

			cmd.TransitionResource(cullResult.FinallyInstancesBuffer, ResourceState.VertexBuffer);
			cmd.TransitionResource(cullResult.GpuInstancesDataBuffer, ResourceState.ShaderResource);
			cmd.TransitionResource(cullResult.IndirectArgsBuffers, ResourceState.IndirectArgument);

			cmd.SetRenderTarget(null, _punctualShadowMaps, 0, sliceIndex);
			cmd.SetViewport(LightClusters.ShadowMapSize, LightClusters.ShadowMapSize);
			cmd.ClearDepthStencil(_punctualShadowMaps, ClearDepthStencilFlags.Depth, 1.0f, 0, sliceIndex);

			cmd.SetVertexBuffers(0, [megaVertexBufferGPU, cullResult.FinallyInstancesBuffer], [0ul, 0ul], SetVertexBuffersFlags.Reset);
			cmd.SetIndexBuffer(megaIndexBufferGPU, 0);

			// Та же логика масочных материалов, что у каскадов (см. ExecuteDrawShadows).
			if (_alphaTestedMaterials.Count == 0)
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
				foreach (var kvp in cullResult.MaterialDrawRanges)
				{
					if (kvp.Value.DrawCount == 0)
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
		}

		/// <summary>Переводит массив теней punctual-светов в состояние чтения из шейдера. Зовётся
		/// КАЖДЫМ ForwardPass перед камерными дроу - в том числе когда слайсы не рисовались вовсе
		/// (превью без светов): текстура объявлена в PS безусловно, и лейаут обязан быть валиден
		/// (VUID-08114/00344), пусть содержимое и не читается при ShadowParams.x = -1.</summary>
		public void TransitionPunctualShadowsForRead(ICommandBuffer cmd)
		{
			cmd.TransitionResource(_punctualShadowMaps, ResourceState.DepthRead);
		}

		private GraphicsStateInfo GetBaseState()
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
					DepthBias = 2000,
					SlopeScaledDepthBias = 2f,
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
			if (_shadowMaps != null)
			{
				_shadowMaps?.Release();
			}

			_punctualShadowMaps?.Release();

			_shadowMaterial?.Release();

			foreach (var material in _alphaTestedMaterials.Values)
			{
				material.Release();
			}
			_alphaTestedMaterials.Clear();
		}
	}
}