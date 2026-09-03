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
	/// <summary>Preview modes: channels/sub-meshes/wireframe and the baker's BVH overlay; state and per-frame Update/Render live in the main file.</summary>
	public partial class ModelPreviewViewport
	{
		/// <summary>Switches the sub-mesh view mode; no-op outside sub-mesh view. Independent of the wireframe overlay.</summary>
		public void SetSubMeshViewMode(SubMeshPreviewMode mode)
		{
			if (_viewMode == mode || !IsSubMeshView)
			{
				return;
			}

			_viewMode = mode;
			ApplyPreviewSettingsToMaterials();
		}

		/// <summary>Toggles the wireframe overlay; orthogonal to the view mode. No-op outside sub-mesh view.</summary>
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

		/// <summary>Switches the Channel-mode debug channel; only visible while ViewMode is Channel.</summary>
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
		/// Pushes the current view mode/channel to every material of the resident model (see the
		/// PreviewSettings cbuffer in UnlitInstancedPS.hlsl). The whole-model view always maps to
		/// Lighting (Mode 3); the View Mode combo only exists for sub-mesh view.
		/// </summary>
		private void ApplyPreviewSettingsToMaterials()
		{
			int mode = !IsSubMeshView ? 3 : _viewMode switch
			{
				SubMeshPreviewMode.Channel => 2,
				SubMeshPreviewMode.Lighting => 3,
				_ => 1,
			};

			// Debug views write already-display-ready values; the HDR pipeline must bypass
			// exposure and the tone curve for them (see TonemapPassResources.SetPassthrough).
			// Pushed BEFORE the null-model early-out: an empty preview still renders a frame.
			_env.SetTonemapPassthrough(mode != 3 || _editorSettings.AoDebugView
				|| (_editorSettings.ProbeGiDebugView && !IsSubMeshView));

			if (_residentModel == null)
			{
				return;
			}

			var data = new PreviewSettingsData
			{
				// The curve applies only in LDR; in HDR the TonemapPass applies it (Tonemap.hlsl).
				ToneCurve = _editorSettings.ToneCurve,
				Mode = mode,
				Channel = (int)_previewChannel,
				EnvYawRadians = _env.ShadowSettings?.EnvYawRadians ?? 0f,
				ShadowMode = _editorSettings.ShadowFilterMode,
			};

			// Probe grid params (zeros = probe-GI off, Origin.w is the shader toggle): atlases are
			// bound in PollProbeBake; turning the toggle off just stops sending the grid.
			if (_probeTextures != null && _editorSettings.PreviewProbeGi)
			{
				ProbeGiViewportShared.PushGrid(ref data, _probeTextures,
					_editorSettings.ProbeGiNormalBias, _editorSettings.ProbeGiViewBias);
			}

			// Live probe-GI/sun knobs (ProbeGiParams cbuffer in UnlitInstancedPS.hlsl).
			data.ProbeGiParams = new Vector4(
				Math.Clamp(_editorSettings.ProbeGiShadowFloor, 0f, 1f),
				Math.Clamp(_editorSettings.ProbeGiSpecularFloor, 0f, 1f),
				Math.Clamp(_editorSettings.ProbeGiSunIntensity, 0.1f, 16f),
				Math.Clamp(_editorSettings.ProbeGiAmbientBoost, 0.1f, 128f));
			// y = visibility octahedral map side: must match what the session was baked with
			// (see ProbeGiBakeResult.VisRes) - the shader uses it to locate probe tiles.
			data.ProbeGiParams2 = new Vector4(
				Math.Clamp(_editorSettings.ProbeGiSkyShadowFloor, 0.01f, 1f),
				ProbeGiBakeResult.VisRes, 0f, 0f);

			// Probe-GI debug view (shader channel 9) - whole-model view only; in sub-mesh mode
			// the Inspector's Channel combo owns the channel.
			if (_editorSettings.ProbeGiDebugView && !IsSubMeshView)
			{
				data.Channel = 9;
			}

			// Probe placement (channel 10) outranks the field view when both are requested.
			if (_editorSettings.ProbeGiDebugProbes && !IsSubMeshView)
			{
				data.Channel = 10;
			}

			// PBR factors are per material (glTF metallic/roughness/baseColor), so the push walks
			// key-value pairs. Use the preview's OWN material set, not the primary one: pushing
			// into a shared model's primary set would clobber the prefab scene's Lighting settings.
			var materials = OwnMaterials!;
			for (int i = 0; i < materials.Count; i++)
			{
				var kvp = materials.GetAt(i);

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
				data.Emissive = pbr.EmissiveFactor;
				data.AlphaBlend = pbr.IsSoftBlend ? 1 : 0;

				kvp.Value.SetConstant("PreviewSettings", ref data, HandleAccess.Pixel);
			}
		}

		// --- Probe BVH debug view -------------------------------------------------------------------

		/// <summary>Wireframe box entities for BVH nodes (see <see cref="PopulateBvhDebugOverlay"/>).</summary>
		private readonly List<Entity> _bvhDebugEntities = new();

		/// <summary>Unit cube for debug boxes, drawn with the sub-mesh wireframe material; one mesh for the whole overlay, boxes differ only by instance Position/Scale3.</summary>
		private MeshId? _bvhDebugMeshId;
		private BatchId? _bvhDebugBatchId;

		/// <summary>Settings snapshot the current overlay was built for; boxes rebuild only on a real change (tree walk is not free).</summary>
		private (bool On, int Depth, bool Leaves, object? Baker) _bvhDebugState;

		/// <summary>Keeps the BVH debug overlay in sync with settings and the current tree; called every frame from Update, all work happens on change only.</summary>
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
		/// Creates one wireframe box per BVH node (see <see cref="ProbeGiBaker.CollectDebugBoxes"/>).
		/// Instances are ordinary Position/Rotation/Scale3 + RenderResourceManager registrations,
		/// so culling and instancing work as-is.
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

			// Cap guards against "show all Sponza leaves" (hundreds of thousands of boxes kill
			// both the instance buffers and the eye); truncation is reported to the console.
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

				// Same barrier + graph rebuild as the other overlays: ForwardPass commands are
				// frozen and we just added a batch/instances (see ClearWireframeOverlay).
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

		/// <summary>Unit cube (centered at origin, side 1) - the debug box geometry.</summary>
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

			// Cube triangles; the wireframe PSO state (see GetWireframeState) renders them as lines.
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

		/// <summary>Drops the GPU side of the debug overlay; call where batch-renderer registrations
		/// are reset (model change, environment recreation) - MeshId/BatchId are reissued from zero
		/// after a reset and stale ones would point into foreign geometry. Caller has already waited
		/// for the GPU. Overlay entities must be removed by the caller BEFORE the reset, while their
		/// BatchId is still valid (see ClearBvhDebugOverlay): live entities left in the store would
		/// render the NEW model's geometry at the old box positions.</summary>
		private void ReleaseBvhDebugResources()
		{
			if (_bvhDebugEntities.Count > 0)
			{
				// Safety net for a new code path that forgot to remove entities: without
				// Unregister the instance slots leak, but no garbage geometry is drawn.
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

			// Without a graph rebuild the removed instances would keep drawing (same reason as
			// ClearWireframeOverlay).
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_env.Pipeline.InvalidateGraph();
		}

		/// <summary>Lazily creates the shared wireframe-overlay material/PSO - one instance for every mesh this viewport draws in wireframe (flat color, no per-material state).</summary>
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
		/// Adds one wireframe instance per instance of the isolated sub-mesh, reusing one wireframe
		/// batch per mesh - mirrors ModelViewportGeometry.CreateInstanceEntity but against the
		/// shared wireframe material, since the overlay is the same flat color for any glTF material.
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

			// A new wireframe batch/material may have just been registered; the render graph's
			// frozen ForwardPass commands must be recompiled to pick it up.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_env.Pipeline.InvalidateGraph();
		}

		/// <summary>Resets sub-mesh view mode/channel/wireframe to defaults when a genuinely new model or sub-mesh is populated - choices made for one sub-mesh must not carry over to an unrelated one.</summary>
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

			// Without this the overlay never disappears: ForwardPass commands are only re-recorded
			// on render-graph recompile, and CheckAndReallocateBuffers (which re-uploads the
			// instance array so culling skips freed slots) only runs inside that recording. The
			// compute dispatch replays every frame against the stale GPU instance buffer otherwise.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();
			_env.Pipeline.InvalidateGraph();
		}

	}
}
