using System.Numerics;
using DecaEngine;
using DecaEngine.Core;

namespace AnimationSample;

public class GameApplication : GameBehaviour
{
	private float _time;

	protected override void OnInitialize()
	{
		_time = 0f;
	}

	protected override void OnUpdate(float deltaTime)
	{
		_time += deltaTime;

		var hue = (_time * 0.15f) % 1.0f;
		var color = HsvToRgb(hue, 0.65f, 0.9f);

		lock (GameHostBridge.GpuSync)
		{
			var cmd = Context.GraphicsApi.CreateCommandBuffer();
			cmd.BeginRecording();

			if (Context.RenderHandle is IGpuTexture handleTexture)
			{
				cmd.ClearRenderTarget(handleTexture, new Vector4(color, 1.0f));
			}
			else
			{
				cmd.SetBackBufferTarget(Context.GraphicsApi);
				cmd.ClearBackBufferTarget(Context.GraphicsApi, new Vector4(color, 1.0f));
			}

			cmd.EndRecording();
			cmd.Execute();
		}
	}

	protected override void OnShutdown()
	{
	}

	private static Vector3 HsvToRgb(float h, float s, float v)
	{
		int i = (int)(h * 6.0f);
		float f = h * 6.0f - i;
		float p = v * (1.0f - s);
		float q = v * (1.0f - f * s);
		float t = v * (1.0f - (1.0f - f) * s);

		return (i % 6) switch
		{
			0 => new Vector3(v, t, p),
			1 => new Vector3(q, v, p),
			2 => new Vector3(p, v, t),
			3 => new Vector3(p, q, v),
			4 => new Vector3(t, p, v),
			_ => new Vector3(v, p, q),
		};
	}
}