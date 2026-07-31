namespace DecaEngine.Core;

/// <summary>
/// Подтип Pipeline State, описываемый <see cref="IStateObject"/>.
/// Список расширяется по мере добавления новых видов пайплайнов backend'а
/// (RayTracing, Mesh, Tile и т.д.), не требуя изменения существующих контрактов.
/// </summary>
public enum PipelineStateType
{
	Unknown = 0,
	Graphics,
	Compute,
	RayTracing,
	Mesh,
	Tile,
}

/// <summary>
/// Базовый контракт для любого объекта, инкапсулирующего Pipeline State Object (PSO)
/// конкретного backend'а (например, DiligentEngine). Реализуется <see cref="IMaterialObject"/>
/// (Graphics) и <see cref="IComputeMaterial"/> (Compute), а также любыми будущими
/// специализированными состояниями (RayTracing, Mesh, Tile), позволяя работать с ними
/// через единый абстрактный тип независимо от конкретного подтипа пайплайна.
/// </summary>
public interface IStateObject : IReleaseObject
{
	public string Name { get; }

	/// <summary>Подтип pipeline state, который описывает данный объект.</summary>
	public PipelineStateType StateType { get; }
}
