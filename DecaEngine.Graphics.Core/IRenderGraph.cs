using System.Runtime.CompilerServices;

using DecaEngine.Core;

namespace DecaEngine.Graphics;

public abstract unsafe class RenderGraphPass<T>() : IRenderGraphPass
{
	public abstract string Name { get; }

	/// <summary>См. <see cref="IRenderGraphPass.Enabled"/>. По умолчанию включён.</summary>
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

	/// <summary>Исполнять ли пасс. Учитывается при КОМПИЛЯЦИИ: выключенный пасс не проходит сетап, не
	/// участвует в графе зависимостей и не пишет команд - для графа его просто нет. Поэтому переходы
	/// состояний ресурсов расставляются между оставшимися пассами и выключать можно ЛЮБОЙ пасс,
	/// включая структурные.
	///
	/// Смена значения помечает граф на перекомпиляцию (см. IRenderGraph.SetPassEnabled). Она дешёвая:
	/// нативные текстуры и буферы переиспользуются из пула графа, пересобираются только командные
	/// буферы - см. IRenderGraph.TrimResourcePool о том, где VRAM реально возвращается.</summary>
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

	/// <summary>Объявляет графу УЖЕ СУЩЕСТВУЮЩИЙ таргет, созданный вне его (офскрин-таргеты конвейера,
	/// собственные таргеты пассов - см. <see cref="PipelineRenderTargets"/>, <see cref="SsaoPassResources"/>).
	/// Граф его не создаёт и не освобождает - он лишь узнаёт, кто его читает и пишет, и потому может
	/// строить настоящие рёбра зависимостей вместо порядка добавления, а окно отладки - показывать
	/// времена жизни и вес ресурсов кадра. Дальше с ним работают как с любым другим:
	/// <see cref="ReadTarget"/>/<see cref="WriteTarget"/>.
	///
	/// Повторный импорт того же таргета в другом пассе - норма и обязателен: именно из повторов и
	/// складывается граф. Вернёт тот же ресурс.</summary>
	TextureResource ImportTexture(IGpuTexture texture);

	BufferResource PinBuffer(BufferInfo bufferInfo);

	void Allocate(int descIndex);
	void Release(int descIndex);
}

public interface IRenderGraphContext
{
	IGraphicsApi Api { get; }

	/// <summary>
	/// Wraps a texture pinned/read via <see cref="IRenderGraphBuilder.ReadTarget"/> (or written via
	/// <see cref="IRenderGraphBuilder.WriteTarget"/>) as an <see cref="IGpuTexture"/>, so it can be
	/// bound into materials (<see cref="IMaterialObject.SetTexture"/>/<see cref="IComputeMaterial.SetTexture"/>)
	/// or passed to <see cref="ICommandBuffer"/> calls exactly like any other engine texture.
	/// Must be called from within a pass's Execute, after the graph has allocated the resource.
	/// The graph owns the returned wrapper's underlying native resource: never call
	/// <see cref="IReleaseObject.Release"/> on it, the render graph releases it on <c>Clean</c>.
	/// </summary>
	IGpuTexture GetTexture(TextureResource textureResource);

	/// <summary>
	/// Wraps a buffer pinned via <see cref="IRenderGraphBuilder.PinBuffer"/> as an <see cref="IBufferHandle"/>,
	/// so it can be bound into materials, vertex/index buffer slots, or any other <see cref="ICommandBuffer"/>
	/// call that expects an <see cref="IBufferHandle"/>.
	/// Must be called from within a pass's Execute, after the graph has allocated the resource.
	/// The graph owns the returned wrapper's underlying native resource: never call
	/// <see cref="IReleaseObject.Release"/> on it, the render graph releases it on <c>Clean</c>.
	/// </summary>
	IBufferHandle GetBuffer(BufferResource bufferResource);

	public ICommandBuffer cmd { get; }
}

public interface IRenderGraph : IReleaseObject
{
	void AddPass(IRenderGraphPass pass);

	void Compile();

	/// <summary>
	/// Forces the next <see cref="Execute"/> to recompile before running. Passes record their
	/// commands (and capture the native resource references they transition, e.g.
	/// <c>ForwardPass</c>'s color/depth target) only during <see cref="Compile"/>, then get frozen
	/// and merely replayed on every subsequent <see cref="Execute"/> - so if a pinned resource is
	/// disposed and recreated later (e.g. an off-screen render target resized to match its ImGui
	/// panel), the frozen commands would otherwise keep referencing the stale, disposed object.
	/// Call this after any such resource is resized/recreated so the graph re-records against the
	/// new one.
	/// </summary>
	void Invalidate();

	/// <summary>Включает/выключает пасс ПО ИМЕНИ (<see cref="IRenderGraphPass.Name"/>) на уже
	/// собранном графе. Изменение значения помечает граф на перекомпиляцию - выключенный пасс
	/// исключается из графа целиком, вместе со своими переходами состояний, так что тумблер
	/// безопасен для любого пасса (см. <see cref="IRenderGraphPass.Enabled"/>).
	/// Возвращает false, если пасса с таким именем в графе нет.</summary>
	bool SetPassEnabled(string name, bool enabled);

	/// <summary>Сносит СПИСОК ПАССОВ (в отличие от <see cref="IReleaseObject.Release"/> - нативные
	/// ресурсы при этом уходят в пул и переживут пересборку). Именно этим пользуется конвейер, когда
	/// пересобирает граф под новую сцену или новый набор фич: пассы создаются заново, а текстуры,
	/// буферы и вьюхи переиспользуются, пока совпадают их дескрипторы.
	/// Вызывающий обязан гарантировать, что кадры со старыми командами уже не в полёте.</summary>
	void ResetPasses();

	/// <summary>Освобождает нативные ресурсы, лежащие в пуле и не востребованные текущей компиляцией,
	/// - то есть VRAM выключенных фич и таргетов старого размера. Держать их в пуле дёшево по
	/// времени, но не по памяти: этот метод - точка, где вызывающий решает, что пора отдать.
	/// Вызывающий обязан дождаться GPU (Flush + WaitForIdle).</summary>
	void TrimResourcePool();

	/// <summary>Имена пассов текущего собранного графа, в порядке добавления - чтобы UI мог
	/// показать список, а не хардкодить его.</summary>
	IReadOnlyList<string> PassNames { get; }

	void Execute();

	/// <summary>
	/// Debug-only snapshot of the last executed frame (per-pass timings, draw call counts,
	/// resource lifetimes). Always null in Release builds. Populated at the end of <see cref="Execute"/>.
	/// </summary>
	RenderGraphDebugSnapshot DebugSnapshot { get; }

	/// <summary>
	/// Debug-only ring buffer of recent frame snapshots, for frame-time history graphs. Always
	/// null in Release builds.
	/// </summary>
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