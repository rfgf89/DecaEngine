using Friflo.Engine.ECS;
using System;
using System.Numerics;
using DecaEngine.Graphics.Diligent;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Editor.ECS
{
    public class GpuInstanceBufferSystem() : QuerySystem<BatchRenderInfo, LinkDrawInfo>
    {
        protected override unsafe void OnUpdate()
        {
            CommandBuffer cb = Query.Store.GetCommandBuffer();

            Query.AnyTags(Tags.Get<GpuUpdateTag>()).ForEachEntity((ref BatchRenderInfo handle, ref LinkDrawInfo drawInfo, Entity entity) =>
            {
                Vector3 pos = entity.Position.value;
                Quaternion rot = entity.Rotation.value;
                Vector3 scale = entity.Scale3.value;

                Matrix4x4 modelMatrix = MathUtils.CreateTrs(pos, rot, scale);

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
            });

            cb.Playback();
        }
    }
}