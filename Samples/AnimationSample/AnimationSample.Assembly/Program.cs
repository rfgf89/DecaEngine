using System.Numerics;
using DecaEngine;
using DecaEngine.Core;
using DecaEngine.Sdl;
using Friflo.Engine.ECS;
using AnimationSample;

namespace AnimationSample.Assembly;

internal sealed class GameHost : TimeLoopCore
{
	private readonly GameApplication _game = new();
	private IWindowHandle? _ownedWindowHandle;
	private IGraphicsApi? _ownedGraphicsApi;
	private SdlDevicePull? _ownedDevicePull;
	private IInputEventPull? _ownedInputEventPull;
	private bool _ownsGraphicsApi;

	protected override void OnStart()
	{
		IGraphicsApi graphicsApi;
		IRenderHandle? renderHandle;
		EntityStore entityStore;

		if (GameHostBridge.IsHosted)
		{
			graphicsApi = GameHostBridge.GraphicsApi!;
			renderHandle = GameHostBridge.RenderHandle;
			entityStore = GameHostBridge.EntityStore ?? new EntityStore();
			_ownsGraphicsApi = false;
		}
		else
		{
			_ownedWindowHandle = new SdlWindowHandle();
			_ownedWindowHandle.Initialize("AnimationSample", 0, new Vector2(1280, 720));

			_ownedGraphicsApi = new DiligentGraphicsApi(_ownedWindowHandle);
			_ownedGraphicsApi.Initialize(GraphicsBackend.Vulkan);

			_ownedDevicePull = new SdlDevicePull();
			_ownedInputEventPull = new SdlEventPull(_ownedWindowHandle, _ownedDevicePull);

			graphicsApi = _ownedGraphicsApi;
			renderHandle = null;
			entityStore = new EntityStore();
			_ownsGraphicsApi = true;
		}

		_game.InternalInitialize(new GameContext(graphicsApi, renderHandle, entityStore, GameHostBridge.SystemRoot, GameHostBridge.IsHosted));

		GameHostBridge.NotifyGameReady();
	}

	protected override void OnUpdate(float deltaTime)
	{
		if (_ownedInputEventPull != null && _ownedInputEventPull.PullEvent())
		{
			Quit();
			return;
		}

		_game.InternalUpdate(deltaTime);

		if (_ownsGraphicsApi)
		{
			_ownedGraphicsApi!.Present();
		}
	}

	protected override void OnFixedUpdate(float fixedDeltaTime)
	{
		_game.InternalFixedUpdate(fixedDeltaTime);
	}

	protected override void OnQuit()
	{
		_game.InternalShutdown();

		_ownedGraphicsApi?.Release();
		_ownedWindowHandle?.Release();
	}
}

public static class Program
{
	private static readonly GameHost _host = new();

	public static void Main(string[] args) => _host.Run();

	public static void Play() => _host.Play();

	public static void Pause() => _host.Pause();

	public static void Quit() => _host.Quit();
}