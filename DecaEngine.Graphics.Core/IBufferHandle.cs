using DecaEngine.Core;

public interface IBufferHandle : IReleaseObject
{
	public uint SizeInBytes { get; }
	public BufferInfo Info { get; }

	public void Alloc(BufferInfo info);
}

public struct BufferInfo
{
	public string name;
	public uint sizeInBytes;
	public uint stride;

	public BufferHandleType type;
	public HandleAccess access;
	public bool dynamic; // If true, the buffer is updated frequently (CPU write access)
}

public enum BufferHandleType : int
{
	Structured = 0,
	Constant = 4,
	Vertex = 1,
	Index = 2,
	IndirectArgs = 256
}

[Flags]
public enum HandleAccess : int
{
	Compute = 1,
	Pixel = 2,
	Vertex = 4,
}