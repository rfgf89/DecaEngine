using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using Friflo.Engine.ECS;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Scene;

/// <summary>
/// Distributes the per-frame shadow-slice budget (LightClusters.MaxShadowSlices) to punctual
/// lights: a spot takes 1 slice, a point light 6 cube faces; nearest lights to the camera win.
/// The slice list is ALWAYS filled to full capacity: the frozen ForwardPass replays a fixed loop
/// over all slices, so dead slices must carry drawCount = 0.
/// </summary>
public static unsafe class PunctualShadowScheduler
{
    // Face order must match the dominant-axis face pick in UnlitInstancedPS.hlsl (+X,-X,+Y,-Y,+Z,-Z).
    private static readonly Vector3[] FaceDirs =
    [
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    ];

    private static readonly Vector3[] FaceUps =
    [
        Vector3.UnitY, Vector3.UnitY,
        Vector3.UnitZ, Vector3.UnitZ,
        Vector3.UnitY, Vector3.UnitY,
    ];

    // Frame scratch - render systems run on the main thread, statics are safe.
    private static readonly List<(Entity Entity, LightComponent Light, float DistSq)> Candidates = new();

    // Budget exhaustion is otherwise silent (light shines through walls); log only on change.
    private static int _lastReportedSkipped;

    /// <summary>DECA_PUNCTUAL_CULL=0 disables caster frustum culling in punctual shadow slices
    /// (diagnostic: these are the only consumers of GPU frustum culling in the engine).</summary>
    private static readonly bool FrustumCullCasters =
        Environment.GetEnvironmentVariable("DECA_PUNCTUAL_CULL") != "0";

    // Same toggle as LightCulling.DumpPunctualLight - the two dump halves print together.
    private static readonly bool DumpPunctual =
        Environment.GetEnvironmentVariable("DECA_PUNCTUAL_DUMP") == "1";

    private static readonly Dictionary<int, string> LastSchedulerDump = new();

    /// <summary>Fills the frame's shadow slices in <paramref name="target"/> and the light-entity
    /// to first-slice map in <paramref name="assignments"/>; target.punctualShadow* lists must be
    /// empty (cleared) on entry.</summary>
    public static void BuildShadowSlices(ArchetypeQuery<LightComponent> punctualLights, Vector3 cameraPos,
        int drawCount, ref RenderCamerasData target, Dictionary<int, int> assignments)
    {
        assignments.Clear();
        Candidates.Clear();

        punctualLights.ForEachEntity((ref LightComponent light, Entity entity) =>
        {
            if (light.ShadowStrength <= 0f || light.Intensity <= 0f || light.Range <= 0f ||
                light.Type is not (LightType.Point or LightType.Spot) ||
                !entity.HasComponent<Position>())
            {
                return;
            }

            // World position, not raw local Position: nested lights offset from their parent.
            LightCulling.GetWorldPositionRotation(entity, out var worldPos, out _);
            float distSq = Vector3.DistanceSquared(worldPos, cameraPos);
            Candidates.Add((entity, light, distSq));
        });

        Candidates.Sort(static (a, b) => a.DistSq.CompareTo(b.DistSq));

        int nextSlice = 0;
        int skipped = 0;
        foreach (var (entity, light, _) in Candidates)
        {
            int sliceCount = light.Type == LightType.Point ? 6 : 1;
            if (nextSlice + sliceCount > LightClusters.MaxShadowSlices)
            {
                // A point light did not fit - a later spot (1 slice) still might; keep scanning.
                skipped++;
                continue;
            }

            // Slices must render from the light's WORLD pose, or nested lights' shadows detach
            // from their own lighting (which reads world position).
            LightCulling.GetWorldPositionRotation(entity, out var position, out var rotation);
            float range = light.Range;
            float near = SliceNearPlane(range);

            if (light.Type == LightType.Spot)
            {
                var dir = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rotation));
                var up = MathF.Abs(dir.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;

                // Slice fov = full outer cone angle: the angular falloff already darkens the rim.
                float fov = Math.Clamp(light.SpotAngle, 1f, 179f) * (MathF.PI / 180f);
                AddSlice(ref target, position, position + dir, up, fov, near, range, drawCount);
            }
            else
            {
                // 2% overlap over 90 deg keeps PCF taps at face edges inside that face's map.
                float fov = MathF.PI * 0.5f * 1.02f;
                for (int face = 0; face < 6; face++)
                {
                    AddSlice(ref target, position, position + FaceDirs[face], FaceUps[face],
                        fov, near, range, drawCount);
                }
            }

            assignments[entity.Id] = nextSlice;

            // Second half of the LightCulling.DumpPunctualLight diagnostic: scheduler and pool
            // builder read the same LightComponent but decide independently and must not diverge.
            if (DumpPunctual)
            {
                // The viewProj w-column is the face axis in world space; expected to match FaceDirs[f].
                var axes = new System.Text.StringBuilder();
                for (int face = 0; face < sliceCount; face++)
                {
                    var m = *UnsafeArray.GetPtr<Matrix4x4>(target.punctualShadowMatrices, nextSlice + face);
                    axes.Append($" [{nextSlice + face}]=({m.M14:F3},{m.M24:F3},{m.M34:F3}|{m.M44:F3})");
                }

                var line = $"[punctual] entity={entity.Id} scheduler type={light.Type} " +
                    $"slices={sliceCount} base={nextSlice} pos=({position.X:F4},{position.Y:F4},{position.Z:F4}) " +
                    $"range={range:F4} near={near:F4} sliceAxis:{axes}";
                if (!LastSchedulerDump.TryGetValue(entity.Id, out var prev) || prev != line)
                {
                    LastSchedulerDump[entity.Id] = line;
                    Console.WriteLine(line);
                }
            }

            nextSlice += sliceCount;
        }

