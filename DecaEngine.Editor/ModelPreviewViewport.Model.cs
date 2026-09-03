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
	/// <summary>Model loading: store claim, streaming, instances, RT shadow scene.</summary>
	public partial class ModelPreviewViewport
	{
		/// <summary>Loads a model for preview; errors surface through <see cref="LoadError"/>.</summary>
		public void LoadModel(string modelPath, int subMeshIndex = -1)
		{
			// The load key is (path, sub-mesh): the same file with another sub-mesh must repopulate.
			if ((string.Equals(_loadedPath, modelPath, StringComparison.OrdinalIgnoreCase) && _loadedSubMesh == subMeshIndex) ||
			    (string.Equals(_loadingPath, modelPath, StringComparison.OrdinalIgnoreCase) && _loadingSubMesh == subMeshIndex))
			{
				return;
			}

			// Already resident: meshes/materials are registered, so just repopulate the scene.
			if (_residentModel != null && string.Equals(_residentPath, modelPath, StringComparison.OrdinalIgnoreCase))
			{
				CancelPendingLoad();
				ClearInstances();

				try
				{
					ResetPreviewModeForNewSelection();
					PopulateFromScene(_residentModel, subMeshIndex);

					// See the matching comment in PollPendingLoad below - new batches were just
					// registered for this sub-mesh selection and the render graph must be recompiled
					// to pick them up.
					_env.DilApi.ImmediateContext.Flush();
					_env.DilApi.ImmediateContext.WaitForIdle();
					_env.Pipeline.InvalidateGraph();

					// AO/GI world range only after the barrier above, as in PollPendingLoad.
					_env.SetAoWorldRange(AoWorldRange());
					_env.SetAoStrength(Math.Clamp(_editorSettings.AoStrength, 0.1f, 4f),
						Math.Clamp(_editorSettings.AoFloor, 0f, 1f));
					_env.SetAoDebugView(_editorSettings.AoDebugView);
					ApplyGiSettings(pushRange: true);

					_loadedPath = modelPath;
					_loadedSubMesh = subMeshIndex;
					_loadError = null;
					ApplyPreviewSettingsToMaterials();
				}
				catch (Exception ex)
				{
					_loadedPath = null;
					_loadError = ex.Message;
					EngineLog.Add(LogLevel.Error, $"Model preview: failed to switch sub-mesh for '{modelPath}': {ex.Message}");
				}

				return;
			}

			CancelPendingLoad();

			// Resuming from pause has no resident by design, so it is not a lost-cache warning.
			if (!_restoringAfterResume)
			{
				EngineLog.Add(LogLevel.Warning,
					$"Model preview: FULL reload for '{modelPath}' subMesh={subMeshIndex} " +
					$"(resident was '{_residentPath}', model={(_residentModel is null ? "null" : "loaded")}) - " +
					"resident path did not match, re-parsing from disk instead of reusing it.");
			}

			UnloadResidentModel();

			// The load starts next frame from ModelStreamingSystem; PollPendingLoad polls it.
			_streamingModel = _streamer.Acquire(modelPath, _orbitTarget);
			_loadingPath = modelPath;
			_loadingSubMesh = subMeshIndex;
			_loadError = null;
		}

		// Drops the preview model from the GPU entirely. Call ONLY under the editor GPU lock:
		// contains Flush/WaitForIdle.
		private void UnloadResidentModel()
		{
			ClearInstances();

			// The background probe BVH build may still be reading the old geometry ClearAll frees.
			WaitProbeBakerTask();

			if (_streamingModel != null)
			{
				_streamer.Release(_streamingModel);
				_streamingModel = null;
			}

			_streamer.ClearAll();
			_rtShadowScene?.Release();
			_rtShadowScene = null;
			_residentModel = null;
			_residentPath = null;
			_meshIdMap.Clear();
			_materialIdMap.Clear();
			_batchCache.Clear();
			// The wireframe material's registration died with ClearAll; recreated lazily later.
			_wireframeMaterial?.Release();
			_wireframeMaterial = null;
			_wireframeMaterialId = null;
			_wireframeBatchCache.Clear();

			// The BVH debug cube's MeshId/BatchId died with the same registration reset.
			ReleaseBvhDebugResources();

			_loadedPath = null;
			_loadedSubMesh = -1;

			ResetProbeGi();
		}

		// A factory, not a snapshot: MaxTextureSize/anisotropy change between loads.
		private ModelLoadOptions BuildLoadOptions() =>
			ViewportSettingsPush.BuildLoadOptions(_editorSettings, RtShadowsEnabled());

		// Call only after a barrier (Flush + WaitForIdle). No-op outside "Ray-traced" mode.
		private void UpdateRtShadowScene()
		{
			_rtShadowScene?.Release();
			_rtShadowScene = null;

			if (!RtShadowsEnabled() || _residentModel == null)
			{
				return;
			}

			var instances = new List<DiligentRayTracingScene.Instance>();
			foreach (var instance in _residentModel.instances)
			{
				if (instance.meshId < 0 || instance.meshId >= _residentModel.Meshes.Count ||
					_residentModel.Meshes[instance.meshId] is not DiligentMesh mesh ||
					mesh.IndexCount < 3 || mesh.VertexBuffer == null || mesh.IndexBuffer == null)
				{
					continue;
				}

				var t = instance.transform;
				instances.Add(new DiligentRayTracingScene.Instance(mesh,
					Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
					Matrix4x4.CreateFromQuaternion(t.rotation) *
					Matrix4x4.CreateTranslation(t.position),
					(uint)instances.Count));
			}

			if (instances.Count == 0)
			{
				return;
			}

			_rtShadowScene = new DiligentRayTracingScene(_env.DilApi);
			_rtShadowScene.Rebuild(instances);
			if (_rtShadowScene.Tlas == null)
			{
				return;
			}

			// Bind into this environment's own material set: another set's TLAS is another scene.
			var tlasTargets = OwnMaterials!;
			for (int i = 0; i < tlasTargets.Count; i++)
			{
				if (tlasTargets.GetAt(i).Value is DiligentMaterial material)
				{
					material.SetAccelStructure("_SceneTlas", _rtShadowScene.Tlas);
				}
			}
		}

		// Compare against the clamped value: raw settings outside [128, 8192] would always differ.
		private int ClampedMaxTextureSize() => ViewportSettingsPush.ClampedMaxTextureSize(_editorSettings);

		// Cancels the in-flight background load; PrepareModel checks the token between phases.
		private void CancelPendingLoad()
		{
			// Release only while still loading: once ready, _streamingModel is the reference that
			// keeps the resident model from being evicted, and sub-mesh switching must not lose it.
			if (_loadingPath != null && _streamingModel != null)
			{
				_streamer.Release(_streamingModel);
				_streamingModel = null;
			}

			_loadingPath = null;
			_loadingSubMesh = -1;
		}

		private void PollPendingLoad()
		{
			if (_streamingModel == null || _loadingPath == null)
			{
				return;
			}

			var state = _streamingModel;

			if (state.Failed)
			{
				var failedPath = _loadingPath;
				var message = state.Error!;
				CancelPendingLoad();
				_loadedPath = null;
				_loadError = message;
				EngineLog.Add(LogLevel.Error, $"Model preview: failed to load '{failedPath}': {message}");
				return;
			}

			if (!state.Ready)
			{
				return;
			}

			var modelPath = _loadingPath;
			var subMeshIndex = _loadingSubMesh;
			_loadingPath = null;
			_loadingSubMesh = -1;

			ClearInstances();

			try
			{
				ResetPreviewModeForNewSelection();

				// Mark the model resident BEFORE populating: PopulateFromScene checks by reference.
				_residentModel = state.Model;
				_residentPath = modelPath;
				_meshIdMap.Clear();
				_materialIdMap.Clear();
				_batchCache.Clear();
				foreach (var kvp in state.MeshIds)
				{
					_meshIdMap[kvp.Key] = kvp.Value;
				}
				foreach (var kvp in state.MaterialIds)
				{
					_materialIdMap[kvp.Key] = kvp.Value;
				}

				PopulateFromScene(state.Model!, subMeshIndex);

				// New batches were just registered in _batchRenderer for this model/sub-mesh, but
				// the render graph's ForwardPass commands are frozen after the first Compile() and
				// merely replayed on every Execute() (see IRenderGraph.Invalidate) - without this,
				// switching model/sub-mesh keeps drawing whatever batch set existed when the graph
				// was first compiled instead of the newly loaded one.
				//
				// Recompiling disposes and recreates every native resource the graph pinned (e.g.
				// ShadowPass's shadow maps) - same hazard as ResizeTargets below: with no
				// frame-in-flight fence in this engine, disposing GPU resources the previous frame's
				// (still in-flight) commands might reference races the GPU and can crash the driver.
				// Flush()+WaitForIdle() must run first, exactly as ResizeTargets does.
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_env.Pipeline.InvalidateGraph();

				// AO/GI world range only after the barrier: SetConstant touches ImmediateContext and
				// marks AoMaterial dirty, unsafe while the previous frame may be in flight.
				_env.SetAoWorldRange(AoWorldRange());
				_env.SetAoStrength(Math.Clamp(_editorSettings.AoStrength, 0.1f, 4f),
					Math.Clamp(_editorSettings.AoFloor, 0f, 1f));
				_env.SetAoDebugView(_editorSettings.AoDebugView);
				ApplyGiSettings(pushRange: true);

				_loadedPath = modelPath;
				_loadedSubMesh = subMeshIndex;
				_loadError = null;
				ApplyPreviewSettingsToMaterials();

				// After the barrier and strictly before the first draw: the FEATURE_RT_SHADOWS
				// variant declares _SceneTlas, and committing without a binding hits a null SRV.
				UpdateRtShadowScene();

				// RT SSR fallback builds its own accel from the model - refresh features here.
				if (_editorSettings.PreviewSsr && _editorSettings.SsrRayTraced)
				{
					ApplyPipelineFeatures();
				}

				ResetProbeGi();
				RequestProbeSession(delaySeconds: 0f);
			}
			catch (Exception ex)
			{
				_loadedPath = null;
				_loadError = ex.Message;
				EngineLog.Add(LogLevel.Error, $"Model preview: failed to load '{modelPath}': {ex.Message}");
			}
		}

		// subMeshIndex >= 0 shows only that sub-mesh's instances; a sub-mesh with none stays empty.
		private void PopulateFromScene(ModelLoader modelLoader, int subMeshIndex = -1)
		{
			// The model must already be resident: anything else violates the streaming lifecycle.
			if (!ReferenceEquals(modelLoader, _residentModel))
			{
				EngineLog.Add(LogLevel.Error,
					"Model preview: PopulateFromScene called with a non-resident model - streaming lifecycle violated, skipping.");
				return;
			}

			var instances = new List<InstanceData>(modelLoader.instances.Count);
			foreach (var candidate in modelLoader.instances)
			{
				if (subMeshIndex < 0 || candidate.meshId == subMeshIndex)
				{
					instances.Add(candidate);
				}
			}

			for (int i = 0; i < instances.Count; i++)
			{
				var instance = instances[i];
				var entity = ModelViewportGeometry.CreateInstanceEntity(_env.Store, _env.ResourceManager,
					_env.BatchRenderer, _meshIdMap, _materialIdMap, _batchCache,
					instance.meshId, instance.materialId, instance.transform);
				if (entity != null)
				{
					_instanceEntities.Add(entity.Value);
				}
			}

			// Framing uses the geometry bounds of all shown instances: glTF nodes are commonly
			// offset from their local origin, so a single mesh bound would orbit an empty point.
			Vector3 boundsMin, boundsMax;
			if (subMeshIndex < 0)
			{
				(boundsMin, boundsMax) = modelLoader.ComputeBounds();
			}
			else
			{
				(boundsMin, boundsMax) = ModelViewportGeometry.ComputeSubMeshBounds(modelLoader, subMeshIndex);
			}

			EngineLog.Add(LogLevel.Info,
				$"Model preview bounds: min={boundsMin}, max={boundsMax}, instances={_instanceEntities.Count}");

			FrameAll(boundsMin, boundsMax);
		}


		private void ClearInstances()
		{
			foreach (var entity in _instanceEntities)
			{
				_env.ResourceManager.UnregisterRenderable(entity);
				entity.DeleteEntity();
			}
			_instanceEntities.Clear();
			ClearWireframeOverlay();

			// BVH debug boxes are batch-renderer instances too: drop them while BatchIds are valid.
			ClearBvhDebugOverlay();
			_bvhDebugState = default;
		}

		// Must match the analytic sun's keyIntensity or the bounce will not match direct light.
		private Vector3 ProbeSunColor() => ViewportSettingsPush.ProbeSunColor(_editorSettings);

	}
}
