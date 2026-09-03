using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core.Entities;
using Friflo.Engine.ECS;
using DecaEngine.Graphics;

namespace DecaEngine.Scene;

/// <summary>
/// CPU frustum culling for punctual lights, one test geometry per light type.
/// Runs in camera view space on the compact frustum of <see cref="CullData"/>
/// (frustum = (fx.X, fx.Z, fy.Y, fy.Z)); the GPU half lives in LightClusterCS.hlsl.
/// </summary>
public static class LightCulling
{
    /// <summary>Point light: range sphere against the frustum.</summary>
    public static bool IsPointLightVisible(in CullData cullData, in Vector3 viewPos, float range)
    {
        Vector4 f = cullData.frustum;
        bool visible = viewPos.Z * f.Y - MathF.Abs(viewPos.X) * f.X > -range;
        visible &= viewPos.Z * f.W - MathF.Abs(viewPos.Y) * f.Z > -range;
        visible &= viewPos.Z + range > cullData.znear && viewPos.Z - range < cullData.zfar;
        return visible;
    }

    /// <summary>Spot light: bounding-sphere reject, then an exact per-plane cone test.</summary>
    public static bool IsSpotLightVisible(in CullData cullData, in Vector3 apexView, in Vector3 dirView,
        float range, float outerHalfAngleRad)
    {
        float cosOuter = MathF.Cos(outerHalfAngleRad);
        float sinOuter = MathF.Sin(outerHalfAngleRad);

        // Minimal bounding sphere of a cone of height range and half-angle a:
        // a <= 45deg: centre on the axis at t = range/(2cos^2 a), radius t;
        // a  > 45deg: centre at the base, radius range*tan a.
        float sphereRadius;
        Vector3 sphereCenter;
        if (outerHalfAngleRad <= MathF.PI * 0.25f)
        {
            float t = range / MathF.Max(2.0f * cosOuter * cosOuter, 1e-4f);
            sphereCenter = apexView + dirView * t;
            sphereRadius = t;
        }
        else
        {
            sphereCenter = apexView + dirView * range;
            sphereRadius = range * (sinOuter / MathF.Max(cosOuter, 1e-4f));
        }

        if (!IsPointLightVisible(in cullData, in sphereCenter, sphereRadius))
        {
            return false;
        }

        // Plane normals point INTO the frustum; the side planes pass through the view origin.
        Vector4 f = cullData.frustum;
        float baseRadius = range * (sinOuter / MathF.Max(cosOuter, 1e-4f));

        Span<Vector4> planes =
        [
            new Vector4(f.X, 0f, f.Y, 0f),             // left
            new Vector4(-f.X, 0f, f.Y, 0f),            // right
            new Vector4(0f, f.Z, f.W, 0f),             // bottom
            new Vector4(0f, -f.Z, f.W, 0f),            // top
            new Vector4(0f, 0f, 1f, -cullData.znear),  // near
            new Vector4(0f, 0f, -1f, cullData.zfar),   // far
        ];

        foreach (ref readonly var plane in planes)
        {
            if (ConeBehindPlane(in apexView, in dirView, range, baseRadius, in plane))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Directional lights are infinite and never culled.</summary>
    public static bool IsDirectionalLightVisible() => true;

    /// <summary>Builds a punctual light record for the probe baker: no culling, no shadow slices.
    /// Angle and direction formulas mirror <see cref="TryBuildPunctualLight"/>; change both.</summary>
    public static bool TryBuildBakeLight(ref LightComponent light, Entity lightEntity,
        out PunctualLight punctualLight)
    {
        punctualLight = default;

        if (light.Intensity <= 0f || light.Range <= 0f || !lightEntity.HasComponent<Position>())
        {
            return false;
        }

        GetWorldPositionRotation(lightEntity, out Vector3 worldPos, out Quaternion worldRot);

        switch (light.Type)
        {
            case LightType.Point:
                punctualLight = new PunctualLight
                {
                    PositionRange = new Vector4(worldPos, light.Range),
                    ColorIntensity = new Vector4(light.Color, light.Intensity),
                };
                return true;

            case LightType.Spot:
            {
                Vector3 dirWorld = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, worldRot));
                float outerHalfRad = Math.Clamp(light.SpotAngle, 1f, 179f) * (MathF.PI / 360f);
                float innerFullDeg = light.InnerSpotAngle > 0f
                    ? MathF.Min(light.InnerSpotAngle, light.SpotAngle)
                    : light.SpotAngle * 0.8f;
                float cosOuter = MathF.Cos(outerHalfRad);
                float cosInner = MathF.Cos(Math.Clamp(innerFullDeg, 0f, 179f) * (MathF.PI / 360f));

                punctualLight = new PunctualLight
                {
                    PositionRange = new Vector4(worldPos, light.Range),
                    ColorIntensity = new Vector4(light.Color, light.Intensity),
                    DirectionType = new Vector4(dirWorld, 1f),
                    SpotAngles = new Vector4(cosOuter, 1f / MathF.Max(cosInner - cosOuter, 1e-4f),
                        MathF.Sin(outerHalfRad), 0f),
                };
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>Culls a punctual light against the camera frustum and, if visible, builds its GPU
    /// record. shadowSlices maps entity id to first slice; a light outside it casts no shadow.</summary>
    public static bool TryBuildPunctualLight(ref LightComponent light, Entity lightEntity,
        in CullData cullData, IReadOnlyDictionary<int, int> shadowSlices, out PunctualLight punctualLight)
    {
        punctualLight = default;

        if (light.Intensity <= 0f || light.Range <= 0f || !lightEntity.HasComponent<Position>())
        {
            return false;
        }

        GetWorldPositionRotation(lightEntity, out Vector3 worldPos, out Quaternion worldRot);
        Vector3 viewPos = Vector3.Transform(worldPos, cullData.view);

        // z is the slice near plane straight from PunctualShadowScheduler: the shader must be
        // handed it rather than re-deriving it from Range.
        var shadowParams = shadowSlices.TryGetValue(lightEntity.Id, out var firstSlice)
            ? new Vector4(firstSlice, Math.Clamp(light.ShadowStrength, 0f, 1f),
                PunctualShadowScheduler.SliceNearPlane(light.Range),
                light.SourceRadius > 0f ? light.SourceRadius : 0f)
            : new Vector4(-1f, 0f, 0f, 0f);

        switch (light.Type)
        {
            case LightType.Point:
            {
                if (!IsPointLightVisible(in cullData, in viewPos, light.Range))
                {
                    return false;
                }

                punctualLight = new PunctualLight
                {
                    PositionRange = new Vector4(worldPos, light.Range),
                    ColorIntensity = new Vector4(light.Color, light.Intensity),
                    DirectionType = new Vector4(0f, 0f, 0f, 0f),
                    SpotAngles = Vector4.Zero,
                    ShadowParams = shadowParams,
                };
                return true;
            }

            case LightType.Spot:
            {
                // Direction convention as for the sun: entity local +Z (LH camera, forward = +Z).
                Vector3 dirWorld = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, worldRot));
                Vector3 dirView = Vector3.Normalize(Vector3.TransformNormal(dirWorld, cullData.view));

                // SpotAngle is the FULL outer angle in degrees; the tests use the half-angle.
                float outerHalfRad = Math.Clamp(light.SpotAngle, 1f, 179f) * (MathF.PI / 360f);
                if (!IsSpotLightVisible(in cullData, in viewPos, in dirView, light.Range, outerHalfRad))
                {
                    return false;
                }

                float innerFullDeg = light.InnerSpotAngle > 0f
                    ? MathF.Min(light.InnerSpotAngle, light.SpotAngle)
                    : light.SpotAngle * 0.8f;
                float cosOuter = MathF.Cos(outerHalfRad);
                float cosInner = MathF.Cos(Math.Clamp(innerFullDeg, 0f, 179f) * (MathF.PI / 360f));

                punctualLight = new PunctualLight
                {
                    PositionRange = new Vector4(worldPos, light.Range),
                    ColorIntensity = new Vector4(light.Color, light.Intensity),
                    DirectionType = new Vector4(dirWorld, 1f),
                    SpotAngles = new Vector4(cosOuter, 1f / MathF.Max(cosInner - cosOuter, 1e-4f),
                        MathF.Sin(outerHalfRad), 0f),
                    ShadowParams = shadowParams,
                };
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>Depth range of the exponential cluster grid, derived from the punctual light
    /// segment (view-depth z +/- R per light) rather than the camera near/far, which would waste
    /// almost every slice on depths no light reaches.</summary>
    public static Vector2 ClusterDepthRange(in CullData cullData, float minLightZ, float maxLightZ)
    {
        float cameraNear = MathF.Max(cullData.znear, 0.01f);
        if (minLightZ > maxLightZ)
        {
            // Empty segment: still no degenerate range, the shader computes log2(far/near) always.
            return new Vector2(cameraNear, MathF.Max(cullData.zfar, cameraNear * 2f));
        }

        float near = MathF.Max(cameraNear, minLightZ);
        float far = MathF.Max(maxLightZ, near * 2f);
        return new Vector2(near, far);
    }

    // DECA_PUNCTUAL_DUMP=1 prints each light's GPU record as UnlitInstancedPS will read it,
    // and only when the line changes.
    private static readonly bool DumpPunctual =
        Environment.GetEnvironmentVariable("DECA_PUNCTUAL_DUMP") == "1";

    private static readonly Dictionary<int, string> LastDump = new();

    /// <summary>Call after <see cref="TryBuildPunctualLight"/> so the dump matches the buffer.</summary>
    public static void DumpPunctualLight(Entity lightEntity, in LightComponent light, in PunctualLight gpu)
    {
        if (!DumpPunctual)
        {
            return;
        }

        var line = $"[punctual] entity={lightEntity.Id} type={light.Type} " +
            $"pos=({gpu.PositionRange.X:F4},{gpu.PositionRange.Y:F4},{gpu.PositionRange.Z:F4}) range={gpu.PositionRange.W:F4} " +
            $"dir=({gpu.DirectionType.X:F3},{gpu.DirectionType.Y:F3},{gpu.DirectionType.Z:F3}) dirType.w={gpu.DirectionType.W:F1} " +
            $"spot(cosOuter={gpu.SpotAngles.X:F4},scale={gpu.SpotAngles.Y:F3},sinOuter={gpu.SpotAngles.Z:F4}) " +
            $"shadow(slice={gpu.ShadowParams.X:F0},strength={gpu.ShadowParams.Y:F3},near={gpu.ShadowParams.Z:F4})";

        if (LastDump.TryGetValue(lightEntity.Id, out var prev) && prev == line)
        {
            return;
        }

        LastDump[lightEntity.Id] = line;
        Console.WriteLine(line);
    }

    /// <summary>World position and rotation of a light: decomposes WorldMatrix when present,
    /// since a parented light's local TRS is not its world placement.</summary>
    public static void GetWorldPositionRotation(Entity entity, out Vector3 position, out Quaternion rotation)
    {
        position = entity.HasComponent<Position>() ? entity.Position.value : Vector3.Zero;
        rotation = entity.HasComponent<Rotation>() ? entity.Rotation.value : Quaternion.Identity;

        if (entity.TryGetComponent<WorldMatrix>(out var worldMatrix) &&
            Matrix4x4.Decompose(worldMatrix.value, out _, out var worldRot, out var worldPos))
        {
            position = worldPos;
            rotation = worldRot;
        }
    }

    // True when both the apex and the base-rim point nearest the plane lie behind it.
    private static bool ConeBehindPlane(in Vector3 apex, in Vector3 dir, float range, float baseRadius,
        in Vector4 plane)
    {
        var n = new Vector3(plane.X, plane.Y, plane.Z);

        float apexDist = Vector3.Dot(n, apex) + plane.W;
        if (apexDist >= 0f)
        {
            return false;
        }

        Vector3 m = Vector3.Cross(Vector3.Cross(n, dir), dir);
        float mLen = m.Length();
        // Degenerate m means the cone axis is parallel to the normal: the base centre suffices.
        Vector3 q = mLen > 1e-6f
            ? apex + dir * range - (m / mLen) * baseRadius
            : apex + dir * range;

        return Vector3.Dot(n, q) + plane.W < 0f;
    }
}
