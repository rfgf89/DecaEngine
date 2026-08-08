using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor
{
	/// <summary>
	/// Layout of the "PreviewSettings" cbuffer declared in UnlitInstancedPS.hlsl (see also
	/// WireframeOverlayPS.hlsl, which ignores it). Mode: 0 = Textured, 1 = Highlight, 2 = Channel debug.
	/// Channel (used only when Mode == 2): 0 = Normal, 1 = UV, 2 = Tangent. Pushed to each material via
	/// <see cref="IMaterialObject.SetConstant{T}"/> by <see cref="ModelPreviewViewport"/> and
	/// <see cref="ModelIconBaker"/> - never touched by the main editor scene, so it stays at its
	/// zero-initialized (Textured) default there.
	/// </summary>
	public struct PreviewSettingsData
	{
		public int Mode;
		public int Channel;
	}

	/// <summary>
	/// Off-screen ECS render environment for showing/baking a .gltf/.glb model: EntityStore +
	/// DiligentBatchRenderer + GraphicsPipeline + camera + color/depth render targets. Models are
	/// drawn unlit (see EditorSettings' default Unlit*Instanced shaders), so there's no light/shadow
	/// setup here - just geometry, culled and drawn via <see cref="SimpleCullingAndRenderSystem"/>.
	/// Shared by <see cref="ModelPreviewViewport"/> (interactive Inspector/Prefab viewport) and
	/// <see cref="ModelIconBaker"/> (background Asset Browser icon baking) - the two have different
	/// update/interaction loops but need the exact same scene scaffolding to render a model, so that
	/// setup lives here instead of being duplicated in both.
	/// </summary>
	public sealed class ModelViewportEnvironment
	{
		public const float CameraFovDegrees = 45f;

		public IGraphicsApi GraphicsApi { get; }
		public DiligentGraphicsApi DilApi { get; }
		public DiligentBatchRenderer BatchRenderer { get; }
		public GraphicsPipelineSimple Pipeline { get; }
		public EntityStore Store { get; }
		public RenderResourceManager ResourceManager { get; }
		public SystemRoot Root { get; }
		public Entity CameraEntity { get; }
		public IRenderTarget ColorTarget { get; }
		public IRenderTarget DepthTarget { get; }

		public ModelViewportEnvironment(IGraphicsApi graphicsApi, uint width, uint height,
			string colorTargetName, string depthTargetName)
		{
			GraphicsApi = graphicsApi;
			DilApi = (DiligentGraphicsApi)graphicsApi;

			BatchRenderer = new DiligentBatchRenderer(DilApi);

			ColorTarget = graphicsApi.CreateRenderTarget(new TextureInfo
			{
				name = colorTargetName,
				width = width,
				height = height,
				format = TextureObjectFormat.R8G8B8A8UNorm,
			});

			DepthTarget = graphicsApi.CreateRenderTarget(new TextureInfo
			{
				name = depthTargetName,
				width = width,
				height = height,
				format = TextureObjectFormat.D32Float,
			});

			Pipeline = new GraphicsPipelineSimple(graphicsApi, BatchRenderer, ColorTarget, DepthTarget,
				new Vector4(0.09f, 0.09f, 0.11f, 1f));

			Store = new EntityStore();
			ResourceManager = new RenderResourceManager(16, 16, Store, BatchRenderer);

			var cameraComponent = new CameraComponent(new CameraData(CameraFovDegrees, 0.05f, 2000f,
				new Vector4(0, 0, width, height)));
			cameraComponent.data.cullFlags = CullFlags.None;

			CameraEntity = Store.CreateEntity(
				new Position(0, 0, -4f),
				new Rotation { value = Quaternion.Identity },
				new Scale3(1, 1, 1),
				cameraComponent);

			Root = new SystemRoot()
			{
				new GpuInstanceBufferSystem(),
				new SimpleCullingAndRenderSystem(ResourceManager, Pipeline)
			};
			Root.AddStore(Store);
		}

		public void SetCameraTransform(Vector3 eye, Vector3 target)
		{
			var viewMatrix = Matrix4x4.CreateLookAtLeftHanded(eye, target, Vector3.UnitY);
			var rotation = Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(viewMatrix));

			CameraEntity.Position = new Position(eye.X, eye.Y, eye.Z);
			CameraEntity.Rotation = new Rotation { value = rotation };
		}
	}

	/// <summary>
	/// Mesh/material registration, instance-entity creation and camera-framing math shared between
	/// <see cref="ModelPreviewViewport"/> and <see cref="ModelIconBaker"/> - both populate a
	/// <see cref="ModelViewportEnvironment"/> from a loaded <see cref="ModelLoader"/> and frame a camera
	/// around either the whole model or a single sub-mesh.
	/// </summary>
	public static class ModelViewportGeometry
	{
		public static void RegisterModelResources(DiligentBatchRenderer batchRenderer, ModelLoader modelLoader,
			Dictionary<int, MeshId> meshIdMap, Dictionary<int, MaterialId> materialIdMap)
		{
			var baseMaterialState = batchRenderer.GetBaseState();
			for (int i = 0; i < modelLoader.materialObjects.Count; i++)
			{
				var kvp = modelLoader.materialObjects.GetAt(i);
				kvp.Value.SetState(baseMaterialState);
				materialIdMap[kvp.Key] = batchRenderer.Register(kvp.Value);
			}

			for (int i = 0; i < modelLoader.Meshes.Count; i++)
			{
				// Пустой меш (без единого индекса - в glTF бывают меши без треугольников/только
				// точки-линии без геометрии) не регистрируем вовсе: batch с нулевым draw-каунтом
				// в лучшем случае рисует "ничего" в очищенный таргет, в худшем - ломает нативный
				// indirect-draw. Инстансы такого меша отсеются в CreateInstanceEntity по отсутствию
				// ключа в meshIdMap, и бейкер/превью корректно пропустят этап (см. BakeNextStage).
				if (modelLoader.Meshes[i].IndexCount == 0)
				{
					continue;
				}

				meshIdMap[i] = batchRenderer.Register(modelLoader.Meshes[i]);
			}
		}

		/// <summary>
		/// Creates one instance entity for the given mesh/material, reusing (and lazily creating) the
		/// batch for that (meshIndex, materialIndex) pair. Returns null if meshIndex has no registered
		/// mesh (e.g. dead reference) - caller should skip it.
		/// </summary>
		public static Entity? CreateInstanceEntity(EntityStore store, RenderResourceManager resourceManager,
			DiligentBatchRenderer batchRenderer, Dictionary<int, MeshId> meshIdMap,
			Dictionary<int, MaterialId> materialIdMap, Dictionary<(int, int), BatchId> batchCache,
			int meshIndex, int materialIndex, DecaEngine.Graphics.Transform t)
		{
			if (!meshIdMap.TryGetValue(meshIndex, out var meshId))
			{
				return null;
			}

			if (!materialIdMap.TryGetValue(materialIndex, out var matId))
			{
				if (materialIdMap.Count == 0)
				{
					// No material registered at all for this model - falling through would leave matId
					// as default(MaterialId) (id 0), which was never registered with the batch renderer.
					// Drawing a batch that references it hits an invalid material slot on the native
					// (Diligent) side, which is undefined behavior rather than a catchable .NET exception.
					return null;
				}

				foreach (var candidate in materialIdMap.Values)
				{
					matId = candidate;
					break;
				}
			}

			if (!batchCache.TryGetValue((meshIndex, materialIndex), out var batchId))
			{
				batchId = batchRenderer.CreateBatch(meshId, matId);
				batchCache[(meshIndex, materialIndex)] = batchId;
			}

			var entity = store.CreateEntity(
				new Position(t.position.X, t.position.Y, t.position.Z),
				new Scale3(t.scale.X, t.scale.Y, t.scale.Z),
				new Rotation(t.rotation.X, t.rotation.Y, t.rotation.Z, t.rotation.W),
				Tags.Get<GpuUpdateTag>());

			resourceManager.RegisterRenderable(entity, batchId);
			return entity;
		}

		/// <summary>
		/// AABB of one sub-mesh across its instances (bounding-sphere of the mesh, transformed by each
		/// instance). If the sub-mesh has no instances, falls back to its local bounding sphere.
		/// </summary>
		public static (Vector3 Min, Vector3 Max) ComputeSubMeshBounds(ModelLoader model, int meshIndex)
		{
			var mesh = model.Meshes[meshIndex];
			var min = new Vector3(float.PositiveInfinity);
			var max = new Vector3(float.NegativeInfinity);
			var any = false;

			foreach (var instance in model.instances)
			{
				if (instance.meshId != meshIndex)
				{
					continue;
				}

				var t = instance.transform;
				var worldCenter = Vector3.Transform(mesh.Center * t.scale, t.rotation) + t.position;
				var maxScale = MathF.Max(MathF.Abs(t.scale.X), MathF.Max(MathF.Abs(t.scale.Y), MathF.Abs(t.scale.Z)));
				var radius = mesh.Radius * maxScale;

				min = Vector3.Min(min, worldCenter - new Vector3(radius));
				max = Vector3.Max(max, worldCenter + new Vector3(radius));
				any = true;
			}

			if (!any)
			{
				min = mesh.Center - new Vector3(mesh.Radius);
				max = mesh.Center + new Vector3(mesh.Radius);
			}

			return (min, max);
		}

		/// <summary>Distance at which a bounding sphere of the given radius exactly fills the vertical FOV, plus a margin.</summary>
		public static float ComputeFramingDistance(float radius, float fovDegrees)
		{
			var halfFovRad = fovDegrees * (MathF.PI / 180f) * 0.5f;
			return Math.Clamp(radius / MathF.Sin(halfFovRad) * 1.25f, 0.2f, 1500f);
		}

		public static Vector3 ComputeOrbitEye(Vector3 target, float distance, float yaw, float pitch)
		{
			return target + distance * new Vector3(
				MathF.Cos(pitch) * MathF.Sin(yaw),
				MathF.Sin(pitch),
				MathF.Cos(pitch) * MathF.Cos(yaw));
		}
	}
}
