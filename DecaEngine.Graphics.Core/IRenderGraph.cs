using System.Runtime.CompilerServices;

using DecaEngine.Core;

namespace DecaEngine.Graphics;

public abstract unsafe class RenderGraphPass<T>() : IRenderGraphPass
{
	public abstract string Name { get; }

	public bool Enabled { get; set; } = true;

	private T _passData;

	public abstract T Setup(IRenderGraphBuilder builder);

	public abstract void WriteCommands(in T value, in IRenderGraphContext context);

	public void SetupPassData(IRenderGraphBuilder builder)
	{
		_passData = Setup(builder);
	}

	public void WriteCommands(in IRenderGraphContext context)
	{
		WriteCommands(_passData, context);
	}

	public virtual void EarlyCommands()
	{

	}
}

public interface IRenderGraphPass
{
	public string Name { get; }

	/// <summary>Whether to run the pass; honoured at compile time, so a disabled pass is absent from
	/// the graph entirely and resource transitions are placed between the surviving passes.</summary>
	public bool Enabled { get; set; }

	public void SetupPassData(IRenderGraphBuilder builder);

	public void WriteCommands(in IRenderGraphContext context);

	public void EarlyCommands();
}

public struct Target<T>
{
	public GraphId id;
	public T value;

	public Target(GraphId id)
	{
		this.id = id;
		value = default(T);
	}

	public Target(GraphId id, T value)
	{
		this.id = id;
		this.value = value;
	}
}

public interface IRenderGraphBuilder
{
	TextureResource WriteTarget(TextureResource textureResource);

	TextureResource ReadTarget(TextureResource textureResource);

	BufferResource WriteBuffer(BufferResource bufferResource);

	BufferResource ReadBuffer(BufferResource bufferResource);

	TextureResource PinTexture(TextureInfo info);

	/// <summary>Declares an externally owned target to the graph; the graph never creates or releases
	/// it. Re-importing the same target from another pass is required - that is what builds the edges.</summary>
	TextureResource ImportTexture(IGpuTexture texture);

	BufferResource PinBuffer(BufferInfo bufferInfo);

	void Allocate(int descIndex);
	void Release(int descIndex);
}

public interface IRenderGraphContext
{
	IGraphicsApi Api { get; }

	/// <summary>Wraps a graph texture as an <see cref="IGpuTexture"/>; call only from a pass's Execute
	/// and never Release the result - the graph owns it.</summary>
	IGpuTexture GetTexture(TextureResource textureResource);

	/// <summary>Wraps a graph buffer as an <see cref="IBufferHandle"/>; call only from a pass's Execute
	/// and never Release the result - the graph owns it.</summary>
	IBufferHandle GetBuffer(BufferResource bufferResource);

	public ICommandBuffer cmd { get; }
}

public interface IRenderGraph : IReleaseObject
{
	void AddPass(IRenderGraphPass pass);

	void Compile();

	/// <summary>Forces a recompile before the next <see cref="Execute"/>; mandatory after any pinned
	/// resource is resized or recreated, since frozen commands hold its native reference.</summary>
	void Invalidate();

	/// <summary>Toggles a pass by name on an already built graph; false if no such pass exists.</summary>
	bool SetPassEnabled(string name, bool enabled);

	/// <summary>Drops the pass list, keeping native resources pooled; caller must ensure no frame with
	/// the old commands is still in flight.</summary>
	void ResetPasses();

	/// <summary>Frees pooled native resources unused by the current compile; caller must Flush + WaitForIdle first.</summary>
	void TrimResourcePool();

	/// <summary>Pass names of the current graph, in the order they were added.</summary>
	IReadOnlyList<string> PassNames { get; }

	void Execute();

	/// <summary>Snapshot of the last executed frame; always null in Release builds.</summary>
	RenderGraphDebugSnapshot DebugSnapshot { get; }

	/// <summary>Ring buffer of recent frame snapshots; always null in Release builds.</summary>
	RenderGraphDebugHistory DebugHistory { get; }
}

public readonly struct GraphId : IEquatable<GraphId>, IEqualityComparer<GraphId>
{
	public readonly int id;
	public readonly string name;

	public GraphId(string name)
	{
		id = name.GetHashCode();
		this.name = name;
	}

	public static implicit operator GraphId(string name)
	{
		return new GraphId(name);
	}

	public override int GetHashCode()
	{
		return id;
	}

	public bool Equals(GraphId other)
	{
		return id == other.id;
	}

	public override bool Equals(object? obj)
	{
		return obj is GraphId other && Equals(other);
	}

	public bool Equals(GraphId x, GraphId y)
	{
		return x.id == y.id;
	}

	public int GetHashCode(GraphId obj)
	{
		return HashCode.Combine(obj.id, obj.name);
	}
}

public enum IndexType : byte
{
	Undefined,
	Int8,
	Int16,
	Int32,
	UInt8,
	UInt16,
	UInt32,
	Float16,
	Float32,
	Float64,
	NumTypes,
}

public interface IGraphResource
{
	public GraphId Id { get; }
}

public struct TextureResource(GraphId id, int bindId) : IGraphResource
{
	public GraphId Id { get; } = id;
	public int bindId { get; } = bindId;
}

public struct BufferResource(GraphId id) : IGraphResource
{
	public GraphId Id { get; } = id;
}

public struct PsoResource(GraphId id) : IGraphResource
{
	public GraphId Id { get; } = id;
}