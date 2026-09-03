using Friflo.Engine.ECS;
using System;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Entities;
using DecaEngine.Graphics;
// Transform here is Friflo's ECS component (world matrix), not DecaEngine.Core's TRS struct.
using Transform = Friflo.Engine.ECS.Transform;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Scene
{
    public class GpuInstanceBufferSystem() : QuerySystem<BatchRenderInfo, LinkDrawInfo>
    {
        protected override unsafe void OnUpdate()
        {
            CommandBuffer cb = Query.Store.GetCommandBuffer();

            // WorldTransformDirtyTag is set by TransformSystem, which runs earlier in the root.
            Query.AnyTags(Tags.Get<GpuUpdateTag, WorldTransformDirtyTag>()).ForEachEntity((ref BatchRenderInfo handle, ref LinkDrawInfo drawInfo, Entity entity) =>
            {
                Vector3 pos = entity.HasPosition ? entity.Position.value : Vector3.Zero;
                Quaternion rot = entity.HasRotation ? entity.Rotation.value : Quaternion.Identity;
                Vector3 scale = entity.HasScale3 ? entity.Scale3.value : Vector3.One;

                Matrix4x4 modelMatrix;
                if (entity.TryGetComponent<WorldMatrix>(out var worldMatrix))
                {
                    // Cull data must be world-space too, so take it from the world matrix.
                    modelMatrix = worldMatrix.value;
                    if (Matrix4x4.Decompose(modelMatrix, out var worldScale, out var worldRot, out var worldPos))
                    {
                        pos = worldPos;
                        rot = worldRot;
                        scale = worldScale;
                    }
                    else
                    {
                        pos = modelMatrix.Translation;
                        var bx = new Vector3(modelMatrix.M11, modelMatrix.M12, modelMatrix.M13);
                        var by = new Vector3(modelMatrix.M21, modelMatrix.M22, modelMatrix.M23);
                        var bz = new Vector3(modelMatrix.M31, modelMatrix.M32, modelMatrix.M33);
                        scale = new Vector3(bx.Length(), by.Length(), bz.Length());

                        // Rotation from the orthonormalized basis: approximate under shear.
                        var rx = scale.X > 1e-8f ? bx / scale.X : Vector3.UnitX;
                        var ry = scale.Y > 1e-8f ? by / scale.Y : Vector3.UnitY;
                        ry -= rx * Vector3.Dot(ry, rx);
                        ry = ry.LengthSquared() > 1e-12f ? Vector3.Normalize(ry) : Vector3.UnitY;
                        var rz = Vector3.Cross(rx, ry);
                        rot = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
                            rx.X, rx.Y, rx.Z, 0f,
                            ry.X, ry.Y, ry.Z, 0f,
                            rz.X, rz.Y, rz.Z, 0f,
                            0f, 0f, 0f, 1f));
                    }
                }
                else
                {
                    modelMatrix = MathUtils.CreateTrs(pos, rot, scale);
                }

                var gpuData = new Transform()
                {
                    value = modelMatrix,
                };

                drawInfo.drawData[0] = new DrawData()
                {
                    positionScale = new Vector4(pos, Math.Max(scale.X, Math.Max(scale.Y, scale.Z))),
                    orientation = rot.AsVector4(),
                    scale3 = new Vector4(scale, 0f),
                };

                drawInfo.renderInstance[0] = new GPURenderInstance()
                {
                    modelMatrix = modelMatrix,
                };

                if (entity.HasComponent<Transform>())
                {
                    entity.Set(gpuData);
                }

                cb.RemoveTag<GpuUpdateTag>(entity.Id);
                cb.RemoveTag<WorldTransformDirtyTag>(entity.Id);
            });

            cb.Playback();
        }
    }
}
