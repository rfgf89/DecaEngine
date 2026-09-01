using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Hexa.NET.ImGui;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>Режимы просмотра: каналы/сабмеши/вайрфрейм и BVH-оверлей бейкера. Часть <see cref="ModelPreviewViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class ModelPreviewViewport
	{
		/// <summary>Switches the sub-mesh view mode (see <see cref="InspectorWindow"/>'s View Mode combo).
		/// No-op outside sub-mesh view. Independent of <see cref="WireframeEnabled"/> - the wireframe
		/// overlay, if on, stays on across a mode switch.</summary>
		public void SetSubMeshViewMode(SubMeshPreviewMode mode)
		{
			if (_viewMode == mode || !IsSubMeshView)
			{
				return;
			}

			_viewMode = mode;
			ApplyPreviewSettingsToMaterials();
		}

		/// <summary>Toggles the wireframe overlay (see <see cref="InspectorWindow"/>'s Wireframe checkbox) -
		/// orthogonal to <see cref="SetSubMeshViewMode"/>, so it can be combined with either Highlight or
		/// Channel. No-op outside sub-mesh view.</summary>
		public void SetWireframeEnabled(bool enabled)
		{
			if (_wireframeEnabled == enabled || !IsSubMeshView)
			{
				return;
			}

			_wireframeEnabled = enabled;

			if (enabled)
			{
				PopulateWireframeOverlay();
			}
			else
			{
				ClearWireframeOverlay();
			}
		}

		/// <summary>Switches the Channel-mode debug channel (see <see cref="InspectorWindow"/>'s Channel
		/// combo). Only has a visible effect while <see cref="ViewMode"/> is <see cref="SubMeshPreviewMode.Channel"/>.</summary>
		public void SetPreviewChannel(PreviewChannel channel)
		{
			if (_previewChannel == channel)
			{
				return;
			}

			_previewChannel = channel;
			ApplyPreviewSettingsToMaterials();
		}

		/// <summary>
		/// Pushes the current view mode/channel to every material of the resident model via
		/// <see cref="IMaterialObject.SetConstant{T}"/> (see UnlitInstancedPS.hlsl's PreviewSettings
		/// cbuffer). The whole-model view (<see cref="IsSubMeshView"/> false) always maps to Lighting
		/// (Mode 3) regardless of <see cref="ViewMode"/> - that combo is only shown/meaningful for
		/// sub-mesh view (see <see cref="InspectorWindow.RenderModelPreview"/>).
		/// </summary>
		private void ApplyPreviewSettingsToMaterials()
		{
			// The whole-model view always renders in Lighting (PBR) mode; the View Mode combo only
			// exists for an isolated sub-mesh (see InspectorWindow.RenderModelPreview).
			int mode = !IsSubMeshView ? 3 : _viewMode switch
			{
				SubMeshPreviewMode.Channel => 2,
				SubMeshPreviewMode.Lighting => 3,
				_ => 1,
			};

			// Отладочные виды (каналы/подсветка сабмеша, AO debug, probe debug) пишут в кадр УЖЕ
			// отображаемые значения - HDR-конвейер обязан прокинуть их мимо экспозиции и кривой, иначе
			// художник смотрел бы не на то, что шейдер посчитал (см. TonemapPassResources.SetPassthrough).
			// Пушится ДО выхода по отсутствию модели: пустое превью тоже рисует кадр.
			_env.SetTonemapPassthrough(mode != 3 || _editorSettings.AoDebugView
				|| (_editorSettings.ProbeGiDebugView && !IsSubMeshView));

			if (_residentModel == null)
			{
				return;
			}

			var data = new PreviewSettingsData
			{
				// Кривая действует только в LDR - в HDR её применяет TonemapPass (см. Tonemap.hlsl).
				ToneCurve = _editorSettings.ToneCurve,
				Mode = mode,
				Channel = (int)_previewChannel,
				EnvYawRadians = _env.ShadowSettings?.EnvYawRadians ?? 0f,
				ShadowMode = _editorSettings.ShadowFilterMode,
			};

			// Параметры сетки проб (нули = probe-GI выключен, Origin.w - тумблер в шейдере): атласы
			// уже привязаны к материалам в PollProbeBake, здесь только кбуфер. Выключение тумблера
			// в окне Graphics просто перестаёт слать сетку - атласы остаются привязанными, но
			// мёртвая ветка шейдера их не читает.
			if (_probeTextures != null && _editorSettings.PreviewProbeGi)
			{
				ProbeGiViewportShared.PushGrid(ref data, _probeTextures,
					_editorSettings.ProbeGiNormalBias, _editorSettings.ProbeGiViewBias);
			}

			// Live-ручки probe-GI/солнца (см. кбуфер ProbeGiParams в UnlitInstancedPS.hlsl).
			data.ProbeGiParams = new Vector4(
				Math.Clamp(_editorSettings.ProbeGiShadowFloor, 0f, 1f),
				Math.Clamp(_editorSettings.ProbeGiSpecularFloor, 0f, 1f),
				Math.Clamp(_editorSettings.ProbeGiSunIntensity, 0.1f, 16f),
				Math.Clamp(_editorSettings.ProbeGiAmbientBoost, 0.1f, 128f));
			// y - сторона окто-карты видимости: шейдер раскладывает по ней тайл пробы в атласе, и
			// значение обязано быть тем же, под которое собрана сессия (см. ProbeGiBakeResult.VisRes).
			data.ProbeGiParams2 = new Vector4(
				Math.Clamp(_editorSettings.ProbeGiSkyShadowFloor, 0.01f, 1f),
				ProbeGiBakeResult.VisRes, 0f, 0f);

			// Отладочный вид probe-GI (см. канал 9 в шейдере) - только для целого вида модели:
			// в сабмеш-режиме каналом управляет Inspector-ов Channel-комбо.
			if (_editorSettings.ProbeGiDebugView && !IsSubMeshView)
			{
				data.Channel = 9;
			}

			// Расстановка проб (канал 10) старше вида поля: если попросили оба, показываем более
			// частный - где стоят пробы.
			if (_editorSettings.ProbeGiDebugProbes && !IsSubMeshView)
			{
				data.Channel = 10;
			}

			// Unlike Mode/Channel, the PBR factors are per material (glTF metallic/roughness/baseColor,
			// see ModelLoader.MaterialPbr), so the constant push has to walk key-value pairs rather than
			// blast one shared struct at every material.
			for (int i = 0; i < _residentModel.materialObjects.Count; i++)
			{
				var kvp = _residentModel.materialObjects.GetAt(i);

				if (!_residentModel.MaterialPbr.TryGetValue(kvp.Key, out var pbr))
				{
					pbr = new MaterialPbrFactors
					{
						BaseColorFactor = Vector4.One,
						MetallicFactor = 0f,
						RoughnessFactor = 0.6f,
						HasBaseColorTexture = false,
						Ior = 1.5f,
						VolumeAttenuation = new Vector4(1f, 1f, 1f, 0f),
						NormalScale = 1f,
						OcclusionStrength = 1f,
						SpecularColorFactor = Vector4.One
					};
				}

				data.Metallic = pbr.MetallicFactor;
				data.Roughness = pbr.RoughnessFactor;
				data.BaseColor = pbr.BaseColorFactor;
				data.HasBaseColorTexture = pbr.HasBaseColorTexture ? 1 : 0;
				data.AlphaCutoff = pbr.AlphaCutoff;
				data.HasMetallicRoughnessTexture = pbr.HasMetallicRoughnessTexture ? 1 : 0;
				data.Transmission = pbr.TransmissionFactor;
				data.Dispersion = pbr.Dispersion;
				data.Ior = pbr.Ior;
				data.VolumeAttenuation = pbr.VolumeAttenuation;
				data.ThicknessWorld = pbr.ThicknessWorld;
				data.FeatureFlags = (int)_featureFlags;
				data.NormalScale = pbr.NormalScale;
				data.OcclusionStrength = pbr.OcclusionStrength;
				data.UvOffset = pbr.UvOffset;
				data.UvTransform = pbr.UvTransform;
				data.UvHasTransform = pbr.HasUvTransform ? 1 : 0;
				data.OcclusionUvSet = pbr.OcclusionUvSet;
				data.SheenColorRoughness = pbr.SheenColorRoughness;
				data.SpecularColorFactor = pbr.SpecularColorFactor;

				kvp.Value.SetConstant("PreviewSettings", ref data, HandleAccess.Pixel);
			}
		}

		// --- Отладочный вид BVH проб ----------------------------------------------------------------

		/// <summary>Сущности каркасных боксов узлов BVH (см. <see cref="PopulateBvhDebugOverlay"/>).</summary>
		private readonly List<Entity> _bvhDebugEntities = new();

		/// <summary>Единичный куб для отладочных боксов: рисуется тем же wireframe-материалом, что и
		/// оверлей сабмеша, поэтому каркас получается из обычной треугольной геометрии. Один меш на
		/// весь оверлей - боксы различаются только Position/Scale3 инстанса.</summary>
		private MeshId? _bvhDebugMeshId;
		private BatchId? _bvhDebugBatchId;

		/// <summary>Снимок настроек, под которым построен текущий оверлей: боксы перестраиваются
		/// только при реальном изменении (спуск по дереву - не бесплатная операция).</summary>
		private (bool On, int Depth, bool Leaves, object? Baker) _bvhDebugState;

		/// <summary>Держит отладочный оверлей BVH в согласии с настройками и текущим деревом.
		/// Зовётся каждый кадр из Update - вся работа только на изменение.</summary>
		private void PollBvhDebugOverlay()
		{
			var wanted = (_editorSettings.ProbeGiBvhDebug && _probeBaker != null,
				Math.Clamp(_editorSettings.ProbeGiBvhDebugDepth, 0, 24),
				_editorSettings.ProbeGiBvhDebugLeaves,
				(object?)_probeBaker);

			if (wanted == _bvhDebugState)
			{
				return;
			}

			_bvhDebugState = wanted;
			ClearBvhDebugOverlay();

			if (wanted.Item1)
			{
				PopulateBvhDebugOverlay(wanted.Item2, wanted.Item3);
			}
		}

		/// <summary>
		/// Создаёт по каркасному боксу на узел BVH (см. <see cref="ProbeGiBaker.CollectDebugBoxes"/>).
		/// Инстансы обычные - те же Position/Rotation/Scale3 + регистрация в RenderResourceManager,
		/// что и у геометрии модели, так что культинг и инстансинг работают как есть.
		/// </summary>
		private void PopulateBvhDebugOverlay(int maxDepth, bool leavesOnly)
		{
			var baker = _probeBaker;
			if (baker == null || !baker.HasGeometry)
			{
				return;
			}

			var boxes = baker.CollectDebugBoxes(maxDepth, leavesOnly);
			if (boxes.Count == 0)
			{
				return;
			}

			// Верхний предел - защита от «показать все листья Sponza» (сотни тысяч боксов кладут и
			// инстанс-буферы, и глаз): режем и честно говорим об этом в консоль.
			const int MaxBoxes = 20000;
			int drawn = Math.Min(boxes.Count, MaxBoxes);

			try
			{
				EnsureWireframeMaterial();
				EnsureBvhDebugMesh();

				if (_bvhDebugMeshId == null || _wireframeMaterialId == null)
				{
					return;
				}

				_bvhDebugBatchId ??= _env.BatchRenderer.CreateBatch(_bvhDebugMeshId.Value, _wireframeMaterialId.Value);

				for (int i = 0; i < drawn; i++)
				{
					var (min, max, _) = boxes[i];
					var center = (min + max) * 0.5f;
					var size = Vector3.Max(max - min, new Vector3(1e-4f));

					var entity = _env.Store.CreateEntity(
						new Position(center.X, center.Y, center.Z),
						new Scale3(size.X, size.Y, size.Z),
						new Rotation(0f, 0f, 0f, 1f),
						Tags.Get<GpuUpdateTag>());

					_env.ResourceManager.RegisterRenderable(entity, _bvhDebugBatchId.Value);
					_bvhDebugEntities.Add(entity);
				}

				// Тот же барьер + пересборка графа, что у остальных оверлеев: команды ForwardPass
				// заморожены, а мы только что добавили батч/инстансы (см. ClearWireframeOverlay).
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_env.Pipeline.InvalidateGraph();

				var stats = baker.GetStats();
				EngineLog.Add(LogLevel.Info,
					$"Probe BVH debug: {drawn} of {boxes.Count} boxes ({(leavesOnly ? "leaves" : $"depth <= {maxDepth}")}), " +
					$"tree: {stats.Nodes} nodes / {stats.Leaves} leaves / depth {stats.MaxDepth} / " +
					$"{stats.AvgLeafTriangles:F1} tris per leaf" +
					(drawn < boxes.Count ? " - list truncated" : ""));
			}
			catch (Exception ex)
			{
				EngineLog.Add(LogLevel.Error, $"Probe BVH debug: failed to build overlay: {ex.Message}");
				ClearBvhDebugOverlay();
			}
		}

		/// <summary>Единичный куб (центр в нуле, сторона 1) - геометрия отладочного бокса.</summary>
		private void EnsureBvhDebugMesh()
		{
			if (_bvhDebugMeshId != null)
			{
				return;
			}

			var corners = new Vector3[8];
			for (int i = 0; i < 8; i++)
			{
				corners[i] = new Vector3(
					(i & 1) == 0 ? -0.5f : 0.5f,
					(i & 2) == 0 ? -0.5f : 0.5f,
					(i & 4) == 0 ? -0.5f : 0.5f);
			}

			var vertices = new Vertex[8];
			for (int i = 0; i < 8; i++)
			{
				vertices[i] = new Vertex
				{
					Position = corners[i],
					Normal = Vector3.Normalize(corners[i]),
					Tangent = new Vector4(1f, 0f, 0f, 1f),
					Color = Vector4.One,
				};
			}

			// Треугольники куба: в wireframe-состоянии PSO (см. GetWireframeState) они и дают каркас.
			var indices = new uint[]
			{
				0, 2, 1, 1, 2, 3, // -Z
				4, 5, 6, 5, 7, 6, // +Z
				0, 1, 4, 1, 5, 4, // -Y
				2, 6, 3, 3, 6, 7, // +Y
				0, 4, 2, 2, 4, 6, // -X
				1, 3, 5, 3, 7, 5, // +X
			};

			var mesh = _graphicsApi.CreateMesh("BVH Debug Box");
			mesh.SetVertices(vertices);
			mesh.SetIndices(indices);
			mesh.RecalculateBounds();

			_bvhDebugMesh = mesh;
			_bvhDebugMeshId = _env.BatchRenderer.Register(mesh);
		}

		private IMeshObject? _bvhDebugMesh;

		/// <summary>Выбрасывает GPU-сторону отладочного оверлея: зовётся там, где сбрасываются
		/// регистрации батч-рендерера (смена модели, пересоздание окружения) - MeshId/BatchId после
		/// сброса выдаются заново с нуля, и старые указывали бы в чужую геометрию. Вызывающий уже
		/// дождался GPU.
		///
		/// ВНИМАНИЕ: сущности оверлея обязан снять вызывающий - ДО сброса регистраций, пока их
		/// BatchId ещё валиден (см. ClearBvhDebugOverlay). Просто забыть список здесь нельзя: живые
		/// сущности остаются в сторе, их BatchRenderInfo указывает на батч с уже переиспользованным
		/// индексом, и вместо боксов дерева сцена получает копии геометрии НОВОЙ модели, расставленные
		/// по позициям старых боксов.</summary>
		private void ReleaseBvhDebugResources()
		{
			if (_bvhDebugEntities.Count > 0)
			{
				// Страховка на случай нового пути, забывшего снять сущности: без Unregister слоты
				// инстансов утекут, но мусорной геометрии в кадре не будет.
				foreach (var entity in _bvhDebugEntities)
				{
					if (!entity.IsNull)
					{
						entity.DeleteEntity();
					}
				}

				_bvhDebugEntities.Clear();
			}

			_bvhDebugMeshId = null;
			_bvhDebugBatchId = null;
			_bvhDebugState = default;

			_bvhDebugMesh?.Release();
			_bvhDebugMesh = null;
		}

		private void ClearBvhDebugOverlay()
		{
			if (_bvhDebugEntities.Count == 0)
			{
				return;
			}

			foreach (var entity in _bvhDebugEntities)
			{
				_env.ResourceManager.UnregisterRenderable(entity);
				entity.DeleteEntity();
			}

			_bvhDebugEntities.Clear();

			// Без пересборки графа снятые инстансы продолжали бы рисоваться (та же причина, что в
			// ClearWireframeOverlay).
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_env.Pipeline.InvalidateGraph();
		}

		/// <summary>Lazily creates the shared wireframe-overlay material/PSO (see
		/// <see cref="DiligentBatchRenderer.GetWireframeState"/>) - one instance shared by every mesh this
		/// viewport ever draws in wireframe, since it needs no per-material texture/state beyond a flat
		/// color (see WireframeOverlayPS.hlsl).</summary>
		private void EnsureWireframeMaterial()
		{
			if (_wireframeMaterialId != null)
			{
				return;
			}

			var vs = _graphicsApi.CreateShader("Wireframe Overlay VS", "EditorAssets/shader", "UnlitInstancedVS.hlsl", ShaderObjectType.Vertex);
			var ps = _graphicsApi.CreateShader("Wireframe Overlay PS", "EditorAssets/shader", "WireframeOverlayPS.hlsl", ShaderObjectType.Pixel);

			_wireframeMaterial = _graphicsApi.CreateMaterial("Wireframe Overlay Material");
			_wireframeMaterial.SetShader(vs, ps);
			_wireframeMaterial.SetState(_env.BatchRenderer.GetWireframeState());

			_wireframeMaterialId = _env.BatchRenderer.Register(_wireframeMaterial);
		}

		/// <summary>
		/// Adds one wireframe instance per instance of the currently isolated sub-mesh, reusing (and
		/// lazily creating) one wireframe batch per mesh - mirrors <see cref="ModelViewportGeometry.CreateInstanceEntity"/>
		/// but against <see cref="_wireframeMaterialId"/> instead of the sub-mesh's real material, since
		/// the wireframe overlay is the same flat color regardless of which glTF material the geometry uses.
		/// </summary>
		private void PopulateWireframeOverlay()
		{
			if (_residentModel == null || !IsSubMeshView)
			{
				return;
			}

			EnsureWireframeMaterial();

			foreach (var instance in _residentModel.instances)
			{
				if (instance.meshId != _loadedSubMesh)
				{
					continue;
				}

				if (!_meshIdMap.TryGetValue(instance.meshId, out var meshId))
				{
					continue;
				}

				if (!_wireframeBatchCache.TryGetValue(instance.meshId, out var batchId))
				{
					batchId = _env.BatchRenderer.CreateBatch(meshId, _wireframeMaterialId!.Value);
					_wireframeBatchCache[instance.meshId] = batchId;
				}

				var t = instance.transform;
				var entity = _env.Store.CreateEntity(
					new Position(t.position.X, t.position.Y, t.position.Z),
					new Scale3(t.scale.X, t.scale.Y, t.scale.Z),
					new Rotation(t.rotation.X, t.rotation.Y, t.rotation.Z, t.rotation.W),
					Tags.Get<GpuUpdateTag>());

				_env.ResourceManager.RegisterRenderable(entity, batchId);
				_wireframeEntities.Add(entity);
			}

			// See the matching Flush/WaitForIdle/InvalidateGraph comments in LoadModel/PollPendingLoad -
			// a new wireframe batch/material may have just been registered, which the render graph's
			// frozen ForwardPass commands need to be recompiled to pick up.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_env.Pipeline.InvalidateGraph();
		}

		/// <summary>Resets the sub-mesh view mode/channel/wireframe toggle to their defaults (Highlight/
		/// Normal/off) whenever a genuinely new model or sub-mesh selection is about to be populated - a
		/// "Channel: Tangent" or wireframe choice made for one sub-mesh shouldn't silently carry over to
		/// an unrelated one (e.g. with different/no UV data). Wireframe overlay entities themselves are
		/// cleared by <see cref="ClearInstances"/>, called by both call sites right before this.</summary>
		private void ResetPreviewModeForNewSelection()
		{
			_viewMode = SubMeshPreviewMode.Highlight;
			_previewChannel = PreviewChannel.Normal;
			_wireframeEnabled = false;
		}

		private void ClearWireframeOverlay()
		{
			if (_wireframeEntities.Count == 0)
			{
				return;
			}

			foreach (var entity in _wireframeEntities)
			{
				_env.ResourceManager.UnregisterRenderable(entity);
				entity.DeleteEntity();
			}
			_wireframeEntities.Clear();

			// Without this, the wireframe overlay never actually disappears: ForwardPass's commands are
			// only (re-)recorded when the render graph recompiles (see DiligentRenderGraph.Compile/
			// Execute), and DiligentBatchRenderer.CheckAndReallocateBuffers - which re-uploads the CPU-side
			// instance array picking up the now-freed slots as holes for the culling compute shader to
			// skip - only runs from inside that recording (ForwardPass.WriteCommands). The compute dispatch
			// itself IS replayed every frame, but against whatever GPU instance buffer content existed at
			// the last compile, so it would keep "seeing" and drawing the unregistered wireframe instances
			// until something forces a recompile. Same Flush/WaitForIdle/InvalidateGraph triple as
			// LoadModel/PollPendingLoad/PopulateWireframeOverlay above, for consistency.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_env.Pipeline.InvalidateGraph();
		}

	}
}
