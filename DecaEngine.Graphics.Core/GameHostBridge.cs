using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace DecaEngine.Core;

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

	public void InternalShutdown() => OnShutdown();

	protected abstract void OnInitialize();

	protected abstract void OnUpdate(float deltaTime);

	protected abstract void OnShutdown();
}
