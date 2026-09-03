using System;
using System.Collections.Generic;
using System.Numerics;

namespace DecaEngine.Core;

/// <summary>Matrix and plane helpers on plain System.Numerics.</summary>
public static class MathUtils
{
	public static Matrix4x4 CreateTrs(Vector3 translate, Quaternion rotation, Vector3 scale)
	{
		Matrix4x4 modelMatrix = Matrix4x4.CreateFromQuaternion(rotation);

		modelMatrix.M11 *= scale.X;
		modelMatrix.M12 *= scale.X;
		modelMatrix.M13 *= scale.X;
		modelMatrix.M21 *= scale.Y;
		modelMatrix.M22 *= scale.Y;
		modelMatrix.M23 *= scale.Y;
		modelMatrix.M31 *= scale.Z;
		modelMatrix.M32 *= scale.Z;
		modelMatrix.M33 *= scale.Z;

		modelMatrix.M41 = translate.X;
		modelMatrix.M42 = translate.Y;
		modelMatrix.M43 = translate.Z;
		modelMatrix.M44 = 1.0f;

		return modelMatrix;
	}

	public static void NormalizePlane(ref Vector4 plane)
	{
		float mag = (float)Math.Sqrt(plane.X * plane.X + plane.Y * plane.Y + plane.Z * plane.Z);
		plane.X = plane.X / mag;
		plane.Y = plane.Y / mag;
		plane.Z = plane.Z / mag;
		plane.W = plane.W / mag;
	}

	public static Vector3[] GetFrustumCorners(Matrix4x4 viewProj)
	{
		Matrix4x4.Invert(viewProj, out var invViewProj);
		Vector3[] corners = new Vector3[8];
		
		// In Reversed-Z, Z=1 is Near, Z=0 is Far.
		corners[0] = Vector3.Transform(new Vector3(-1, -1, 1), invViewProj);
		corners[1] = Vector3.Transform(new Vector3(1, -1, 1), invViewProj);
		corners[2] = Vector3.Transform(new Vector3(1, 1, 1), invViewProj);
		corners[3] = Vector3.Transform(new Vector3(-1, 1, 1), invViewProj);
		
		corners[4] = Vector3.Transform(new Vector3(-1, -1, 0), invViewProj);
		corners[5] = Vector3.Transform(new Vector3(1, -1, 0), invViewProj);
		corners[6] = Vector3.Transform(new Vector3(1, 1, 0), invViewProj);
		corners[7] = Vector3.Transform(new Vector3(-1, 1, 0), invViewProj);
		
		return corners;
	}

	public static Vector3[] GetFrustumCorners(Matrix4x4 viewProj, float near, float far)
	{
		Matrix4x4.Invert(viewProj, out var invViewProj);
		Vector3[] corners = new Vector3[8];

		corners[0] = Vector3.Transform(new Vector3(-1, -1, near), invViewProj);
		corners[1] = Vector3.Transform(new Vector3(1, -1, near), invViewProj);
		corners[2] = Vector3.Transform(new Vector3(1, 1, near), invViewProj);
		corners[3] = Vector3.Transform(new Vector3(-1, 1, near), invViewProj);
		corners[4] = Vector3.Transform(new Vector3(-1, -1, far), invViewProj);
		corners[5] = Vector3.Transform(new Vector3(1, -1, far), invViewProj);
		corners[6] = Vector3.Transform(new Vector3(1, 1, far), invViewProj);
		corners[7] = Vector3.Transform(new Vector3(-1, 1, far), invViewProj);

		return corners;
	}

	/// <summary>Quaternion to Euler angles, for inspector rotation fields.</summary>
	public static Vector3 ToEulerAngles(Quaternion q)
	{
		Vector3 angles = new();

		// pitch (x-axis rotation)
		double sinp = 2 * (q.W * q.Y - q.Z * q.X);
		if (Math.Abs(sinp) >= 1)
			angles.X = (float)Math.CopySign(Math.PI / 2, sinp);
		else
			angles.X = (float)Math.Asin(sinp);

		// yaw (y-axis rotation)
		double sinr_cosp = 2 * (q.W * q.X + q.Y * q.Z);
		double cosr_cosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
		angles.Y = (float)Math.Atan2(sinr_cosp, cosr_cosp);

		// roll (z-axis rotation)
		double siny_cosp = 2 * (q.W * q.Z + q.X * q.Y);
		double cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
		angles.Z = (float)Math.Atan2(siny_cosp, cosy_cosp);

		return angles;
	}
}