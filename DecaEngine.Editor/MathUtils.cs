using System;
using System.Numerics;

namespace DecaEngine.Editor
{
	public static class MathUtils
	{
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
}