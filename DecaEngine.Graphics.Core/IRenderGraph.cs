using System.Runtime.CompilerServices;

namespace DecaEngine.Core;

public abstract unsafe class RenderGraphPass<T>() : IRenderGraphPass
{
	public abstract string Name { get; }

	private T _passData;

	public abstract T Setup(IRenderGraphBuilder builder);

	public abstract void Execute(in T value, in IRenderGraphContext context);

	public void SetupPassData(IRenderGraphBuilder builder)
	{
		_passData = Setup(builder);
	}

	public void Execute(in IRenderGraphContext context)
	{
		Execute(_passData, context);
	}
}

public interface IRenderGraphPass
{
	public string Name { get; }

	public void SetupPassData(IRenderGraphBuilder builder);

	public void Execute(in IRenderGraphContext context);
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

	TextureResource PinTexture(RenderTargetInfo info);

	BufferResource PinBuffer(BufferInfo bufferInfo);

	void Allocate(int descIndex);
	void Release(int descIndex);
}

public interface IRenderGraphContext
{
	IGraphicsPipeline Pipeline { get; }

	void SetRenderTargets(TextureResource textureResource);

	void ClearRenderTarget(TextureResource textureResource, float r, float g, float b, float a);

	void SetPipelineState(PsoResource psoResource);

	void DrawIndexed(uint indicesStart, uint indicesCount, uint vertexStart, uint instanceStart, uint instanceCount, IndexType indexType);
}

public interface IRenderGraph
{
	void AddPass(IRenderGraphPass pass);

	void Compile();

	void Execute();
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