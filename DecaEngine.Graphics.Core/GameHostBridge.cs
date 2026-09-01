using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Graphics;

public static class GameHostBridge
{
	public static readonly object GpuSync = new();

	public static IGraphicsApi? GraphicsApi { get; set; }

	public static IRenderHandle? RenderHandle { get; set; }

	public static EntityStore? EntityStore { get; set; }

	public static SystemRoot? SystemRoot { get; set; }

	public static bool IsHosted => GraphicsApi != null;

	public static event Action? GameReady;

	public static void NotifyGameReady() => GameReady?.Invoke();

	public static void Reset()
	{
		GraphicsApi = null;
		RenderHandle = null;
		EntityStore = null;
		SystemRoot = null;
		GameReady = null;
	}
}

public readonly struct GameContext
{
	public IGraphicsApi GraphicsApi { get; }

	public IRenderHandle? RenderHandle { get; }

	public EntityStore EntityStore { get; }

	public SystemRoot? SystemRoot { get; }

	public bool IsEditorHosted { get; }

	public GameContext(IGraphicsApi graphicsApi, IRenderHandle? renderHandle, EntityStore entityStore, SystemRoot? systemRoot, bool isEditorHosted)
	{
		GraphicsApi = graphicsApi;
		RenderHandle = renderHandle;
		EntityStore = entityStore;
		SystemRoot = systemRoot;
		IsEditorHosted = isEditorHosted;
	}
}

public abstract class GameBehaviour
{
	protected GameContext Context { get; private set; }

	public void InternalInitialize(GameContext context)
	{
		Context = context;
		OnInitialize();
	}

	public void InternalUpdate(float deltaTime) => OnUpdate(deltaTime);

	public void InternalFixedUpdate(float fixedDeltaTime) => OnFixedUpdate(fixedDeltaTime);

	public void InternalShutdown() => OnShutdown();

	protected abstract void OnInitialize();

	protected abstract void OnUpdate(float deltaTime);

	/// <summary>Called 0..N times per frame with a constant step (see TimeLoopCore.FixedTimeStep)
	/// before the variable-rate <see cref="OnUpdate"/>. Override for fixed-rate simulation
	/// (physics, deterministic gameplay). Default implementation does nothing.</summary>
	protected virtual void OnFixedUpdate(float fixedDeltaTime)
	{
	}

	protected abstract void OnShutdown();
}
