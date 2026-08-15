using DecaEngine.Graphics.Core;
using Diligent;

namespace DecaEngine.Core;

public interface ISamplerObject : IReleaseObject { }

public struct MaterialDrawRange
{
	public uint FirstDrawIndex;
	public uint DrawCount;
}

public interface IMaterialObject : IStateObject
{
	/// <summary>Освобождает ли <see cref="IReleaseObject.Release"/> этого материала свои шейдеры.
	///
	/// По умолчанию true, и это верно для подавляющего большинства материалов движка: каждый пасс
	/// заводит СВОЙ экземпляр шейдера именно затем, чтобы материал мог его освободить (см.
	/// SsaoPassResources и родственные - там об этом прямо написано).
	///
	/// Но у загрузчика моделей всё наоборот: один вершинный шейдер и горстка вариантов пиксельного
	/// ШАРЯТСЯ между всеми материалами модели - ради этого вариантный кэш и существует, компиляция
	/// стоит сотни миллисекунд. Освобождение шейдера материалом - это декремент счётчика ссылок
	/// НАТИВНОГО объекта, и двадцать пять материалов на одну ссылку уводят счётчик в минус: объект
	/// уничтожается на первых вызовах, а следующие бьют в освобождённую память
	/// (0xC0000005 в Diligent.ComObject.Release). Такие материалы обязаны выставлять false, а
	/// владелец шейдеров освобождает их сам, по одному разу (см. ModelLoader.Release).</summary>
	bool OwnsShaders { get; set; }

	PipelineStateType IStateObject.StateType => PipelineStateType.Graphics;

	public void SetState(IStateObject stateObject);

	public void SetShader(IShaderObject shaderObject);
	public void SetShader(params IShaderObject[] shaders);

	public void SetConstant<T>(string name, ref T data, HandleAccess access = HandleAccess.Pixel) where T : unmanaged;

	public void SetConstant<T>(int ctx, string name, ref T data, HandleAccess access = HandleAccess.Pixel) where T : unmanaged;

	public void SetBuffer(string name, IBufferHandle bufferHandle, HandleAccess access = HandleAccess.Pixel);

	public void SetTexture(string name, IGpuTexture texture, HandleAccess access = HandleAccess.Pixel);

	public void SetSampler(string name, ISamplerObject sampler, HandleAccess access = HandleAccess.Pixel);
	public void SetImmutableSampler(string name, ISamplerObject sampler, HandleAccess access = HandleAccess.Pixel);
}

// ?????: ????????? ????????? ??? Compute
public interface IComputeMaterial : IStateObject
{
	PipelineStateType IStateObject.StateType => PipelineStateType.Compute;

	public void SetState(IStateObject stateObject);

	public void SetShader(IShaderObject computeShader);

	public void SetConstant<T>(string name, ref T data) where T : unmanaged;

	public void SetConstant<T>(int ctx, string name, ref T data) where T : unmanaged;

	public void SetBuffer(string name, IBufferHandle bufferHandle);

	public void SetTexture(string name, IGpuTexture texture, bool isUnorderedAccess = false);

	public void SetSampler(string name, ISamplerObject sampler);

	public void Dispatch(uint threadGroupCountX, uint threadGroupCountY, uint threadGroupCountZ);
}
