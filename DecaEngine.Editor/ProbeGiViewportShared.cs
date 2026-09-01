using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Graphics;
using DecaEngine.Graphics.ProbeGi;

namespace DecaEngine.Editor;

/// <summary>
/// Общая логика probe-GI обоих вьюпортов (превью Инспектора и Scene View) - всё, что обязано
/// СОВПАДАТЬ, чтобы вьюпорты не расходились по фичам: привод раундов базового объёма и каскадов,
/// геометрия каскадов (боксы/кламп/гистерезис перецентровки), пуш сеток в кбуфер материалов и
/// возврат плейсхолдеров в каскадные слоты.
///
/// Вьюпорты остаются владельцами ЖИЗНИ объектов (когда создавать/освобождать сессию, барьеры,
/// порядок с оверлеем) - у них разные источники моделей, поз и точек интереса; здесь живёт
/// поведение, которое от вьюпорта не зависит. Новая фича раунда/каскада добавляется сюда ОДИН раз.
/// </summary>
public static class ProbeGiViewportShared
{

	/// <summary>Выпускает порции ОДНОГО объёма в пределах кадрового бюджета - единственная
	/// реализация этого цикла на все три привода (превью, сцена, CLI).
	///
	/// Бюджет тратится ЦЕЛИКОМ, переходя границы раундов. Раньше цикл обрывался на первом же
	/// завершённом раунде, и потолок был «раунд в кадр» независимо от того, сколько работы GPU
	/// реально успевает; вместе с сериализующим забором (см. ProbeRoundGpu.MaxRoundsInFlight) это
	/// давало «раунд в два кадра» и съедало ровно тот выигрыш, ради которого трассировка и
	/// аппаратная: 23-кратное ускорение самого обхода превращалось в полуторакратное на сходимости.
	///
	/// Веер и вес берутся ВНУТРИ цикла: после AdvanceRound это уже другой раунд - со своим сдвинутым
	/// набором направлений и своим весом смешивания.</summary>
	/// <returns>Сколько раундов ЗАВЕРШИЛОСЬ в этом кадре - метрика темпа для CLI и диагностики.</returns>
	/// <param name="stopOnConverged">Обрывать ли цикл по сходимости. Вьюпортам - да. CLI-гарнесс
	/// передаёт false СОЗНАТЕЛЬНО: он меряет худший случай, когда освещение всё время
	/// «меняется» и раунды идут без остановки, - на сошедшейся сессии проверка обнулила бы
	/// весь замер темпа.</param>
	public static int DriveChunks(ProbeRoundGpu gpu, ProbeGiBakeSession session,
		ProbeGiBaker baker, int chunkBudget, bool stopOnConverged = true)
	{
		int rounds = 0;
		for (int i = 0; i < chunkBudget; i++)
		{
			// Сходимость проверяется на КАЖДОМ раунде, а не только на входе: в кадр их теперь
			// помещается несколько, и без проверки бюджет дожигался бы по сошедшемуся полю.
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

			// Глубина очереди набрана - остаток бюджета уедет в следующий кадр. В пределах ОДНОГО
			// кадра забор не двигается (команды ещё не отправлены), поэтому эта проверка и есть
			// потолок «сколько раундов за кадр»: ровно MaxRoundsInFlight.
			if (!gpu.IsReady)
			{
				break;
			}
		}

		return rounds;
	}

	/// <summary>Сетка базового объёма - в кбуфер материала. Origin.w = 1 - тумблер шейдера.
	/// Каскадные поля (ProbeGridOrigin1/2) остаются нулями: каскады сняты вместе с прокруткой,
	/// шейдерная ветка выборки по ним мертва при нулевом Origin.w.</summary>
	/// <param name="viewBias">Доля ВЗГЛЯДА в направлении сдвига точки сэмпла, 0..1 (уходит в
	/// свободную .w счётчиков сетки - см. ProbeGiSampleBody). 1 - сдвиг строго к камере, 0 - строго
	/// по нормали.</param>
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

	/// <summary>Ведёт набор дебаг-оверлеев (шарики проб) за галочкой и жизнью атласов - оверлей
	/// единственного объёма. Любое расхождение набора с текущими атласами - пересборка целиком
	/// (оверлеи дёшевы, частичное обновление при замороженных командах графа не стоит сложности).
	/// failed взводится при ошибке компиляции и глушит попытки до конца сессии.</summary>
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
			// Оверлей сверяет номер раскладки сам и в неподвижном кадре не делает ничего.
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

	/// <summary>Снимает оверлеи с конвейера и освобождает: отцепить от графа, пересобрать команды,
	/// дождаться GPU - замороженный командный буфер прошлого кадра ещё держит материалы.</summary>
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

	/// <summary>Настройки запечки из окна Graphics - единственное место сборки опций сессии для
	/// обоих вьюпортов: новая ручка добавляется сюда один раз.</summary>
	internal static ProbeGiBakeOptions BuildOptions(EditorSettings settings)
	{
		// Сторона окто-карты видимости - глобальная раскладка атласов, а не поле опций: её читают
		// и упаковка на CPU, и оба шейдера. Ставится ЗДЕСЬ, то есть при заведении сессии, - менять
		// её на живой сессии нельзя, запись разошлась бы с чтением (ручка помечена как ребейк).
		ProbeGiBakeResult.VisRes = Math.Clamp(settings.ProbeGiVisRes,
			ProbeGiBakeResult.MinVisRes, ProbeGiBakeResult.MaxVisRes);

		var options = BuildOptionsCore(settings);

		// Каскады вместе со всей прокруткой СНЯТЫ (артефакты въезжающих плоскостей возвращались
		// четырежды - см. историю dense-probe-layout), и бюджет, который раньше делился на три
		// объёма, отдаётся единственной статической сетке целиком: та же конфигурация, что у
		// эталона RTXGI на Sponza (22x22x22, infiniteScrolling.enabled=0).
		//
		// Замерено на Sponza: 8192 проб дают сетку 17x20x17 с ячейкой 2.23, 24576 - 25x28x25 с
		// ячейкой 1.49.
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
