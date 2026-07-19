using System;
using System.Numerics;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS;

namespace DecaEngine.Editor.ECS
{
	[Flags]
	public enum CullFlags
	{
		None = 0,
		Frustum = 1,
		Lod = 2,
		All = Frustum | Lod,
	}

	public struct BoundingBoxInfo : IComponent
	{
		public Vector3 Center;
		public Vector3 Extents;
	}

	public struct BatchRenderInfo : IComponent
	{
		public BatchId BatchId;
		public int GpuSlotIndex;
	}

	public unsafe struct LinkDrawInfo : IComponent
	{
		public GPURenderInstance* renderInstance;
		public DrawData* drawData;
	}

	public struct GpuUpdateTag : ITag
	{

	}

	public enum ProjectionType
	{
		Perspective,
		Orthographic
	}

	public struct CameraData
	{
		public float near;
		public float far;

		public float fovRad;
		public float aspect;
		public float lodTargetError;

		public Vector4 viewport;
		public ProjectionType projectionType;

		public CullFlags cullFlags;

		public CameraData(float fov, float near, float far, Vector4 viewport)
		{
			projectionType = ProjectionType.Perspective;
			fovRad = fov * (float)(Math.PI / 180f);
			this.near = near;
			this.far = far;
			this.viewport = viewport;
			aspect = viewport.Z / viewport.W;
			lodTargetError = 0.001f;
		}

		public CameraData(float width, float height, float near, float far, Vector4 viewport)
		{
			projectionType = ProjectionType.Orthographic;
			this.near = near;
			this.far = far;
			this.viewport = viewport;
			this.viewport.Z = width;
			this.viewport.W = height;
			aspect = width / height;
			lodTargetError = 0.001f;
		}
	}

	public struct RenderCamera
	{
		public Matrix4x4 view = Matrix4x4.Identity;
		public Matrix4x4 proj = Matrix4x4.Identity;
		public Matrix4x4 rotate = Matrix4x4.Identity;

		public RenderCamera()
		{
		}
	}

	public static class CameraUtils
	{
		public static void MakeRotate(this ref RenderCamera camera, Quaternion rotation)
		{
			camera.rotate = Matrix4x4.CreateFromQuaternion(rotation);
		}

		public static void MakeView(this ref RenderCamera camera, Vector3 position)
		{
			camera.view = Matrix4x4.Transpose(camera.rotate);

			float x = -position.X;
			float y = -position.Y;
			float z = -position.Z;

			camera.view.M41 = (x * camera.view.M11) + (y * camera.view.M21) + (z * camera.view.M31);
			camera.view.M42 = (x * camera.view.M12) + (y * camera.view.M22) + (z * camera.view.M32);
			camera.view.M43 = (x * camera.view.M13) + (y * camera.view.M23) + (z * camera.view.M33);
			camera.view.M44 = 1.0f;
		}

		public static void MakeOrthographicReversedZ(this ref RenderCamera camera, float width, float height, float near, float far)
		{
			camera.proj = new Matrix4x4(
				2.0f / width, 0, 0, 0,
				0, 2.0f / height, 0, 0,
				0, 0, 1.0f / (near - far), 0,
				0, 0, near / (near - far), 1.0f
			);
		}

		public static void MakePerspectiveReversedZ(this ref RenderCamera camera, float fov, float aspect, float near)
		{
			float f = 1.0f / MathF.Tan(fov / 2.0f);
			camera.proj = new Matrix4x4(
				f / aspect, 0, 0, 0,
				0, f, 0, 0,
				0, 0, 0, 1,
				0, 0, near, 0);
		}
	}

	public struct CameraComponent : IComponent
	{
		private Vector3 position;
		private Quaternion rotation;

		public CameraData data;
		public RenderCamera renderCamera;

		public CameraComponent(CameraData data)
		{
			this.data = data;
			this.position = default;
			this.rotation = Quaternion.Identity;

			RecalculateRotate();
			RecalculateProjection();
			RecalculateView();
		}

		public Vector3 Position
		{
			get => position;
			set
			{
				if (position == value)
				{
					return;
				}

				position = value;
				RecalculateView();
			}
		}

		public Quaternion Rotation
		{
			get => rotation;
			set
			{
				if (rotation == value)
				{
					return;
				}

				rotation = value;
				RecalculateRotate();
				RecalculateView();
			}
		}

		public void SetPositionAndRotation(Vector3 pos, Quaternion rot)
		{
			bool posChanged = position != pos;
			bool rotChanged = rotation != rot;

			if (!posChanged && !rotChanged)
			{
				return;
			}

			position = pos;
			rotation = rot;

			if (rotChanged)
			{
				RecalculateRotate();
			}

			RecalculateView();
		}

		public void SetLookAt(Vector3 pos, Vector3 target, Vector3 up)
		{
			position = pos;
			var viewMatrix = Matrix4x4.CreateLookAt(pos, target, up);
			Matrix4x4.Invert(viewMatrix, out var worldMatrix);
			if (Matrix4x4.Decompose(worldMatrix, out _, out rotation, out _))
			{
				RecalculateRotate();
				RecalculateView();
			}
		}

		private void RecalculateRotate()
		{
			renderCamera.MakeRotate(rotation);
		}

		private void RecalculateView()
		{
			renderCamera.MakeView(position);
		}

		public void RecalculateProjection()
		{
			switch (data.projectionType)
			{
				case ProjectionType.Perspective:
					renderCamera.MakePerspectiveReversedZ(data.fovRad, data.aspect, data.near);
					break;
				default:
					renderCamera.MakeOrthographicReversedZ(data.viewport.Z, data.viewport.W, data.near, data.far);
					break;
			}
		}

		public CullData CreateCullData()
		{
			CullData cullData = default;
			cullData.view = renderCamera.view;
			cullData.cullFrustum = 0;

			Matrix4x4 projectionT = Matrix4x4.Transpose(renderCamera.proj);
			Vector4 frustumX = (projectionT[3] + projectionT[0]);
			Vector4 frustumY = (projectionT[3] + projectionT[1]);
			MathUtils.NormalizePlane(ref frustumX);
			MathUtils.NormalizePlane(ref frustumY);

			cullData.frustum = new Vector4(frustumX.X, frustumX.Z, frustumY.Y, frustumY.Z);
			cullData.P00 = renderCamera.proj.M11;
			cullData.P11 = renderCamera.proj.M22;
			cullData.znear = data.near;
			cullData.zfar = data.far;

			if (data.projectionType == ProjectionType.Perspective)
			{
				float fy = (data.viewport.W * 0.5f) / MathF.Tan(0.5f * data.fovRad);
				cullData.lodTarget = data.lodTargetError / fy;
			}
			else
			{
				cullData.lodTarget = data.lodTargetError * (data.viewport.W / data.viewport.Z); // Simplified
			}

			return cullData;
		}

		public ViewData CreateViewData()
		{
			ViewData viewData = default;
			viewData.view = renderCamera.view;
			viewData.viewProj = renderCamera.view * renderCamera.proj;
			viewData.viewport = data.viewport;
			viewData.CameraWorldPos = position;
			return viewData;
		}

		public void RecalculateForce()
		{
			RecalculateRotate();
			RecalculateView();
			RecalculateProjection();
		}
	}

	public unsafe struct CascadedShadowComponent() : IComponent
	{
		public CameraComponent Cascade0 = default;
		public CameraComponent Cascade1 = default;
		public CameraComponent Cascade2 = default;
		public CameraComponent Cascade3 = default;

		public float[] CascadeDistances = new float[5];
	}
}