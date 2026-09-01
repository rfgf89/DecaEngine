using System.Collections.Generic;
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

	/// <summary>Привязка TLAS для inline-трассировки в пиксельном шейдере (RT-тени материалов,
	/// RT-фолбэк SSR - см. SsrPassResources.SetRayScene). Дефолт - no-op: реализует только
	/// Diligent-бэкенд (DiligentMaterial.SetAccelStructure, стадия строго пиксельная).</summary>
	public void SetAccelStructure(string name, ITopLevelAS tlas) { }

	/// <summary>Привязка «сырого» структурированного Diligent-буфера как SRV пиксельной стадии
	/// (таблицы атрибутов сцены для RT-фолбэка SSR). Дефолт - no-op, как у
	/// <see cref="SetAccelStructure"/>.</summary>
	public void SetStructuredBufferSrv(string name, IBuffer buffer) { }

	/// <summary>Привязка массива текстур в одну шейдерную переменную `Texture2D name[N]` (SRV
	/// пиксельной стадии) - «bindless»-текстуры RT-хитов SSR. Число элементов обязано совпадать с N
	/// в шейдере, каждый слот - живой Texture2D (свободные добиваются плейсхолдером). Дефолт -
	/// no-op, как у <see cref="SetAccelStructure"/>.</summary>
	public void SetTextureSrvArray(string name, IReadOnlyList<IGpuTexture> textures) { }
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
