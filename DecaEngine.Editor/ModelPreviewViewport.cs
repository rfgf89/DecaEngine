using System.Numerics;
using System.Threading;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor
{
	/// <summary>
	/// ????????? (????????????? ?? ??????? ????? / Game View) ????????? ??????-?????: ????
	/// EntityStore, DiligentBatchRenderer, GraphicsPipeline ? off-screen color/depth render-???????.
	/// ???????????? <see cref="InspectorWindow"/> ??? 3D-?????? .gltf/.glb ???????, ????????? ?
	/// <see cref="AssetBrowserWindow"/> - ?????? ??????????? ????? <see cref="ModelLoader"/> ? ????
	/// EntityStore (????? ?? ???????????? ?? ? ??????? ??????, ?? ? EntityStore-?? ????????
	/// Inspector-?), ? ?????? ???? ?????????? ? ??????????? offscreen-????????, ??????? ?????
	/// ???????????? ????? ImGui.Image (?????????? ????, ??? <see cref="GameViewWindow"/>
	/// ?????????? ??????? ????? ????? ???? ??????????? IRenderHandle).
	/// </summary>
	public class ModelPreviewViewport
	{
		/// <summary>Sub-mesh view mode, selectable from the Inspector while a single sub-mesh is isolated
		/// (see <see cref="InspectorWindow.RenderModelPreview"/>). Irrelevant for the whole-model view,
		/// which is always rendered Textured (see <see cref="ApplyPreviewSettingsToMaterials"/>). Orthogonal
		/// to <see cref="WireframeEnabled"/> - the wireframe overlay can be toggled on top of either mode.</summary>
		public enum SubMeshPreviewMode
		{
			Highlight,
			Channel,
		}

		/// <summary>Debug channel visualized in <see cref="SubMeshPreviewMode.Channel"/>.</summary>
		public enum PreviewChannel
		{
			Normal,
			Uv,
			Tangent,
		}

		private const uint InitialWidth = 256;
		private const uint InitialHeight = 256;
		private const float CameraFovDegrees = ModelViewportEnvironment.CameraFovDegrees;

		/// <summary>
		/// How long the requested ImGui image size must stay unchanged before the off-screen targets
		/// are actually resized - resizing recreates GPU resources (see <see cref="ResizeTargets"/>),
		/// so applying it every frame while the user is still dragging the window edge would mean a
		/// GPU stall (<see cref="Diligent.IDeviceContext.WaitForIdle"/>) on every single frame of the
		/// drag instead of once after they let go.
		/// </summary>
		private const float ResizeSettleSeconds = 0.3f;

		private readonly IGraphicsApi _graphicsApi;
		private readonly EditorSettings _editorSettings;
		private readonly ModelViewportEnvironment _env;

		private readonly List<Entity> _instanceEntities = new();

		private string? _loadedPath;
		private int _loadedSubMesh = -1;
		private string? _loadError;
		private string? _loadingPath;
		private int _loadingSubMesh = -1;
		private ModelLoader.ModelLoadRequest? _loadRequest;
		private EditorLoadingStatus.Handle? _loadHandle;
		private CancellationTokenSource? _loadCts;

		// Резидентная модель: тот же .gltf/.glb, что уже полностью распарсен и зарегистрирован в
		// _env.BatchRenderer с предыдущего LoadModel - переключение сабмеша той же модели (см.
		// LoadModel) должно просто перенаселить сцену данными, уже сидящими в памяти/на GPU, а не
		// заново читать файл с диска и гонять прогресс-бар (см. ModelIconBaker, тот же приём).
		private string? _residentPath;
		private ModelLoader? _residentModel;
		private readonly Dictionary<int, MeshId> _meshIdMap = new();
		private readonly Dictionary<int, MaterialId> _materialIdMap = new();
		private readonly Dictionary<(int, int), BatchId> _batchCache = new();

		// Wireframe overlay toggle (see WireframeEnabled/SetWireframeEnabled): a second material
		// (wireframe-filled PSO, see DiligentBatchRenderer.GetWireframeState) drawing the exact same
		// geometry as the currently isolated sub-mesh's instances, added/removed on top of
		// _instanceEntities independently of SubMeshPreviewMode - the batch renderer has no notion of
		// "redraw this batch again with a different PSO", so a second material/batch/instance set is how
		// a second draw pass over the same geometry happens here (see
		// ModelViewportGeometry.CreateInstanceEntity for the pattern this mirrors).
		private IMaterialObject? _wireframeMaterial;
		private MaterialId? _wireframeMaterialId;
		private readonly Dictionary<int, BatchId> _wireframeBatchCache = new();
		private readonly List<Entity> _wireframeEntities = new();
		private bool _wireframeEnabled;

		private SubMeshPreviewMode _viewMode = SubMeshPreviewMode.Highlight;
		private PreviewChannel _previewChannel = PreviewChannel.Normal;

		private Vector3 _orbitTarget = Vector3.Zero;
		private float _yaw = -0.6f;
		private float _pitch = 0.35f;
		private float _distance = 4f;
		private bool _orbiting;
		private bool _panning;

		private ImTextureRef _textureRef;
		private bool _textureBound;

		private Vector2 _pendingSize;
		private float _resizeIdleSeconds;

		/// <summary>?????????? ???? ? ????????? ??????? ??????????? ??????, ???? null.</summary>
		public string? LoadedPath => _loadedPath;

		/// <summary>????????? ?? ?????? ????????? ??????? ????????, ???? null ???? ??? ??????.</summary>
		public string? LoadError => _loadError;

		public bool HasModel => _instanceEntities.Count > 0;

		/// <summary>True while a single sub-mesh (rather than the whole model) is isolated - only then
		/// is <see cref="ViewMode"/>/<see cref="Channel"/> meaningful (see <see cref="InspectorWindow"/>).</summary>
		public bool IsSubMeshView => _loadedSubMesh >= 0;

		/// <summary>Current sub-mesh view mode - see <see cref="SetSubMeshViewMode"/>.</summary>
		public SubMeshPreviewMode ViewMode => _viewMode;

		/// <summary>Whether the wireframe overlay is currently on - see <see cref="SetWireframeEnabled"/>.
		/// Orthogonal to <see cref="ViewMode"/>: can be toggled on top of either Highlight or Channel.</summary>
		public bool WireframeEnabled => _wireframeEnabled;

		/// <summary>Current Channel-mode debug channel - see <see cref="SetPreviewChannel"/>.</summary>
		public PreviewChannel Channel => _previewChannel;

		/// <summary>Whether the currently isolated sub-mesh has real UV data, i.e. whether
		/// <see cref="PreviewChannel.Tangent"/> (derived from UV derivatives) is meaningful for it.</summary>
		public bool CurrentSubMeshHasUv =>
			_loadedSubMesh >= 0 && _residentModel != null &&
			_loadedSubMesh < _residentModel.MeshHasUv.Count && _residentModel.MeshHasUv[_loadedSubMesh];

		public ModelPreviewViewport(IGraphicsApi graphicsApi, EditorSettings editorSettings)
		{
			_graphicsApi = graphicsApi;
			_editorSettings = editorSettings;

			_env = new ModelViewportEnvironment(graphicsApi, InitialWidth, InitialHeight,
				"Model Preview Color", "Model Preview Depth");
		}

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
		/// cbuffer). The whole-model view (<see cref="IsSubMeshView"/> false) always maps to Textured
		/// (Mode 0) regardless of <see cref="ViewMode"/> - that combo is only shown/meaningful for
		/// sub-mesh view (see <see cref="InspectorWindow.RenderModelPreview"/>).
		/// </summary>
		private void ApplyPreviewSettingsToMaterials()
		{
			if (_residentModel == null)
			{
				return;
			}

			int mode = !IsSubMeshView ? 0 : (_viewMode == SubMeshPreviewMode.Channel ? 2 : 1);

			var data = new PreviewSettingsData { Mode = mode, Channel = (int)_previewChannel };

			foreach (var material in _residentModel.materialObjects.Values)
			{
				material.SetConstant("PreviewSettings", ref data, HandleAccess.Pixel);
			}
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

		/// <summary>
		/// ????????? .gltf/.glb ?????? ?? ?????????? ???? ? ??????????? EntityStore ????? ??????.
		/// ?? ?????? ??????, ???? ???? ????????? ? ??? ???????????. ?????? ???????? (????? ????,
		/// ?? ?????? ? ?.?.) ?? ????????? ?????? - ??. <see cref="LoadError"/>.
		/// </summary>
		public void LoadModel(string modelPath, int subMeshIndex = -1)
		{
			// Ключ загрузки - пара (путь, сабмеш): та же модель с другим выбранным сабмешем
			// должна перезагрузиться (точнее, перенаселить сцену только этим сабмешем).
			if ((string.Equals(_loadedPath, modelPath, StringComparison.OrdinalIgnoreCase) && _loadedSubMesh == subMeshIndex) ||
			    (string.Equals(_loadingPath, modelPath, StringComparison.OrdinalIgnoreCase) && _loadingSubMesh == subMeshIndex))
			{
				return;
			}

			// Та же модель, что уже резидентна с предыдущего вызова (просто другой сабмеш выбран) -
			// файл уже распарсен и его меши/материалы уже зарегистрированы в _env.BatchRenderer, так
			// что достаточно перенаселить сцену, без диска, фоновой задачи и лоадер-хендла.
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

					_loadedPath = modelPath;
					_loadedSubMesh = subMeshIndex;
					_loadError = null;
					ApplyPreviewSettingsToMaterials();
				}
				catch (Exception ex)
				{
					_loadedPath = null;
					_loadError = ex.Message;
					EditorConsoleLog.Add(LogLevel.Error, $"Model preview: failed to switch sub-mesh for '{modelPath}': {ex.Message}");
				}

				return;
			}

			CancelPendingLoad();

			EditorConsoleLog.Add(LogLevel.Warning,
				$"Model preview: FULL reload for '{modelPath}' subMesh={subMeshIndex} " +
				$"(resident was '{_residentPath}', model={(_residentModel is null ? "null" : "loaded")}) - " +
				"resident path did not match, re-parsing from disk instead of reusing it.");

			var cts = new CancellationTokenSource();

			try
			{
				_loadRequest = ModelLoader.BeginLoadAsync(_graphicsApi, modelPath, new ModelLoadOptions
				{
					VertexShader = _editorSettings.DefaultVertexShader,
					PixelShader = _editorSettings.DefaultPixelShader,
					OptimizeMesh = false,
					GenerateLods = false
				}, cancellationToken: cts.Token);
				_loadCts = cts;
				_loadingPath = modelPath;
				_loadingSubMesh = subMeshIndex;
				_loadHandle = EditorLoadingStatus.Begin($"Loading preview: {Path.GetFileName(modelPath)}");
				_loadError = null;
			}
			catch (Exception ex)
			{
				_loadedPath = null;
				_loadError = ex.Message;
				EditorConsoleLog.Add(LogLevel.Error, $"Model preview: failed to load '{modelPath}': {ex.Message}");
			}
		}

		/// <summary>
		/// Cancels and releases the in-flight background load, if any - the background Task.Run in
		/// ModelLoader.PrepareModel checks the token between phases, so this actually stops it from
		/// continuing to burn CPU decoding textures for a model/sub-mesh selection the user has already
		/// moved on from, instead of just forgetting the reference and letting it run to completion
		/// unobserved.
		/// </summary>
		private void CancelPendingLoad()
		{
			if (_loadCts != null)
			{
				_loadCts.Cancel();
				_loadCts.Dispose();
				_loadCts = null;
			}

			if (_loadHandle != null)
			{
				EditorLoadingStatus.End(_loadHandle);
				_loadHandle = null;
			}

			_loadRequest = null;
			_loadingPath = null;
			_loadingSubMesh = -1;
		}

		private void PollPendingLoad()
		{
			if (_loadRequest == null)
			{
				return;
			}

			_loadHandle!.Progress = _loadRequest.Progress;

			if (!_loadRequest.PrepareTask.IsCompleted)
			{
				return;
			}

			var request = _loadRequest;
			var modelPath = _loadingPath!;
			var subMeshIndex = _loadingSubMesh;
			EditorLoadingStatus.End(_loadHandle);
			_loadHandle = null;
			_loadRequest = null;
			_loadingPath = null;
			_loadingSubMesh = -1;
			_loadCts?.Dispose();
			_loadCts = null;

			if (request.PrepareTask.IsCompletedSuccessfully)
			{
				ClearInstances();

				try
				{
					ResetPreviewModeForNewSelection();
					var scene = request.FinalizeOnMainThread();
					PopulateFromScene(scene, subMeshIndex);

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

					_loadedPath = modelPath;
					_loadedSubMesh = subMeshIndex;
					_residentPath = modelPath;
					_loadError = null;
					ApplyPreviewSettingsToMaterials();
				}
				catch (Exception ex)
				{
					_loadedPath = null;
					_loadError = ex.Message;
					EditorConsoleLog.Add(LogLevel.Error, $"Model preview: failed to load '{modelPath}': {ex.Message}");
				}
			}
			else
			{
				var message = request.PrepareTask.Exception?.GetBaseException().Message ?? "Unknown error";
				_loadedPath = null;
				_loadError = message;
				EditorConsoleLog.Add(LogLevel.Error, $"Model preview: failed to load '{modelPath}': {message}");
			}
		}

		/// <summary>
		/// subMeshIndex &gt;= 0 - показываем только инстансы этого сабмеша, иначе всю модель. Сабмеш
		/// без единого инстанса (неиспользуемый меш в glTF) остаётся пустым - HasModel вернёт false
		/// и Render покажет "No model loaded" вместо синтетического инстанса.
		/// </summary>
		private void PopulateFromScene(ModelLoader modelLoader, int subMeshIndex = -1)
		{
			// Регистрируем меши/материалы в _env.BatchRenderer только для реально нового файла - для
			// уже резидентной модели (переключение сабмеша, см. LoadModel) _meshIdMap/_materialIdMap
			// уже заполнены с предыдущего вызова, повторная регистрация тех же ресурсов только бы
			// зря плодила GPU-объекты.
			if (!ReferenceEquals(modelLoader, _residentModel))
			{
				_residentModel = modelLoader;
				_meshIdMap.Clear();
				_materialIdMap.Clear();
				_batchCache.Clear();
				ModelViewportGeometry.RegisterModelResources(_env.BatchRenderer, modelLoader, _meshIdMap, _materialIdMap);
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

			// Framing must be based on the actual MESH geometry bounds of ALL sub-meshes/instances
			// (Scene.ComputeBounds, using Scene.Meshes[i].Center/Radius, computed by
			// MeshUtility.RecalculateBounds when the model was loaded, see Scene.cs) - a model
			// almost always consists of multiple sub-meshes/nodes, so a single mesh's bound is not
			// enough on its own; a mesh whose geometry is offset from its local origin (very common
			// for glTF nodes) would otherwise make the orbit target sit next to the model instead of
			// at its actual visual center, so the camera would circle some empty point beside it
			// rather than fully around it.

			// Для одиночного сабмеша считаем bounds только по ЕГО инстансам (аналог
			// ModelLoader.ComputeBounds, но с фильтром) - иначе камера кадрировала бы всю модель,
			// а маленький сабмеш где-нибудь с краю был бы едва различим.
			Vector3 boundsMin, boundsMax;
			if (subMeshIndex < 0)
			{
				(boundsMin, boundsMax) = modelLoader.ComputeBounds();
			}
			else
			{
				(boundsMin, boundsMax) = ModelViewportGeometry.ComputeSubMeshBounds(modelLoader, subMeshIndex);
			}

			// ??????????? ??? ??????? ??????? ? ???????
			EditorConsoleLog.Add(LogLevel.Info,
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
		}

		private void FrameAll(Vector3 min, Vector3 max)
		{
			// ????????? bounds - ???? ??? ???????? NaN ??? Infinity, ?????????? ?????????? ????????
			if (float.IsNaN(min.X) || float.IsNaN(min.Y) || float.IsNaN(min.Z) ||
			    float.IsNaN(max.X) || float.IsNaN(max.Y) || float.IsNaN(max.Z) ||
			    float.IsInfinity(min.X) || float.IsInfinity(min.Y) || float.IsInfinity(min.Z) ||
			    float.IsInfinity(max.X) || float.IsInfinity(max.Y) || float.IsInfinity(max.Z))
			{
				// Если bounds некорректны, используем значения по умолчанию
				_orbitTarget = Vector3.Zero;
				_distance = 4f;
				_yaw = -0.6f;
				_pitch = 0.35f;
				return;
			}

			_orbitTarget = (min + max) * 0.5f;

			// Half-diagonal of the (mesh-bounds-based, see PopulateFromScene) AABB, used as a
			// bounding-sphere radius around _orbitTarget - simple and good enough for auto-framing.
			var radius = MathF.Max(0.05f, (max - min).Length() * 0.5f);

			// Distance at which a sphere of this radius exactly fills the vertical FOV, plus a
			// small margin so the model isn't touching the viewport edges.
			_distance = ModelViewportGeometry.ComputeFramingDistance(radius, CameraFovDegrees);

			_yaw = -0.6f;
			_pitch = 0.35f;
		}

		/// <summary>
		/// ?????????? ??????????? (?????????) ECS/render-graph ?????? ?? ???? ????. ??????
		/// ?????????? ??? ? ???? ????????? (??. EditorManager.OnUpdate) ??? ??? ?? GPU-?????, ??? ?
		/// ???????? ?????, ????????? ?????????? ????? IGraphicsApi/??????????.
		/// </summary>
		public void Update(float deltaTime, float time)
		{
			PollPendingLoad();

			if (!HasModel)
			{
				return;
			}

			try
			{
				var eye = ModelViewportGeometry.ComputeOrbitEye(_orbitTarget, _distance, _yaw, _pitch);
				_env.SetCameraTransform(eye, _orbitTarget);

				_env.Root.Update(new UpdateTick(deltaTime, time));
				_env.Pipeline.Execute();
			}
			catch (Exception ex)
			{
				// This runs every frame while a model is loaded (unlike the one-time load path in
				// PollPendingLoad, which already has its own try/catch) - EditorManager.OnUpdate calls
				// this BEFORE the main scene's _pipeline.Execute()/Present() inside the same GPU lock
				// (see the ordering comment there), so an exception escaping here would skip Present()
				// for this frame and, since the model stays loaded, do so again on every frame after -
				// i.e. the editor would appear to freeze/stop presenting entirely instead of just
				// losing this one preview.
				_loadError = ex.Message;
				EditorConsoleLog.Add(LogLevel.Error, $"Model preview: render failed for '{_loadedPath}': {ex.Message}");
				ClearInstances();
			}
		}

		/// <summary>
		/// ?????? ImGui.Image ?????? ? ???????????? orbit/pan/zoom ???? ???? ??? ??? (??????????
		/// <see cref="PrefabSceneViewport"/>). ?????? ?????????? ?? ??????????? ImGui-????, ???????
		/// ??? ?????????? (??. InspectorWindow.RenderModelPreview).
		/// </summary>
		public void Render(ImGuiRender imGuiRender, Vector2 size)
		{
			if (size.X <= 1f || size.Y <= 1f)
			{
				return;
			}

			if (!_textureBound)
			{
				_textureRef = imGuiRender.GetNewTexture();
				_textureBound = true;
			}

			bool resized = TrackAndApplyResize(imGuiRender, size);

			if (resized)
			{
				// Resizing recreates the underlying GPU texture (see DiligentRenderTarget.Resize), so
				// the shader resource binding ImGui captured at bind time now points at a disposed
				// texture - rebind onto the same ImTextureID rather than allocating a new one each time,
				// which would otherwise leak an entry in ImGuiDiligentRender's texture table per resize.
				imGuiRender.BindRenderTarget(_textureRef.GetTexID(), _env.ColorTarget);
			}

			var cursor = ImGui.GetCursorScreenPos();
			ImGui.Image(_textureRef, size);

			bool hovered = ImGui.IsItemHovered();
			HandleCameraInput(hovered);

			if (!HasModel)
			{
				var drawList = ImGui.GetWindowDrawList();
				var text = _loadError ?? "No model loaded";
				var textSize = ImGui.CalcTextSize(text);
				var textPos = cursor + (size - textSize) * 0.5f;
				drawList.AddText(textPos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f)), text);
			}
		}

		/// <summary>
		/// Debounces <see cref="ResizeTargets"/>: only applies once the requested ImGui image size has
		/// stayed unchanged for <see cref="ResizeSettleSeconds"/>, i.e. once the user has finished
		/// resizing the window/panel rather than on every frame while they're still dragging it.
		/// </summary>
		private bool TrackAndApplyResize(ImGuiRender imGuiRender, Vector2 imGuiSize)
		{
			var width = (uint)Math.Max(1, MathF.Round(imGuiSize.X));
			var height = (uint)Math.Max(1, MathF.Round(imGuiSize.Y));
			var requestedSize = new Vector2(width, height);

			if (requestedSize != _pendingSize)
			{
				_pendingSize = requestedSize;
				_resizeIdleSeconds = 0f;
				return false;
			}

			if (requestedSize == _env.ColorTarget.Size)
			{
				return false;
			}

			_resizeIdleSeconds += ImGui.GetIO().DeltaTime;
			if (_resizeIdleSeconds < ResizeSettleSeconds)
			{
				return false;
			}

			return ResizeTargets(imGuiRender, requestedSize);
		}

		/// <summary>
		/// Resizes the off-screen color/depth targets and camera viewport to match the given size so
		/// the preview renders at native resolution instead of a fixed one.
		/// </summary>
		private bool ResizeTargets(ImGuiRender imGuiRender, Vector2 newSize)
		{
			var width = (uint)newSize.X;
			var height = (uint)newSize.Y;

			// Resize disposes and recreates the underlying GPU texture (see
			// DiligentRenderTarget.Resize) - without waiting for any in-flight GPU work that still
			// reads/writes the old texture (this engine currently has no frame-in-flight fence, see
			// DiligentGraphicsApi.Present) to finish first, disposing it here races the GPU and can
			// crash the driver with an access violation. Flush() must precede WaitForIdle(): otherwise
			// commands recorded on the immediate context but not yet submitted are still pending when
			// WaitForIdle() returns (see the same Flush()+WaitForIdle() pairing in
			// DiligentGraphicsUtility's buffer readback), so the old texture could still be disposed out
			// from under work the GPU hasn't actually started yet.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			// Must happen before Resize() disposes the color target's texture/views below: the cached
			// ImGui shader-resource binding for this texture id holds a reference to a view of the
			// CURRENT (about to be stale) texture, and releasing that binding after the view is gone
			// crashes instead of cleanly releasing it (see ImGuiRender.ReleaseRenderTargetBinding).
			imGuiRender.ReleaseRenderTargetBinding(_textureRef.GetTexID());

			_env.ColorTarget.Resize(newSize);
			_env.DepthTarget.Resize(newSize);

			// Must happen immediately after Resize(), before any code below that could throw (e.g.
			// GetComponent/RecalculateProjection) - Resize() already disposed the old GPU
			// texture/views, so if Invalidate() were skipped the render graph would keep replaying
			// its frozen command buffer, which still references those disposed views, on every
			// subsequent frame until something else happens to invalidate it.
			_env.Pipeline.SetOffscreenViewportSize(newSize);

			ref var cameraComponent = ref _env.CameraEntity.GetComponent<CameraComponent>();
			cameraComponent.data.viewport = new Vector4(0, 0, width, height);
			cameraComponent.data.aspect = width / (float)height;
			cameraComponent.RecalculateProjection();

			return true;
		}

		private void HandleCameraInput(bool hovered)
		{
			var io = ImGui.GetIO();

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
			{
				_orbiting = true;
			}
			if (_orbiting && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
			{
				_orbiting = false;
			}

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
			{
				_panning = true;
			}
			if (_panning && ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
			{
				_panning = false;
			}

			if (_orbiting)
			{
				var delta = io.MouseDelta;
				_yaw -= delta.X * 0.01f;
				_pitch = Math.Clamp(_pitch - delta.Y * 0.01f, -1.5f, 1.5f);
			}
			else if (_panning)
			{
				var delta = io.MouseDelta;
				var right = new Vector3(MathF.Cos(_yaw), 0f, -MathF.Sin(_yaw));
				var panScale = MathF.Max(0.01f, _distance * 0.001f);
				_orbitTarget -= right * delta.X * panScale;
				_orbitTarget += Vector3.UnitY * delta.Y * panScale;
			}

			if (hovered && io.MouseWheel != 0f)
			{
				_distance = Math.Clamp(_distance + io.MouseWheel * _distance * 0.1f, 0.2f, 1500f);
			}
		}
	}
}



