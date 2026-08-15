using Friflo.Engine.ECS;
using System;
using System.Numerics;
using DecaEngine.Core.Entities;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor.ECS
{
    public class GpuInstanceBufferSystem() : QuerySystem<BatchRenderInfo, LinkDrawInfo>
    {
        protected override unsafe void OnUpdate()
        {
            CommandBuffer cb = Query.Store.GetCommandBuffer();

            // GpuUpdateTag: legacy/editor-driven refresh. WorldTransformDirtyTag: set by the Core
            // TransformSystem (runs earlier in the SystemRoot) when the entity's world transform
            // was recomputed this frame - either tag means the instance data must be re-uploaded.
            Query.AnyTags(Tags.Get<GpuUpdateTag, WorldTransformDirtyTag>()).ForEachEntity((ref BatchRenderInfo handle, ref LinkDrawInfo drawInfo, Entity entity) =>
            {
                Vector3 pos = entity.HasPosition ? entity.Position.value : Vector3.Zero;
                Quaternion rot = entity.HasRotation ? entity.Rotation.value : Quaternion.Identity;
                Vector3 scale = entity.HasScale3 ? entity.Scale3.value : Vector3.One;

                Matrix4x4 modelMatrix;
                if (entity.TryGetComponent<WorldMatrix>(out var worldMatrix))
                {
                    // Hierarchy path: TransformSystem stored the composed world matrix. The culling
                    // data below (positionScale/orientation) must be world-space as well, so pull
                    // it out of the matrix; on a non-decomposable matrix (e.g. skewed by extreme
                    // non-uniform parent scale) keep the local TRS as a best-effort fallback.
                    modelMatrix = worldMatrix.value;
                    if (Matrix4x4.Decompose(modelMatrix, out var worldScale, out var worldRot, out var worldPos))
                    {
                        pos = worldPos;
                        rot = worldRot;
                        scale = worldScale;
                    }
                }
                else
                {
                    // No hierarchy involvement (root entity): own TRS is the world transform.
                    modelMatrix = DecaEngine.Graphics.Diligent.MathUtils.CreateTrs(pos, rot, scale);
                }

                var gpuData = new Transform()
                {
                    value = modelMatrix,
                };

                drawInfo.drawData[0] = new DrawData()
                {
                    positionScale = new Vector4(pos, Math.Max(scale.X, Math.Max(scale.Y, scale.Z))),
                    orientation = rot.AsVector4(),
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
