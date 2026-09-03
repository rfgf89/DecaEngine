using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Graphics;

/// <summary>Debug line vertex; position is in world space.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DebugLineVertex
{
	public Vector3 Position;

	// Alpha is not opacity: zero means "no vertex" and the shader culls it to clip space.
	public Vector4 Color;
}

/// <summary>Screen-space caption anchored to a world point; collected here, drawn by the host.</summary>
public struct DebugLabel
{
	public Vector3 Position;
	public string Text;
	public Vector4 Color;
}

/// <summary>Shared debug palette; colors encode meaning and must stay consistent across systems.</summary>
public static class DebugColor
{
	public static readonly Vector4 White = new(1f, 1f, 1f, 1f);
	public static readonly Vector4 Black = new(0f, 0f, 0f, 1f);
	public static readonly Vector4 Grey = new(0.5f, 0.5f, 0.5f, 1f);
	public static readonly Vector4 Red = new(1f, 0.15f, 0.15f, 1f);
	public static readonly Vector4 Green = new(0.2f, 1f, 0.25f, 1f);
	public static readonly Vector4 Blue = new(0.3f, 0.5f, 1f, 1f);
	public static readonly Vector4 Yellow = new(1f, 0.9f, 0.2f, 1f);
	public static readonly Vector4 Orange = new(1f, 0.55f, 0.1f, 1f);
	public static readonly Vector4 Cyan = new(0.2f, 0.95f, 1f, 1f);
	public static readonly Vector4 Magenta = new(1f, 0.3f, 0.9f, 1f);

	/// <summary>Dimmed variant of a color, for the secondary half of a pair.</summary>
	public static Vector4 Dim(Vector4 color, float factor = 0.45f) =>
		new(color.X * factor, color.Y * factor, color.Z * factor, color.W);
}

/// <summary>Per-frame immediate-mode sink for debug lines and labels, flushed once by the renderer.
/// Keeps two independent buffers, depth-tested and on-top. Not thread-safe.</summary>
public sealed class DebugDraw
{
	private readonly List<DebugLineVertex> _depthTested = new();
	private readonly List<DebugLineVertex> _onTop = new();
	private readonly List<DebugLabel> _labels = new();

	/// <summary>When false, primitive calls return immediately and nothing is accumulated.</summary>
	public bool Enabled { get; set; }

	/// <summary>Per-frame vertex cap; anything beyond it is dropped and sets <see cref="Overflowed"/>.</summary>
	public int MaxVertices { get; set; } = 1 << 18;

	/// <summary>Whether a per-frame cap was hit this frame.</summary>
	public bool Overflowed { get; private set; }

	public int DepthTestedCount => _depthTested.Count;
	public int OnTopCount => _onTop.Count;
	public int TotalCount => _depthTested.Count + _onTop.Count;

	public ReadOnlySpan<DebugLineVertex> DepthTestedVertices() => CollectionsMarshal.AsSpan(_depthTested);

	public ReadOnlySpan<DebugLineVertex> OnTopVertices() => CollectionsMarshal.AsSpan(_onTop);

	/// <summary>Labels collected this frame.</summary>
	public IReadOnlyList<DebugLabel> Labels => _labels;

	/// <summary>Per-frame label cap; far lower than <see cref="MaxVertices"/>, one draw call each.</summary>
	public int MaxLabels { get; set; } = 512;

	/// <summary>Drops the frame's contents; call exactly once per frame, before filling.</summary>
	public void Clear()
	{
		_depthTested.Clear();
		_onTop.Clear();
		_labels.Clear();
		Overflowed = false;
	}

	/// <summary>Adds a caption at a world position; empty text is ignored.</summary>
	public void Label(Vector3 position, string text, Vector4 color)
	{
		if (!Enabled || string.IsNullOrEmpty(text) || !IsFinite(position))
		{
			return;
		}

		if (_labels.Count >= MaxLabels)
		{
			Overflowed = true;
			return;
		}

		_labels.Add(new DebugLabel { Position = position, Text = text, Color = color });
	}

	public void Line(Vector3 a, Vector3 b, Vector4 color, bool onTop = false)
	{
		if (!Enabled)
		{
			return;
		}

		// A single NaN vertex can drop the whole line draw, differently per driver.
		if (!IsFinite(a) || !IsFinite(b))
		{
			return;
		}

		var list = onTop ? _onTop : _depthTested;

		if (_depthTested.Count + _onTop.Count + 2 > MaxVertices)
		{
			Overflowed = true;
			return;
		}

		list.Add(new DebugLineVertex { Position = a, Color = color });
		list.Add(new DebugLineVertex { Position = b, Color = color });
	}

