using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Core.Assets;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Engine.ImGui.Core;
using System.Threading.Tasks;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>Scene contents: ECS tree sync, model streaming, bounds, RT-shadow TLAS.</summary>
	public partial class PrefabSceneViewport
	{
		private void SyncScene(Entity root)
		{
			_visitedThisSync.Clear();
			_visitedLightsThisSync.Clear();
			bool structuralChange = false;
			bool boundsDirty = false;

			SyncEntity(root, ref structuralChange, ref boundsDirty);

			// Dropping light mirrors needs no graph rebuild: frozen commands re-read the light pool.
			_removeScratch.Clear();
			foreach (var kvp in _lightMirrors)
			{
				if (!_visitedLightsThisSync.Contains(kvp.Key))
				{
					_removeScratch.Add(kvp.Key);
				}
			}
			foreach (var id in _removeScratch)
			{
				if (!_lightMirrors[id].IsNull)
				{
					_lightMirrors[id].DeleteEntity();
				}
				_lightMirrors.Remove(id);
			}

			_removeScratch.Clear();
			foreach (var kvp in _rendered)
			{
				if (!_visitedThisSync.Contains(kvp.Key))
				{
					_removeScratch.Add(kvp.Key);
				}
			}
			foreach (var id in _removeScratch)
			{
				RemoveRecord(_rendered[id]);
				_rendered.Remove(id);
				structuralChange = true;
			}

			if (structuralChange)
			{
				// Graph commands are frozen after the first Compile; releasing/creating GPU
				// resources needs a barrier first.
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();

				// Grow capacity here, while the GPU is stopped: the _transformsDirty branch below
				// runs after command recording and assumes capacity never changes.
				_env.BatchRenderer.CheckAndReallocateBuffers();

				// Instance world matrices are produced later in the frame by GpuInstanceBufferSystem,
				// so uploading them here would push zeros; defer to the movement branch instead.
				_transformsDirty = true;

				// Zero-step pose evaluation: the character enters the driver here, after the frame's
				// UpdateAnimation already ran, so skinning would miss its first dispatch.
				UpdateAnimation(0f);

				_env.Pipeline.InvalidateGraph();

				// AO/SSGI knobs only after the barrier (SetConstant touches ImmediateContext).
				PushPostProcessRanges();
				ApplyMaterialSettings();

				// Also after the barrier: _SceneTlas must be bound before the new meshes' first draw.
				UpdateRtShadowScene();
				boundsDirty = true;

				_structuralDirtySelection = true;
				_physicsStaticsDirty = true;

				RequestProbeSession(0.3f);
			}

			if (boundsDirty)
			{
				UpdateShadowBounds();
				if (_framePending)
				{
					FrameAll();
				}
			}

			if (_transformsDirty)
			{
				// Requires acceleration structures too, not just a live GPU path: without a TLAS the
				// shader walks the baker BVH, which still holds the old poses - so rebake instead.
				if (_sceneGpu != null && _sceneAccel != null && !_sceneGpuDisabled)
				{
					_sceneTlasDirty = true;
				}
				else
				{
					RequestProbeSession(0.4f);
				}

				// RT-shadow TLAS follows poses on its own; top-level rebuild is cheap (BLAS cached).
				UpdateRtShadowScene();
			}
		}

		// Instance world pose = local TRS x record world matrix. No-op outside "Ray-traced" mode.
		private void UpdateRtShadowScene()
		{
			if (!RtShadowsEnabled())
			{
				return;
			}

			_rtShadowInstances.Clear();
			foreach (var record in _rendered.Values)
			{
				var model = record.Resident?.Model;
				if (model == null || !record.Instantiated)
				{
					continue;
				}

				foreach (var instance in model.instances)
				{
					if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count ||
						model.Meshes[instance.meshId] is not DiligentMesh mesh ||
						mesh.IndexCount < 3 || mesh.VertexBuffer == null || mesh.IndexBuffer == null)
					{
						continue;
					}

					var t = instance.transform;
					var local = Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
						Matrix4x4.CreateFromQuaternion(t.rotation) *
						Matrix4x4.CreateTranslation(t.position);
					_rtShadowInstances.Add(new DiligentRayTracingScene.Instance(mesh,
						local * record.LastWorld, (uint)_rtShadowInstances.Count));
				}
			}

			if (_rtShadowInstances.Count == 0)
			{
				return;
			}

			_rtShadowScene ??= new DiligentRayTracingScene(_env.DilApi);
			_rtShadowScene.Rebuild(_rtShadowInstances);
			if (_rtShadowScene.Tlas == null)
			{
				return;
			}

			// Idempotent: the descriptor points at the TLAS object itself.
			foreach (var kvp in ((DiligentBatchRenderer)_env.BatchRenderer).GetMaterials())
			{
				kvp.Value.SetAccelStructure("_SceneTlas", _rtShadowScene.Tlas);
			}
		}

		private void SyncEntity(Entity entity, ref bool structuralChange, ref bool boundsDirty)
		{
			if (entity.HasComponent<ModelRenderer>())
			{
				var assetPath = entity.GetComponent<ModelRenderer>().modelRef.Path ?? "";
				_visitedThisSync.Add(entity.Id);

				if (!_rendered.TryGetValue(entity.Id, out var record) ||
					!string.Equals(record.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase))
				{
					if (record != null)
					{
						RemoveRecord(record);
						structuralChange = true;
					}

					record = new RenderedModel { AssetPath = assetPath, EntityId = entity.Id };
					_rendered[entity.Id] = record;
				}

				if (record.ResolvedPath == null && assetPath.Length > 0)
				{
					record.ResolvedPath = ResolveAssetPath(assetPath);
					if (record.ResolvedPath == null)
					{
						EngineLog.Add(LogLevel.Warning, $"Prefab scene: asset not found: '{assetPath}'");
						// Empty marks "resolved to nothing" so the file is not searched every frame.
						record.ResolvedPath = "";
					}
				}

				if (!string.IsNullOrEmpty(record.ResolvedPath))
				{
					var world = ComputeWorldMatrix(entity);
					var anchor = world.Translation;

					// Camera-radius streaming decision; hysteresis lives in ShouldBeResident.
					if (record.Resident == null)
					{
						if (_streamer.ShouldBeResident(anchor, currentlyResident: false))
						{
							record.Resident = _streamer.Acquire(record.ResolvedPath, anchor);
						}
					}
					else
					{
						record.Resident.Anchor = anchor;

						if (!_streamer.ShouldBeResident(anchor, currentlyResident: true))
						{
							if (record.Instantiated)
							{
								RemoveRecord(record, releaseResident: false);
								structuralChange = true;
							}

							_streamer.Release(record.Resident);
							record.Resident = null;
						}
					}

					if (record.Resident is { } state)
					{
						if (!record.Instantiated && state.Ready)
						{
							InstantiateRecord(record, state, world);
							structuralChange = true;
						}
						else if (record.Instantiated && world != record.LastWorld)
						{
							UpdateRecordTransforms(record, state, world);
							boundsDirty = true;
							_transformsDirty = true;

							// Skinned models are not in the static set, and rebuilding it every frame
							// resets the floor mesh, so standing bodies lose contact impulses and fall.
							_physicsStaticsDirty |= state.Model?.Skeleton == null;
						}
					}
				}
			}

			// Punctual lights are mirrored into the env store with a WORLD transform (prefab
			// Position is parent-local). Directional lights go through SyncSunEntity instead.
			if (entity.HasComponent<LightComponent>() && !entity.HasComponent<SunComponent>())
			{
				var light = entity.GetComponent<LightComponent>();
				if (light.Type is LightType.Point or LightType.Spot)
				{
					_visitedLightsThisSync.Add(entity.Id);

					var world = ComputeWorldMatrix(entity);
					Matrix4x4.Decompose(world, out _, out var worldRot, out var worldPos);

					if (!_lightMirrors.TryGetValue(entity.Id, out var mirror) || mirror.IsNull)
					{
						mirror = _env.Store.CreateEntity(
							new Position(worldPos.X, worldPos.Y, worldPos.Z),
							new Rotation(worldRot.X, worldRot.Y, worldRot.Z, worldRot.W),
							light);
						_lightMirrors[entity.Id] = mirror;
					}
					else
					{
						mirror.Position = new Position(worldPos.X, worldPos.Y, worldPos.Z);
						mirror.Rotation = new Rotation(worldRot.X, worldRot.Y, worldRot.Z, worldRot.W);
						ref var mirrorLight = ref mirror.GetComponent<LightComponent>();
						mirrorLight = light;
					}
				}
			}

			foreach (var child in entity.ChildEntities)
			{
				SyncEntity(child, ref structuralChange, ref boundsDirty);
			}
		}

		// AssetRef paths are forward-slash and relative to the project "Assets" folder; falls back
		// to the nearest "Assets" ancestor of the .prefab.json when no project is loaded.
		private string? ResolveAssetPath(string assetPath)
		{
			if (Path.IsPathRooted(assetPath))
			{
				return File.Exists(assetPath) ? assetPath : null;
			}

			var relative = assetPath.Replace('/', Path.DirectorySeparatorChar);

			var assetsRoot = _projectSession.AssetsPath;
			if (assetsRoot != null)
			{
				var candidate = Path.Combine(assetsRoot, relative);
				if (File.Exists(candidate))
				{
					return candidate;
				}
			}

			if (_currentPrefabPath != null)
			{
				var dir = Path.GetDirectoryName(Path.GetFullPath(_currentPrefabPath));
				while (dir != null)
				{
					if (string.Equals(Path.GetFileName(dir), "Assets", StringComparison.OrdinalIgnoreCase))
					{
						var candidate = Path.Combine(dir, relative);
						return File.Exists(candidate) ? candidate : null;
					}
					dir = Path.GetDirectoryName(dir);
				}
			}

			return null;
		}

		// A factory, not a snapshot: anisotropy can change at any time.
		private ModelLoadOptions BuildLoadOptions() =>
			ViewportSettingsPush.BuildLoadOptions(_editorSettings, RtShadowsEnabled());

		// Compare against the clamped value: raw settings outside [128, 8192] would always differ.
		private int ClampedMaxTextureSize() => ViewportSettingsPush.ClampedMaxTextureSize(_editorSettings);

		// Bind the real probe atlases immediately so the new model never renders with placeholders.
		private void OnStreamedModelReady(ModelStreamer.Resident resident)
		{
			_probeTextures?.Bind(resident.Model!);
		}

		// Runs while the evicted resident's BatchIds are still valid; other residents are untouched.
		private void OnStreamerResidencyResetting(ModelStreamer.Resident resident)
		{
			// The background BVH build reads the whole scene's geometry - stop it before the free.
			WaitProbeBakerTask();

			foreach (var record in _rendered.Values)
			{
				if (record.Instantiated && ReferenceEquals(record.Resident, resident))
				{
					RemoveRecord(record, releaseResident: false);
				}
			}

			_highlightedId = -1;
			_env.Pipeline.PostOverlay = null;
			_structuralDirtySelection = true;
			_physicsStaticsDirty = true;
		}

		private void OnStreamerResidencyReset(ModelStreamer.Resident resident)
		{
			ResetProbeGi();
			_framePending |= _rendered.Count == 0;
		}

		// Combined transform = local glTF instance transform * prefab entity world matrix.
		private void InstantiateRecord(RenderedModel record, ModelStreamer.Resident state, Matrix4x4 world)
		{
			var model = state.Model!;
			for (int i = 0; i < model.instances.Count; i++)
			{
				var instance = model.instances[i];
				var combined = ComposeInstanceTransform(instance.transform, world);
				var entity = ModelViewportGeometry.CreateInstanceEntity(_env.Store, _env.ResourceManager,
					_env.BatchRenderer, state.MeshIds, state.MaterialIds, state.BatchCache,
					instance.meshId, instance.materialId, combined,
					model, state.SkinBases,
					// All skinned instances share one prefab entity: one character, one pose.
					palette => EnsureAnimation().AddInstance(record.EntityId, model, palette));
				if (entity != null)
				{
					record.EnvEntities.Add(entity.Value);
					record.InstanceIndices.Add(i);
				}
			}

			record.LastWorld = world;
			record.Instantiated = true;
		}

		// GpuInstanceBufferSystem clears GpuUpdateTag after applying it - re-add it on every move.
		private void UpdateRecordTransforms(RenderedModel record, ModelStreamer.Resident state, Matrix4x4 world)
		{
			var model = state.Model!;
			for (int i = 0; i < record.EnvEntities.Count; i++)
			{
				var instance = model.instances[record.InstanceIndices[i]];
				var t = ComposeInstanceTransform(instance.transform, world);

				var entity = record.EnvEntities[i];
				entity.Position = new Position(t.position.X, t.position.Y, t.position.Z);
				entity.Rotation = new Rotation(t.rotation.X, t.rotation.Y, t.rotation.Z, t.rotation.W);
				entity.Scale3 = new Scale3(t.scale.X, t.scale.Y, t.scale.Z);
				entity.AddTag<GpuUpdateTag>();
			}

			record.LastWorld = world;
		}

		private static DecaEngine.Core.Transform ComposeInstanceTransform(
			DecaEngine.Core.Transform instanceLocal, Matrix4x4 world)
		{
			var combined = MathUtils.CreateTrs(
				instanceLocal.position, instanceLocal.rotation, instanceLocal.scale) * world;

			if (!Matrix4x4.Decompose(combined, out var scale, out var rotation, out var translation))
			{
				// Sheared transform (non-uniform scale under rotation): recover TRS from the basis.
				// The orthonormalised basis is an approximation, but keeps the object in place.
				translation = combined.Translation;
				var bx = new Vector3(combined.M11, combined.M12, combined.M13);
				var by = new Vector3(combined.M21, combined.M22, combined.M23);
				var bz = new Vector3(combined.M31, combined.M32, combined.M33);
				scale = new Vector3(bx.Length(), by.Length(), bz.Length());

				var rx = scale.X > 1e-8f ? bx / scale.X : Vector3.UnitX;
				var ry = scale.Y > 1e-8f ? by / scale.Y : Vector3.UnitY;
				ry -= rx * Vector3.Dot(ry, rx);
				ry = ry.LengthSquared() > 1e-12f ? Vector3.Normalize(ry) : Vector3.UnitY;
				var rz = Vector3.Cross(rx, ry);
				rotation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
					rx.X, rx.Y, rx.Z, 0f,
					ry.X, ry.Y, ry.Z, 0f,
					rz.X, rz.Y, rz.Z, 0f,
					0f, 0f, 0f, 1f));
			}

			return new DecaEngine.Core.Transform
			{
				position = translation,
				rotation = rotation,
				scale = scale,
			};
		}

		// releaseResident: false when the caller keeps or releases the streamer reference itself.
		private void RemoveRecord(RenderedModel record, bool releaseResident = true)
		{
			foreach (var entity in record.EnvEntities)
			{
				_env.ResourceManager.UnregisterRenderable(entity);
				entity.DeleteEntity();
			}
			record.EnvEntities.Clear();
			record.InstanceIndices.Clear();
			record.Instantiated = false;

			// The pose owns native ozz objects and palette slices tied to the removed instances.
			_animation?.Remove(record.EntityId);

			if (releaseResident && record.Resident != null)
			{
				_streamer.Release(record.Resident);
				record.Resident = null;
			}
		}

		/// <summary>DECA_LOOP_INSPECTOR diagnostic: mean luma/chroma of the Scene View frame.</summary>
		public void DumpFrameStats(string tag)
		{
			try
			{
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				var pixels = DiligentTextureReadback.ReadRgba8(_env.DilApi,
					(DiligentRenderTarget)_env.ColorTarget, out var width, out var height);

				long lumaSum = 0;
				long chromaSum = 0;
				int count = pixels.Length / 4;
				for (int i = 0; i < pixels.Length; i += 4)
				{
					int r = pixels[i];
					int g = pixels[i + 1];
					int b = pixels[i + 2];
					lumaSum += (r + g + b) / 3;
					chromaSum += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
				}

				Console.WriteLine($"[insploop] {tag}: {width}x{height} " +
					$"luma={(count > 0 ? (double)lumaSum / count : 0):F1} " +
					$"chroma={(count > 0 ? (double)chromaSum / count : 0):F1} " +
					$"rendered={_rendered.Count} probes={(_probeTextures != null ? "on" : "OFF")} " +
					$"probesEnabled={ProbesEnabled} sessionDelay={_probeSessionDelay:F2} " +
					$"baker={(_probeBaker != null)} session={(_probeSession != null)} " +
					$"gpuDisabled={_sceneGpuDisabled}");

				var dumpDir = System.Environment.GetEnvironmentVariable("DECA_LOOP_INSPECTOR_DIR");
				if (!string.IsNullOrEmpty(dumpDir))
				{
					Directory.CreateDirectory(dumpDir);
					WriteBmp(Path.Combine(dumpDir, $"insploop_{tag.Replace(' ', '_')}.bmp"), pixels, width, height);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[insploop] {tag}: readback failed: {ex.Message}");
			}
		}

		// Uncompressed 24-bit BMP: the editor has no PNG writer (that lives in DecaEngine.Probes).
		private static void WriteBmp(string path, byte[] rgba, int width, int height)
		{
			int rowSize = (width * 3 + 3) & ~3;
			int dataSize = rowSize * height;
			using var writer = new BinaryWriter(File.Create(path));
			writer.Write((byte)'B'); writer.Write((byte)'M');
			writer.Write(54 + dataSize); writer.Write(0); writer.Write(54);
			writer.Write(40); writer.Write(width); writer.Write(height);
			writer.Write((short)1); writer.Write((short)24);
			writer.Write(0); writer.Write(dataSize); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
			var row = new byte[rowSize];
			for (int y = height - 1; y >= 0; y--)
			{
				int src = y * width * 4;
				for (int x = 0; x < width; x++)
				{
					row[x * 3] = rgba[src + x * 4 + 2];
					row[x * 3 + 1] = rgba[src + x * 4 + 1];
					row[x * 3 + 2] = rgba[src + x * 4];
				}
				writer.Write(row);
			}
		}

		private void ClearScene()
		{
			if (_rendered.Count == 0 && _models.Count == 0 && _lightMirrors.Count == 0)
			{
				return;
			}

			// Ragdolls first: they hold body handles into the simulation destroyed on the next line.
			_animation?.DetachPhysics();
			_physics?.Dispose();
			_physics = null;
			_physicsStaticsDirty = true;

			foreach (var record in _rendered.Values)
			{
				RemoveRecord(record);
			}
			_rendered.Clear();

			// Light mirrors live in the env store, which outlives a prefab switch - delete by hand.
			foreach (var mirror in _lightMirrors.Values)
			{
				if (!mirror.IsNull)
				{
					mirror.DeleteEntity();
				}
			}
			_lightMirrors.Clear();

			// Clear before loading: the new prefab's models must start in an empty batch renderer.
			WaitProbeBakerTask();
			_streamer.ClearAll();

			_highlightedId = -1;
			_env.Pipeline.PostOverlay = null;

			// Frozen graph commands would keep drawing the removed instances: barrier then rebuild.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			ResetProbeGi();

			_env.Pipeline.InvalidateGraph();
		}

		// --- Selection: outline pass and picking ----------------------------------------------------

		private void SyncSelectionHighlight(Entity? selected)
		{
			int id = selected.HasValue && !selected.Value.IsNull ? selected.Value.Id : -1;
			bool selectionChanged = id != _highlightedId;

			if (!selectionChanged && !_transformsDirty && !_structuralDirtySelection)
			{
				return;
			}

			_highlightedId = id;
			_structuralDirtySelection = false;

			_selectionPositions.Clear();
			_selectionIndices.Clear();
			if (selected.HasValue && id != -1)
			{
				CollectSelectionGeometry(selected.Value);
			}

			if (_selectionPositions.Count == 0)
			{
				if (_env.Pipeline.PostOverlay != null)
				{
					_env.Pipeline.PostOverlay = null;
					_env.Pipeline.InvalidateGraph();
				}
				return;
			}

			_selectionOverlay ??= new SelectionOutlineOverlay(_env.DilApi, _graphicsApi, _env.BatchRenderer,
				(IRenderTarget)_env.ColorTarget);

			// Frozen commands survive in-place buffer updates; only a resize needs a rebuild.
			bool commandsDirty = _selectionOverlay.UpdateGeometry(_selectionPositions, _selectionIndices);
			if (_env.Pipeline.PostOverlay == null)
			{
				_env.Pipeline.PostOverlay = _selectionOverlay.Draw;
				commandsDirty = true;
			}

			if (commandsDirty)
			{
				_env.Pipeline.InvalidateGraph();
			}
		}

		// Selecting a parent highlights its whole subtree.
		private void CollectSelectionGeometry(Entity entity)
		{
			if (_rendered.TryGetValue(entity.Id, out var record) && record.Instantiated &&
				!string.IsNullOrEmpty(record.ResolvedPath) &&
				_models.TryGetValue(record.ResolvedPath, out var state) && state.Model != null)
			{
				AppendRecordGeometry(record, state.Model, _selectionPositions, _selectionIndices);
			}

			foreach (var child in entity.ChildEntities)
			{
				CollectSelectionGeometry(child);
			}
		}

		// Bakes instance vertices into world space; CPU vertex copies live in IMeshObject.
		private void AppendRecordGeometry(RenderedModel record, ModelLoader model,
			List<Vector3> targetPositions, List<uint> targetIndices)
			=> AppendModelGeometry(model,
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(record.InstanceIndices),
				record.LastWorld, targetPositions, targetIndices);

		/// <summary>Bakes the given model instances into world-space triangles.</summary>
		public static unsafe void AppendModelGeometry(ModelLoader model, ReadOnlySpan<int> instanceIndices,
			Matrix4x4 world, List<Vector3> targetPositions, List<uint> targetIndices)
		{
			for (int i = 0; i < instanceIndices.Length; i++)
			{
				var instance = model.instances[instanceIndices[i]];
				if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count)
				{
					continue;
				}

				// Triangles only: the mask PSO is TriangleList, line/point indices would be garbage.
				if (model.MaterialPbr.TryGetValue(instance.materialId, out var pbr) &&
					pbr.Topology != ModelLoader.MeshTopologyTriangles)
				{
					continue;
				}

				var mesh = model.Meshes[instance.meshId];
				if (mesh.IndexCount < 3 || mesh.VertexData == null || mesh.IndexData == null)
				{
					continue;
				}

				var t = ComposeInstanceTransform(instance.transform, world);
				var matrix = MathUtils.CreateTrs(t.position, t.rotation, t.scale);

				int vertexCount = UnsafeArray.GetLength(mesh.VertexData);
				var vertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);

				// ModelLoader always builds 32-bit indices.
				var indices = new ReadOnlySpan<uint>(UnsafeArray.GetPtr<uint>(mesh.IndexData, 0), mesh.IndexCount);

				int baseVertex = targetPositions.Count;
				for (int v = 0; v < vertexCount; v++)
				{
					targetPositions.Add(Vector3.Transform(vertices[v].Position, matrix));
				}
				for (int j = 0; j < indices.Length; j++)
				{
					targetIndices.Add((uint)baseVertex + indices[j]);
				}
			}
		}

		private bool TryComputeSceneBounds(out Vector3 min, out Vector3 max)
		{
			min = new Vector3(float.PositiveInfinity);
			max = new Vector3(float.NegativeInfinity);
			bool any = false;

			foreach (var record in _rendered.Values)
			{
				any |= AccumulateRecordBounds(record, ref min, ref max);
			}

			return FinalizeBounds(any, ref min, ref max);
		}

		// Entities with no geometry in the subtree (lights, groups) fall back to a nominal radius.
		private bool TryComputeEntityBounds(Entity entity, out Vector3 min, out Vector3 max)
		{
			min = new Vector3(float.PositiveInfinity);
			max = new Vector3(float.NegativeInfinity);
			bool any = AccumulateEntityBounds(entity, ref min, ref max);

			if (!any)
			{
				const float fallbackRadius = 0.5f;
				var center = ComputeWorldMatrix(entity).Translation;
				min = center - new Vector3(fallbackRadius);
				max = center + new Vector3(fallbackRadius);
				any = true;
			}

			return FinalizeBounds(any, ref min, ref max);
		}

		private bool AccumulateEntityBounds(Entity entity, ref Vector3 min, ref Vector3 max)
		{
			bool any = _rendered.TryGetValue(entity.Id, out var record) &&
				AccumulateRecordBounds(record, ref min, ref max);

			foreach (var child in entity.ChildEntities)
			{
				any |= AccumulateEntityBounds(child, ref min, ref max);
			}

			return any;
		}

		private bool AccumulateRecordBounds(RenderedModel record, ref Vector3 min, ref Vector3 max)
		{
			if (!record.Instantiated || string.IsNullOrEmpty(record.ResolvedPath) ||
				!_models.TryGetValue(record.ResolvedPath, out var state) || state.Model == null)
			{
				return false;
			}

			var model = state.Model;
			bool any = false;
			for (int i = 0; i < record.EnvEntities.Count; i++)
			{
				var instance = model.instances[record.InstanceIndices[i]];
				if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count)
				{
					continue;
				}

				var mesh = model.Meshes[instance.meshId];
				var t = ComposeInstanceTransform(instance.transform, record.LastWorld);
				var worldCenter = Vector3.Transform(mesh.Center * t.scale, t.rotation) + t.position;
				var maxScale = MathF.Max(MathF.Abs(t.scale.X), MathF.Max(MathF.Abs(t.scale.Y), MathF.Abs(t.scale.Z)));
				var radius = mesh.Radius * maxScale;

				min = Vector3.Min(min, worldCenter - new Vector3(radius));
				max = Vector3.Max(max, worldCenter + new Vector3(radius));
				any = true;
			}

			return any;
		}

		// NaN/Infinity in transforms must not break camera framing.
		private static bool FinalizeBounds(bool any, ref Vector3 min, ref Vector3 max)
		{
			if (!any)
			{
				return false;
			}

			if (float.IsNaN(min.X) || float.IsInfinity(min.X) || float.IsNaN(max.X) || float.IsInfinity(max.X) ||
				float.IsNaN(min.Y) || float.IsInfinity(min.Y) || float.IsNaN(max.Y) || float.IsInfinity(max.Y) ||
				float.IsNaN(min.Z) || float.IsInfinity(min.Z) || float.IsNaN(max.Z) || float.IsInfinity(max.Z))
			{
				return false;
			}

			return true;
		}

		private void UpdateShadowBounds()
		{
			if (_env.ShadowSettings == null || !TryComputeSceneBounds(out var min, out var max))
			{
				return;
			}

			_env.ShadowSettings.BoundsCenter = (min + max) * 0.5f;
			_env.ShadowSettings.BoundsRadius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
		}

	}
}
