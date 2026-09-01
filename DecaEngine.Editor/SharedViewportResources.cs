using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Animation;
using DecaEngine.Graphics;

namespace DecaEngine.Editor;

/// <summary>
/// Device-level container of GPU resources shared by every <see cref="ModelViewportEnvironment"/> in
/// the process (Model Preview, Scene View, Icon Baker) - same ownership pattern as
/// <see cref="DecaEngine.Editor.ECS.ModelStore"/>: one instance created once (see
/// EditorManager._sharedViewportResources) and handed into each consumer's constructor, so a viewport
/// only ever loads MODELS, never its own copy of environment/sampler state. Owns:
///  - environment maps (IBL texture + CPU radiance + sun direction, see
///    <see cref="PreviewEnvironmentMap"/>), keyed by HDRI path ("" = procedural sky) - several HDRIs
///    can coexist, and switching to an already-seen one is a dictionary lookup instead of a
///    synchronous file decode + mip blur on the render thread;
///  - the "_EnvMap_Sampler" / "_SceneColor_Sampler" sampler objects, previously recreated once per
///    (model, environment) registration (see ModelViewportGeometry.RegisterModelResources) even
///    though they are pure, environment-independent sampler state.
///
/// No per-viewport state lives here. Consumers must NOT release anything obtained from this
/// container - <see cref="Release"/> (editor/harness shutdown only) is the sole owner.
/// </summary>
public sealed class SharedViewportResources
{
	private readonly IGraphicsApi _api;
	private readonly Dictionary<string, EnvironmentMapResult> _environments = new();

	/// <summary>Trilinear + Wrap - equirect seam wraps seamlessly, SampleLevel by roughness blends
	/// neighboring mips. Shared across every material of every environment (see
	/// ModelViewportGeometry.RegisterModelResources).</summary>
	public ISamplerObject EnvMapSampler { get; }

	/// <summary>Linear + Clamp for the scene-copy refraction source (_SceneColor) - clamped so the
	/// refractive UV offset past the screen edge stretches the border pixel instead of wrapping to
	/// the opposite side.</summary>
	public ISamplerObject SceneColorSampler { get; }

	public SharedViewportResources(IGraphicsApi api)
	{
		_api = api;

		EnvMapSampler = api.CreateSampler(
			name: "_EnvMap_Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Wrap,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);

		SceneColorSampler = api.CreateSampler(
			name: "_SceneColor_Sampler",
			filter: TextureFilter.Linear,
			address: TextureAddress.Clamp,
			comparisonFunction: CompFunction.Always,
			border: Vector4.Zero);
	}

	/// <summary>Resolves the shared environment for <paramref name="hdrPath"/> (null/empty =
	/// procedural sky), building and caching it on first use (see <see cref="PreviewEnvironmentMap.Create"/>).
	/// Every caller asking for the same path gets back the SAME GPU texture - callers must not
	/// release <see cref="EnvironmentMapResult.Texture"/> themselves, and must not mutate it (no
	/// per-environment binding exists on the texture itself - see class doc).</summary>
	public EnvironmentMapResult GetEnvironment(string? hdrPath)
	{
		var key = hdrPath ?? "";
		if (_environments.TryGetValue(key, out var cached))
		{
			return cached;
		}

		var result = PreviewEnvironmentMap.Create(_api, hdrPath);
		_environments[key] = result;
		return result;
	}

	/// <summary>Releases every cached environment texture and the shared samplers - editor/harness
	/// shutdown only. No <see cref="ModelViewportEnvironment"/> may still be alive when this runs
	/// (it holds raw references into <see cref="_environments"/> via its own EnvironmentMap
	/// property).</summary>
	public void Release()
	{
		foreach (var env in _environments.Values)
		{
			env.Texture.Release();
		}
		_environments.Clear();

		EnvMapSampler.Release();
		SceneColorSampler.Release();
	}
}