        if (skipped != _lastReportedSkipped)
        {
            _lastReportedSkipped = skipped;
            if (skipped > 0)
            {
                Console.WriteLine($"[shadows] {skipped} punctual light(s) with ShadowStrength > 0 got no shadow " +
                    $"slice: the budget of {LightClusters.MaxShadowSlices} slices is taken (a point light costs 6 " +
                    "slices, a spot 1; the nearest lights to the camera win). Those lights shine through geometry.");
            }
        }

        // Pad to full capacity with dead slices: drawCount = 0, culling passes nobody.
        while (target.punctualShadowCullData.Count < LightClusters.MaxShadowSlices)
        {
            int slice = target.punctualShadowCullData.Count;
            target.punctualShadowCullData.Add(default);
            target.punctualShadowLightData.Add(default);
            UnsafeArray.Set(target.punctualShadowMatrices, slice, Matrix4x4.Identity);
        }
    }

    /// <summary>Near plane of a shadow slice (far = range). SINGLE source of this value: both the
    /// slice projection and PunctualLight.ShadowParams.z read it - the shader must not re-derive
    /// it. Must stay strictly below far (perspective ctor throws at near >= far), hence the
    /// range*0.5 floor; capped at 0.25 so huge ranges do not clip nearby casters.</summary>
    public static float SliceNearPlane(float range) =>
        MathF.Min(Math.Clamp(range * 0.001f, 0.05f, 0.25f), range * 0.5f);

    private static void AddSlice(ref RenderCamerasData target, Vector3 eye, Vector3 lookAt, Vector3 up,
        float fov, float near, float far, int drawCount)
    {
        var view = Matrix4x4.CreateLookAtLeftHanded(eye, lookAt, up);
        // System.Numerics maps depth near->0, far->1 - same standard-Z convention as sun cascades.
        var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fov, 1f, near, far);
        var viewProj = view * proj;

        // Frustum planes from ROWS of the transposed projection - literally the same expression as
        // CameraComponent.CreateCullData; only the punctual slot ever reads these planes.
        var projT = Matrix4x4.Transpose(proj);
        var frustumX = projT[3] + projT[0];
        var frustumY = projT[3] + projT[1];
        MathUtils.NormalizePlane(ref frustumX);
        MathUtils.NormalizePlane(ref frustumY);

        var cullData = new CullData
        {
            view = view,
            frustum = new Vector4(frustumX.X, frustumX.Z, frustumY.Y, frustumY.Z),
            P00 = proj.M11,
            P11 = proj.M22,
            znear = near,
            zfar = far,
            drawCount = drawCount,
            // Bit 0 = frustum culling only, no LOD: shadows must match main-view geometry.
            cullFrustum = FrustumCullCasters ? 1 : 0,
        };

        int slice = target.punctualShadowCullData.Count;
        target.punctualShadowCullData.Add(cullData);
        target.punctualShadowLightData.Add(new LightData { CascadeMatrix0 = viewProj });
        UnsafeArray.Set(target.punctualShadowMatrices, slice, viewProj);
    }
}
