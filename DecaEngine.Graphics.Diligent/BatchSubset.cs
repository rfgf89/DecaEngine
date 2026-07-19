using UnsafeCollections.Collections.Native;

namespace DecaEngine.Graphics.Diligent;

public struct BatchSubset
{
	public NativeArray<IndirectInstance> instances;
	public NativeArray<DrawData> drawData;
	public NativeArray<GPURenderInstance> gpuData;

	public NativeArray<int> countData;

	public BatchSubset(NativeArray<IndirectInstance> instances, NativeArray<DrawData> drawData, NativeArray<GPURenderInstance> gpuData, NativeArray<int> countData)
	{
		this.instances = instances;
		this.drawData = drawData;
		this.gpuData = gpuData;
		this.countData = countData;
	}
}