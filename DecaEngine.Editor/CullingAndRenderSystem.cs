using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.Diligent.RenderGraph;
using Diligent;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using UnsafeCollections.Collections.Native;

namespace DecaEngine.Editor.ECS;

public class CullingAndRenderSystem : QuerySystem, IDisposable
{
	private readonly DiligentBatchRenderer _batchRenderer;
	private readonly RenderResourceManager _resourceManager;
	private readonly DiligentGraphicsPipeline _pipeline;

	private readonly Dictionary<Entity, List<DiligentCommandBuffer>> _cameraCommandBuffers = new();

	private NativeArray<ViewData> _viewDatas = new(8);
	private NativeArray<CullData> _cullDatas = new (8);
	private NativeArray<LightData> _lightDatas = new(8);

	public float CascadeLambda { get; set; } = 0.999f;
	public float ShadowMapZMargin { get; set; } = 50.0f;

	public CullingAndRenderSystem(DiligentBatchRenderer batchRenderer, RenderResourceManager resourceManager, DiligentGraphicsPipeline pipeline)
	{
		_batchRenderer = batchRenderer;
		_resourceManager = resourceManager;
		_pipeline = pipeline;
	}

	protected override void OnUpdate()
	{
		var mainCameras = Query.Store.Query<CameraComponent>().WithoutAllComponents(ComponentTypes.Get<CascadedShadowComponent>());
		var lights = Query.Store.Query<LightComponent, SunComponent, CascadedShadowComponent>();

		if (mainCameras.Count > 0)
		{
			_batchRenderer.CheckAndReallocateBuffers();
			bool isDirty = _batchRenderer.IsDirty;

			mainCameras.ForEachEntity((ref CameraComponent camera, Entity entity) =>
			{
				camera.SetPositionAndRotation(entity.Position.value, entity.Rotation.value);

				if (!_cameraCommandBuffers.TryGetValue(entity, out var cmdList))
				{
					cmdList = new List<DiligentCommandBuffer>();
					_cameraCommandBuffers[entity] = cmdList;
					isDirty = true;
				}

				int drawCount = _resourceManager.totalInstances - _resourceManager.totalFreeSlot;
				
				// --- Update main camera data ---
				var mainViewData = camera.CreateViewData();
				_viewDatas[7] = mainViewData;

				CullData mainCullData = camera.CreateCullData();
				mainCullData.drawCount = drawCount;
				_cullDatas[7] = mainCullData;

				if (lights.Count > 0)
				{
					var mainCam = camera;
					lights.ForEachEntity((ref LightComponent light, ref SunComponent sun, ref CascadedShadowComponent cascadedShadow, Entity lightEntity) =>
					{
						var lightDirection = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, lightEntity.Rotation.value));

						UpdateCascades(mainCam, lightDirection, ref cascadedShadow);

						// Populate arrays for rendering from the component
						_viewDatas[0] = cascadedShadow.Cascade0.CreateViewData();
						_cullDatas[0] = cascadedShadow.Cascade0.CreateCullData();
						_cullDatas.GetRef(0).drawCount = drawCount;

						_viewDatas[1] = cascadedShadow.Cascade1.CreateViewData();
						_cullDatas[1] = cascadedShadow.Cascade1.CreateCullData();
						_cullDatas.GetRef(1).drawCount = drawCount;

						_viewDatas[2] = cascadedShadow.Cascade2.CreateViewData();
						_cullDatas[2] = cascadedShadow.Cascade2.CreateCullData();
						_cullDatas.GetRef(2).drawCount = drawCount;

						_viewDatas[3] = cascadedShadow.Cascade3.CreateViewData();
						_cullDatas[3] = cascadedShadow.Cascade3.CreateCullData();
						_cullDatas.GetRef(3).drawCount = drawCount;

						for (int i = 0; i < ShadowRenderer.MaxCascades; i++)
						{
							_lightDatas[i] = new LightData
							{
								CascadeMatrix0 = _viewDatas[i].viewProj,
								CascadeMatrix1 = _viewDatas[i].viewProj,
								CascadeMatrix2 = _viewDatas[i].viewProj,
								CascadeMatrix3 = _viewDatas[i].viewProj,
								LightPos = lightEntity.Position.value.AsVector4(),
								SpotAngles = new Vector4(0, 0, light.ShadowStrength, 0),
								LightDirection = new Vector4(-lightDirection, 1.0f),
								LightColor = new Vector4(light.Color, light.Intensity),
							};
						}

						// Update main pass light data with all cascade matrices
						_lightDatas[7] = new LightData
						{
							LightDirection = new Vector4(-lightDirection, 1.0f),
							LightColor = new Vector4(light.Color, light.Intensity),
							CascadeMatrix0 = _viewDatas[0].viewProj,
							CascadeMatrix1 = _viewDatas[1].viewProj,
							CascadeMatrix2 = _viewDatas[2].viewProj,
							CascadeMatrix3 = _viewDatas[3].viewProj,
							LightPos = lightEntity.Position.value.AsVector4(),
							SpotAngles = new Vector4(0, 0, light.ShadowStrength, 0),
							CascadeSplits = _lightDatas.GetRef(7).CascadeSplits // Preserved from UpdateCascades
						};
					});
				}

				if (isDirty)
				{
					cmdList.Clear();

					if (lights.Count > 0)
					{
						for (int i = 0; i < ShadowRenderer.MaxCascades; i++)
						{
							var shadowCmd = new DiligentCommandBuffer(_pipeline.ImmediateContext);
							shadowCmd.BeginRecording();

							_batchRenderer.SetupViewData(shadowCmd, ref _viewDatas.GetRef(i));
							_batchRenderer.SetupCullData(shadowCmd, ref _cullDatas.GetRef(i));
							_batchRenderer.SetupLightData(shadowCmd, ref _lightDatas.GetRef(i));

							_batchRenderer.ClearIndirectDrawBuffers(shadowCmd);
							var shadowCullResult = _batchRenderer.ExecuteComputeCulling(shadowCmd, i);

							_batchRenderer.ExecuteDrawShadows(shadowCmd, shadowCullResult, i);
							
							shadowCmd.Freeze();
							cmdList.Add(shadowCmd);
						}
					}

					// --- Main Pass ---
					var mainCmd = new DiligentCommandBuffer(_pipeline.ImmediateContext);
					mainCmd.BeginRecording();

					_batchRenderer.ClearIndirectDrawBuffers(mainCmd);

					_batchRenderer.SetupViewData(mainCmd, ref _viewDatas.GetRef(7));
					_batchRenderer.SetupCullData(mainCmd, ref _cullDatas.GetRef(7));
					_batchRenderer.SetupLightData(mainCmd, ref _lightDatas.GetRef(7));
					CullResult cameraCullResult = _batchRenderer.ExecuteComputeCulling(mainCmd);
					_batchRenderer.ExecuteDrawBatching(mainCmd, cameraCullResult);

					mainCmd.Freeze();
					cmdList.Add(mainCmd);
				}

				for (var index = 0; index < cmdList.Count - 1; index++)
				{
					var cmd = cmdList[index];
					cmd.Execute();
				}

				var rtv = _pipeline.SwapChain.GetCurrentBackBufferRTV();
				var dsv = _pipeline.SwapChain.GetDepthBufferDSV();
				_pipeline.ImmediateContext.SetRenderTargets([rtv], dsv, ResourceStateTransitionMode.Transition);
				_pipeline.ImmediateContext.ClearRenderTarget(rtv, new Vector4(0.1f, 0.1f, 0.1f, 1f), ResourceStateTransitionMode.Transition);
				_pipeline.ImmediateContext.ClearDepthStencil(dsv, ClearDepthStencilFlags.Depth, 0.0f, 0, ResourceStateTransitionMode.Transition);

				cmdList[^1].Execute();
			});
		}
	}

	private unsafe void UpdateCascades(CameraComponent camera, Vector3 lightDirection, ref CascadedShadowComponent cascadedShadow)
	{
		var cascadeSplits = cascadedShadow.CascadeDistances;

		Vector3 lightUp = Vector3.UnitY;
		if (Math.Abs(Vector3.Dot(lightDirection, lightUp)) > 0.99f) lightUp = Vector3.UnitX;

		Matrix4x4.Invert(camera.renderCamera.view, out Matrix4x4 cameraWorld);

		for (int i = 0; i < ShadowRenderer.MaxCascades; i++)
		{
			float n = cascadeSplits[i];
			float f = cascadeSplits[i + 1];

			float tanHalfFov = MathF.Tan(camera.data.fovRad * 0.5f);
			float nearY = n * tanHalfFov;
			float nearX = nearY * camera.data.aspect;
			float farY = f * tanHalfFov;
			float farX = farY * camera.data.aspect;

			Vector3[] cornersViewSpace = new Vector3[8];
			cornersViewSpace[0] = new Vector3(-nearX, -nearY, -n);
			cornersViewSpace[1] = new Vector3( nearX, -nearY, -n);
			cornersViewSpace[2] = new Vector3( nearX,  nearY, -n);
			cornersViewSpace[3] = new Vector3(-nearX,  nearY, -n);
			cornersViewSpace[4] = new Vector3(-farX, -farY, -f);
			cornersViewSpace[5] = new Vector3( farX, -farY, -f);
			cornersViewSpace[6] = new Vector3( farX,  farY, -f);
			cornersViewSpace[7] = new Vector3(-farX,  farY, -f);

			Vector3 center = Vector3.Zero;
			for(int j=0; j<8; j++)
			{
				cornersViewSpace[j] = Vector3.Transform(cornersViewSpace[j], cameraWorld);
				center += cornersViewSpace[j];
			}
			center /= 8.0f;

			float radius = 0.0f;
			foreach (var corner in cornersViewSpace)
			{
				radius = Math.Max(radius, Vector3.Distance(corner, center));
			}

			float worldUnitsPerTexel = (radius * 2.0f) / ShadowRenderer.ShadowMapSize;
			Matrix4x4 tempLightView = Matrix4x4.CreateLookAt(Vector3.Zero, lightDirection, lightUp);
			Vector3 lightSpaceCenter = Vector3.Transform(center, tempLightView);
			lightSpaceCenter.X = MathF.Floor(lightSpaceCenter.X / worldUnitsPerTexel) * worldUnitsPerTexel;
			lightSpaceCenter.Y = MathF.Floor(lightSpaceCenter.Y / worldUnitsPerTexel) * worldUnitsPerTexel;
			Matrix4x4.Invert(tempLightView, out Matrix4x4 tempLightViewInv);
			center = Vector3.Transform(lightSpaceCenter, tempLightViewInv);

			Vector3 lightPos = center - lightDirection;
			float znear = radius + ShadowMapZMargin;
			float zfar = -radius - ShadowMapZMargin;

			var camData = new CameraData(radius * 2.0f, radius * 2.0f, znear, zfar, new Vector4(0, 0, ShadowRenderer.ShadowMapSize, ShadowRenderer.ShadowMapSize));

			fixed (CameraComponent* ptr = &cascadedShadow.Cascade0)
			{
				(ptr + i)->data = camData;
				(ptr + i)->SetLookAt(lightPos, center, lightUp);
				(ptr + i)->RecalculateProjection();
			}
		}

		_lightDatas.GetRef(7).CascadeSplits = new Vector4(cascadeSplits[1], cascadeSplits[2], cascadeSplits[3], cascadeSplits[4]);
	}

	public void Dispose()
	{
	}
}