	/// <summary>Draws a polyline through the points, optionally closing the loop.</summary>
	public void Polyline(ReadOnlySpan<Vector3> points, Vector4 color, bool closed = false, bool onTop = false)
	{
		for (int i = 1; i < points.Length; i++)
		{
			Line(points[i - 1], points[i], color, onTop);
		}

		if (closed && points.Length > 2)
		{
			Line(points[^1], points[0], color, onTop);
		}
	}

	/// <summary>Three-axis cross marking a point; size is in world units and has no default.</summary>
	public void Cross(Vector3 point, float size, Vector4 color, bool onTop = false)
	{
		Line(point - Vector3.UnitX * size, point + Vector3.UnitX * size, color, onTop);
		Line(point - Vector3.UnitY * size, point + Vector3.UnitY * size, color, onTop);
		Line(point - Vector3.UnitZ * size, point + Vector3.UnitZ * size, color, onTop);
	}

	public void Ray(Vector3 origin, Vector3 direction, float length, Vector4 color, bool onTop = false) =>
		Line(origin, origin + direction * length, color, onTop);

	/// <summary>Arrow with a head sized as a fraction of its own length.</summary>
	public void Arrow(Vector3 from, Vector3 to, Vector4 color, bool onTop = false)
	{
		Line(from, to, color, onTop);

		var delta = to - from;
		float length = delta.Length();
		if (length < 1e-6f)
		{
			return;
		}

		var direction = delta / length;
		Basis(direction, out var u, out var v);

		float head = length * 0.18f;
		var baseCenter = to - direction * head;

		Line(to, baseCenter + u * head * 0.5f, color, onTop);
		Line(to, baseCenter - u * head * 0.5f, color, onTop);
		Line(to, baseCenter + v * head * 0.5f, color, onTop);
		Line(to, baseCenter - v * head * 0.5f, color, onTop);
	}

	/// <summary>Transform axes in the usual editor convention: X red, Y green, Z blue.</summary>
	public void Axes(in Matrix4x4 transform, float scale, bool onTop = false)
	{
		var origin = transform.Translation;

		Line(origin, origin + new Vector3(transform.M11, transform.M12, transform.M13) * scale, DebugColor.Red, onTop);
		Line(origin, origin + new Vector3(transform.M21, transform.M22, transform.M23) * scale, DebugColor.Green, onTop);
		Line(origin, origin + new Vector3(transform.M31, transform.M32, transform.M33) * scale, DebugColor.Blue, onTop);
	}

	public void Circle(Vector3 center, Vector3 axisU, Vector3 axisV, float radius, Vector4 color,
		int segments = 24, bool onTop = false)
	{
		if (segments < 3)
		{
			segments = 3;
		}

		var previous = center + axisU * radius;

		for (int i = 1; i <= segments; i++)
		{
			float angle = i / (float)segments * MathF.Tau;
			var next = center + (axisU * MathF.Cos(angle) + axisV * MathF.Sin(angle)) * radius;

			Line(previous, next, color, onTop);
			previous = next;
		}
	}

	/// <summary>Sphere drawn as three orthogonal rings, cheap enough for hundreds of bodies.</summary>
	public void WireSphere(Vector3 center, float radius, Vector4 color, int segments = 24, bool onTop = false)
	{
		Circle(center, Vector3.UnitX, Vector3.UnitY, radius, color, segments, onTop);
		Circle(center, Vector3.UnitY, Vector3.UnitZ, radius, color, segments, onTop);
		Circle(center, Vector3.UnitZ, Vector3.UnitX, radius, color, segments, onTop);
	}

	public void WireBox(Vector3 center, Quaternion orientation, Vector3 halfExtents, Vector4 color,
		bool onTop = false)
	{
		Span<Vector3> corners = stackalloc Vector3[8];

		for (int i = 0; i < 8; i++)
		{
			var local = new Vector3(
				(i & 1) == 0 ? -halfExtents.X : halfExtents.X,
				(i & 2) == 0 ? -halfExtents.Y : halfExtents.Y,
				(i & 4) == 0 ? -halfExtents.Z : halfExtents.Z);

			corners[i] = center + Vector3.Transform(local, orientation);
		}

		// Corner indices are edge-adjacent when they differ in exactly one bit.
		for (int i = 0; i < 8; i++)
		{
			for (int bit = 1; bit <= 4; bit <<= 1)
			{
				int j = i | bit;
				if (j != i)
				{
					Line(corners[i], corners[j], color, onTop);
				}
			}
		}
	}

