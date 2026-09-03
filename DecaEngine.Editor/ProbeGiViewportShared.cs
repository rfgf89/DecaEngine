using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.ProbeGi;

namespace DecaEngine.Editor;

/// <summary>Probe-GI behavior shared by both viewports (Inspector preview and Scene View);
/// anything added here applies to both so the viewports cannot drift apart.
/// Viewports still own object lifetime (session create/release, barriers, overlay order).</summary>
public static class ProbeGiViewportShared
{

	/// <summary>Runs probe rounds for one volume within the frame budget; returns rounds completed.
	/// The budget deliberately crosses round boundaries; fan directions and blend weight must be
	/// re-fetched inside the loop because AdvanceRound changes both.</summary>
	/// <param name="stopOnConverged">CLI harness passes false on purpose to measure worst-case pace.</param>
	public static int DriveChunks(ProbeRoundGpu gpu, ProbeGiBakeSession session,
		ProbeGiBaker baker, int chunkBudget, bool stopOnConverged = true)
	{
		int rounds = 0;
		for (int i = 0; i < chunkBudget; i++)
		{
			// Convergence must be re-checked every round, not just on entry.
			if (stopOnConverged && session.Converged)
			{
				break;
			}

			if (!gpu.RunRound(session, baker,
					ProbeGiBaker.RoundRayDirections(session),
					ProbeGiBaker.RoundBlendWeight(session)))
			{
				continue;
			}

			session.AdvanceRound();
			rounds++;

			// Queue depth reached: the fence does not move within one frame, so this caps
			// rounds per frame at exactly MaxRoundsInFlight.
			if (!gpu.IsReady)
			{
				break;
			}
		}

		return rounds;
	}

	/// <summary>Pushes the base-volume probe grid into the material cbuffer; Origin.w = 1 is the
	/// shader enable toggle. Cascade fields stay zero (cascades removed).</summary>
	/// <param name="viewBias">0..1 view fraction of the sample offset, packed into counts .w
	/// (see ProbeGiSampleBody).</param>
	public static void PushGrid(ref PreviewSettingsData data, ProbeGiTextures textures,
		float normalBias, float viewBias)
	{
		float bias = 0.75f * Math.Clamp(normalBias, 0f, 2f);
		float view = Math.Clamp(viewBias, 0f, 1f);

		data.ProbeGridOrigin = textures.GridOrigin;
		data.ProbeGridCell = new Vector4(
			textures.GridCell.X, textures.GridCell.Y, textures.GridCell.Z,
			textures.MinCellSize * bias);
		data.ProbeGridCounts = new Vector4(
			textures.GridCounts.X, textures.GridCounts.Y, textures.GridCounts.Z, view);
	}

	/// <summary>Keeps the probe debug overlay in sync with the toggle and atlas lifetime; any
	/// mismatch rebuilds it wholesale. failed latches compile errors for the session.</summary>
	internal static void PollOverlays(
		List<(ProbeDebugOverlay Overlay, ProbeGiTextures Textures)> overlays,
		bool want, ref bool failed, ModelViewportEnvironment env, IGraphicsApi graphicsApi,
		ProbeGiBakeSession? session, ProbeGiTextures? textures)
	{
		want = want && session != null && textures != null && !failed;

		bool matches = want && overlays.Count == 1
			&& ReferenceEquals(overlays[0].Textures, textures);

		if (overlays.Count > 0 && !matches)
		{
			ReleaseOverlays(overlays, env);
		}

		if (overlays.Count > 0)
		{
			// Refresh checks the layout version itself and is a no-op in a static frame.
			overlays[0].Overlay.Refresh(session!);
			return;
		}

		if (!want)
		{
			return;
		}

		try
		{
			var format = env.Pipeline.Targets?.RenderColorFormat ?? TextureObjectFormat.R8G8B8A8UNorm;
			overlays.Add((new ProbeDebugOverlay(env.DilApi, graphicsApi, env.BatchRenderer,
				session!, textures!, format), textures!));

			env.Pipeline.InlineOverlay = cmd =>
			{
				foreach (var entry in overlays)
				{
					entry.Overlay.Draw(cmd);
				}
			};
			env.Pipeline.InvalidateGraph();
		}
		catch (Exception ex)
		{
			failed = true;
			foreach (var entry in overlays)
			{
				entry.Overlay.Dispose();
			}

			overlays.Clear();
			EngineLog.Add(LogLevel.Error, $"Probe GI: debug overlay failed: {ex.Message}");
		}
	}

	/// <summary>Detach, rebuild the graph, then wait for GPU idle before disposing: the frozen
	/// command buffer from the previous frame still holds the overlay materials.</summary>
	internal static void ReleaseOverlays(
		List<(ProbeDebugOverlay Overlay, ProbeGiTextures Textures)> overlays,
		ModelViewportEnvironment env)
	{
		if (overlays.Count == 0)
		{
			return;
		}

		env.Pipeline.InlineOverlay = null;
		env.Pipeline.InvalidateGraph();
		env.DilApi.ImmediateContext.Flush();
		env.DilApi.ImmediateContext.WaitForIdle();
		foreach (var entry in overlays)
		{
			entry.Overlay.Dispose();
		}

		overlays.Clear();
	}

	/// <summary>Single place both viewports build bake options from the Graphics window settings.</summary>
	internal static ProbeGiBakeOptions BuildOptions(EditorSettings settings)
	{
		// VisRes is global atlas layout read by CPU packing and both shaders; it must be set
		// at session creation and never changed on a live session.
		ProbeGiBakeResult.VisRes = Math.Clamp(settings.ProbeGiVisRes,
			ProbeGiBakeResult.MinVisRes, ProbeGiBakeResult.MaxVisRes);

		var options = BuildOptionsCore(settings);

		// Cascades are removed: the budget formerly split across three volumes goes to the
		// single static grid (matches the RTXGI Sponza reference, infiniteScrolling off).
		options.MaxProbes = Math.Min(settings.ProbeGiMaxProbes * 3, ProbeGiBaker.MaxProbeBudget);
		options.GridDensity = settings.ProbeGiGridDensity * 1.45f;

		return options;
	}

	private static ProbeGiBakeOptions BuildOptionsCore(EditorSettings settings) => new()
	{
		RaysPerProbe = settings.ProbeGiRaysPerProbe,
		Bounces = settings.ProbeGiBounces,
		SkyIntensity = settings.ProbeGiSkyIntensity,
		BounceSaturation = settings.ProbeGiBounceSaturation,
		GridDensity = settings.ProbeGiGridDensity,
		MaxProbes = settings.ProbeGiMaxProbes,
		Realtime = settings.ProbeGiRealtime,
		RealtimeRaysPerRound = settings.ProbeGiRealtimeRays,
		RealtimeBlend = settings.ProbeGiRealtimeBlend,
		RealtimeMaxStep = settings.ProbeGiRealtimeMaxStep,
		RealtimeRelocation = settings.ProbeGiRealtimeRelocation,
		RealtimeGamma = settings.ProbeGiRealtimeGamma,
		RealtimeVariabilityThreshold = settings.ProbeGiVariabilityThreshold,
	};
}