	public void WireBox(Vector3 min, Vector3 max, Vector4 color, bool onTop = false) =>
		WireBox((min + max) * 0.5f, Quaternion.Identity, (max - min) * 0.5f, color, onTop);

	/// <summary>Capsule in Bepu convention: local Y axis, length excludes the two hemispheres.</summary>
	public void WireCapsule(Vector3 center, Quaternion orientation, float radius, float length,
		Vector4 color, int segments = 16, bool onTop = false)
	{
		float half = length * 0.5f;

		var axis = Vector3.Transform(Vector3.UnitY, orientation);
		var u = Vector3.Transform(Vector3.UnitX, orientation);
		var v = Vector3.Transform(Vector3.UnitZ, orientation);

		var top = center + axis * half;
		var bottom = center - axis * half;

		Circle(top, u, v, radius, color, segments, onTop);
		Circle(bottom, u, v, radius, color, segments, onTop);

		Line(top + u * radius, bottom + u * radius, color, onTop);
		Line(top - u * radius, bottom - u * radius, color, onTop);
		Line(top + v * radius, bottom + v * radius, color, onTop);
		Line(top - v * radius, bottom - v * radius, color, onTop);

		HalfCircle(top, u, axis, radius, color, segments, onTop);
		HalfCircle(top, v, axis, radius, color, segments, onTop);
		HalfCircle(bottom, u, -axis, radius, color, segments, onTop);
		HalfCircle(bottom, v, -axis, radius, color, segments, onTop);
	}

	public void WireCylinder(Vector3 center, Quaternion orientation, float radius, float length,
		Vector4 color, int segments = 16, bool onTop = false)
	{
		float half = length * 0.5f;

		var axis = Vector3.Transform(Vector3.UnitY, orientation);
		var u = Vector3.Transform(Vector3.UnitX, orientation);
		var v = Vector3.Transform(Vector3.UnitZ, orientation);

		var top = center + axis * half;
		var bottom = center - axis * half;

		Circle(top, u, v, radius, color, segments, onTop);
		Circle(bottom, u, v, radius, color, segments, onTop);

		Line(top + u * radius, bottom + u * radius, color, onTop);
		Line(top - u * radius, bottom - u * radius, color, onTop);
		Line(top + v * radius, bottom + v * radius, color, onTop);
		Line(top - v * radius, bottom - v * radius, color, onTop);
	}

	/// <summary>Octahedral bone shape, which also shows the bone's roll around its own axis.</summary>
	public void Bone(Vector3 from, Vector3 to, Vector4 color, float widthFactor = 0.12f, bool onTop = false)
	{
		var delta = to - from;
		float length = delta.Length();

		if (length < 1e-6f)
		{
			return;
		}

		var direction = delta / length;
		Basis(direction, out var u, out var v);

		float width = length * widthFactor;
		var ringCenter = from + direction * (length * 0.2f);

		Span<Vector3> ring =
		[
			ringCenter + u * width,
			ringCenter + v * width,
			ringCenter - u * width,
			ringCenter - v * width,
		];

		for (int i = 0; i < 4; i++)
		{
			Line(from, ring[i], color, onTop);
			Line(ring[i], to, color, onTop);
			Line(ring[i], ring[(i + 1) & 3], color, onTop);
		}
	}

	private void HalfCircle(Vector3 center, Vector3 axisU, Vector3 axisV, float radius, Vector4 color,
		int segments, bool onTop)
	{
		if (segments < 2)
		{
			segments = 2;
		}

		var previous = center + axisU * radius;

		for (int i = 1; i <= segments; i++)
		{
			float angle = i / (float)segments * MathF.PI;
			var next = center + (axisU * MathF.Cos(angle) + axisV * MathF.Sin(angle)) * radius;

			Line(previous, next, color, onTop);
			previous = next;
		}
	}

	// Reference axis picked by smallest component; a fixed one degenerates for aligned directions.
	private static void Basis(Vector3 direction, out Vector3 u, out Vector3 v)
	{
		var absolute = Vector3.Abs(direction);

		var reference = absolute.X <= absolute.Y && absolute.X <= absolute.Z ? Vector3.UnitX
			: absolute.Y <= absolute.Z ? Vector3.UnitY
			: Vector3.UnitZ;

		u = Vector3.Normalize(Vector3.Cross(direction, reference));
		v = Vector3.Cross(direction, u);
	}

	private static bool IsFinite(Vector3 value) =>
		float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
