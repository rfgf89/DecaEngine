using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Core;

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
internal static class ProbeGiViewportShared
{

	/// <summary>Живые ручки реального времени из настроек - в сессию, перед раундом.</summary>
	internal static void ApplyRealtimeKnobs(ProbeGiBakeSession session, EditorSettings settings,
		bool gpuAlive)
	{
		session.Realtime = settings.ProbeGiRealtime && gpuAlive;
		session.RealtimeRaysPerRound = Math.Clamp(settings.ProbeGiRealtimeRays, 8, 1024);
		session.RealtimeBlend = Math.Clamp(settings.ProbeGiRealtimeBlend, 0.01f, 0.5f);
		session.RealtimeMaxStep = Math.Clamp(settings.ProbeGiRealtimeMaxStep, 0f, 0.2f);
		session.RealtimeGamma = Math.Clamp(settings.ProbeGiRealtimeGamma, 1f, 8f);
		session.RealtimeRelocation = Math.Clamp(settings.ProbeGiRealtimeRelocation, 0f, 0.45f);
	}

	/// <summary>Один объём на один кадр: свет, ручки, бюджет порций, продвижение счётчиков.
	/// Возвращает false, если GPU-раунд бросил исключение - вызывающий гасит путь.</summary>
	internal static bool DriveVolume(ProbeRoundGpu gpu, ProbeGiBakeSession session,
		ProbeGiBaker baker, EditorSettings settings, Vector3 sunDirection, Vector3 sunColor,
		float envYaw, Func<Vector3, Vector3> skyRadiance, int volumeCount)
	{
		ApplyRealtimeKnobs(session, settings, gpuAlive: true);
		session.SetLighting(sunDirection, sunColor, envYaw, skyRadiance);

		if (session.Converged || !gpu.IsReady)
		{
			return true;
		}

		// Бюджет порций - от пути и веера (см. ProbeRoundGpu.ChunksPerFrame): аппаратная порция на
		// два порядка дешевле программной, и без этой поправки темп раундов упирался в число кадров,
		// а не в скорость лучей - ускорение трассировки было невидимым.
		DriveChunks(gpu, session, baker, gpu.ChunksPerFrame(session.RaysPerRound, volumeCount));
		return true;
	}

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
	internal static int DriveChunks(ProbeRoundGpu gpu, ProbeGiBakeSession session,
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
					ProbeGiBaker.RoundRayDirections(session.RaysPerRound, session.Sequence),
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

	/// <summary>Половина бокса каскада i от баундов сцены: тот же бюджет проб на бокс в 2^i раз
	/// меньше = ячейка в 2^i раз мельче.</summary>
	internal static Vector3 CascadeHalfExtent(Vector3 boundsMin, Vector3 boundsMax, int index) =>
		(boundsMax - boundsMin) * (0.5f / (1 << index));

	/// <summary>Центр бокса каскада под точку интереса, с клампом к баундам - объём не должен
	/// уезжать в пустоту за геометрией.</summary>
	internal static Vector3 ClampCascadeCenter(Vector3 boundsMin, Vector3 boundsMax, Vector3 target,
		Vector3 half)
	{
		var lo = boundsMin + half;
		var hi = boundsMax - half;
		var mid = (boundsMin + boundsMax) * 0.5f;
		return new Vector3(
			lo.X <= hi.X ? Math.Clamp(target.X, lo.X, hi.X) : mid.X,
			lo.Y <= hi.Y ? Math.Clamp(target.Y, lo.Y, hi.Y) : mid.Y,
			lo.Z <= hi.Z ? Math.Clamp(target.Z, lo.Z, hi.Z) : mid.Z);
	}

	/// <summary>Гистерезис перецентровки: пересоздавать каскад, только если желаемый центр ушёл
	/// дальше четверти бокса - мелкие движения камеры не трогают ничего.</summary>
	internal static bool NeedsRecenter(Vector3 center, Vector3 desired, Vector3 half)
	{
		var delta = Vector3.Abs(desired - center);
		return delta.X > half.X * 0.5f || delta.Y > half.Y * 0.5f || delta.Z > half.Z * 0.5f;
	}

	/// <summary>Сетки базового объёма и каскадов - в кбуфер материала. Origin.w = 1 - тумблер
	/// шейдера; незаполненные каскады остаются нулями, и шейдер их не трогает.</summary>
	internal static void PushGrid(ref PreviewSettingsData data, ProbeGiTextures textures,
		IReadOnlyList<(ProbeGiBakeSession Session, ProbeGiTextures Textures, ProbeRoundGpu Gpu,
			Vector3 Center)> cascades,
		float normalBias)
	{
		float bias = 0.75f * Math.Clamp(normalBias, 0f, 2f);
		data.ProbeGridOrigin = textures.GridOrigin;
		data.ProbeGridCell = new Vector4(
			textures.GridCell.X, textures.GridCell.Y, textures.GridCell.Z,
			textures.MinCellSize * bias);
		data.ProbeGridCounts = textures.GridCounts;
		data.ProbeGridBricks = textures.GridBricks;

		if (cascades.Count > 0)
		{
			var t = cascades[0].Textures;
			data.ProbeGridOrigin1 = t.GridOrigin;
			data.ProbeGridCell1 = new Vector4(
				t.GridCell.X, t.GridCell.Y, t.GridCell.Z, t.MinCellSize * bias);
			data.ProbeGridCounts1 = t.GridCounts;
			data.ProbeGridBricks1 = t.GridBricks;
		}

		if (cascades.Count > 1)
		{
			var t = cascades[1].Textures;
			data.ProbeGridOrigin2 = t.GridOrigin;
			data.ProbeGridCell2 = new Vector4(
				t.GridCell.X, t.GridCell.Y, t.GridCell.Z, t.MinCellSize * bias);
			data.ProbeGridCounts2 = t.GridCounts;
			data.ProbeGridBricks2 = t.GridBricks;
		}
	}

	/// <summary>Возвращает плейсхолдер в каскадные слоты материалов модели - слоты объявлены в
	/// шейдере безусловно, пустой дескриптор роняет валидацию Vulkan (VUID-08114).</summary>
	internal static void RebindCascadePlaceholders(ModelLoader model, IGpuTexture placeholder,
		string suffix)
	{
		for (int i = 0; i < model.materialObjects.Count; i++)
		{
			var material = model.materialObjects.GetAt(i).Value;
			material.SetTexture($"_ProbeSh0{suffix}", placeholder);
			material.SetTexture($"_ProbeSh1{suffix}", placeholder);
			material.SetTexture($"_ProbeSh2{suffix}", placeholder);
			material.SetTexture($"_ProbeSh3{suffix}", placeholder);
			material.SetTexture($"_ProbeVis{suffix}", placeholder);
			material.SetTexture($"_ProbeOffset{suffix}", placeholder);
			material.SetTexture($"_ProbeIndirection{suffix}", placeholder);
		}
	}

	/// <summary>Создание каскада - общее для обоих вьюпортов: сессия по боксу вокруг точки
	/// интереса + атласы (слоты _Ci у всех переданных моделей) + GPU-раунд.
	///
	/// Кэш поверхностей здесь НЕ захватывается сознательно: каскады существуют только в реальном
	/// времени, где кэш не читается (см. этап 3), а его захват стоил сотен миллисекунд - превью
	/// платило их за каждый каскад впустую, пока создание жило в двух копиях.</summary>
	internal static (ProbeGiBakeSession Session, ProbeGiTextures Textures, ProbeRoundGpu Gpu,
		Vector3 Center) CreateCascade(
		ProbeGiBaker baker, ProbeRoundPipelines pipelines, ProbeSceneAccel? accel,
		ModelViewportEnvironment env, IGraphicsApi graphicsApi, EditorSettings settings,
		IEnumerable<ModelLoader> models, string atlasName, int index, Vector3 target,
		Vector3 boundsMin, Vector3 boundsMax, Vector3 sunColor)
	{
		var half = CascadeHalfExtent(boundsMin, boundsMax, index);
		var center = ClampCascadeCenter(boundsMin, boundsMax, target, half);
		var session = baker.BeginBake(center - half, center + half,
			Vector3.Normalize(-env.ShadowSettings!.LightDirection), sunColor,
			env.ShadowSettings.EnvYawRadians, env.EnvironmentRadiance, BuildOptions(settings));

		var textures = new ProbeGiTextures(graphicsApi, session.Result, atlasName, gpuWritable: true);
		foreach (var model in models)
		{
			textures.Bind(model, $"_C{index}");
		}

		return (session, textures,
			new ProbeRoundGpu(env.DilApi, pipelines, session, baker, textures,
				env.EnvironmentMap, env.ShadowSettings.EnvYawRadians, accel),
			center);
	}

	/// <summary>Ведёт набор дебаг-оверлеев (шарики проб) за галочкой и жизнью атласов: base +
	/// каскады, по оверлею на объём. Любое расхождение набора с текущими атласами - пересборка
	/// целиком (оверлеи дёшевы, частичное обновление при замороженных командах графа не стоит
	/// сложности). failed взводится при ошибке компиляции и глушит попытки до конца сессии.</summary>
	internal static void PollOverlays(
		List<(ProbeDebugOverlay Overlay, ProbeGiTextures Textures)> overlays,
		bool want, ref bool failed, ModelViewportEnvironment env, IGraphicsApi graphicsApi,
		ProbeGiBakeSession? session, ProbeGiTextures? textures,
		IReadOnlyList<(ProbeGiBakeSession Session, ProbeGiTextures Textures, ProbeRoundGpu Gpu,
			Vector3 Center)> cascades)
	{
		want = want && session != null && textures != null && !failed;

		bool matches = want && overlays.Count == 1 + cascades.Count
			&& ReferenceEquals(overlays[0].Textures, textures);
		for (int i = 0; matches && i < cascades.Count; i++)
		{
			matches = ReferenceEquals(overlays[i + 1].Textures, cascades[i].Textures);
		}

		if (overlays.Count > 0 && !matches)
		{
			ReleaseOverlays(overlays, env);
		}

		if (!want || overlays.Count > 0)
		{
			return;
		}

		try
		{
			var format = env.Pipeline.Targets?.RenderColorFormat ?? TextureObjectFormat.R8G8B8A8UNorm;
			overlays.Add((new ProbeDebugOverlay(env.DilApi, graphicsApi, env.BatchRenderer,
				session!, textures!, env.MsaaSamples, format), textures!));
			for (int i = 0; i < cascades.Count; i++)
			{
				// Метки объёмов: каскад 1 - голубой, каскад 2 - пурпурный. Без подкраски их шарики
				// (мельче базовых, того же цвета поля) в общей массе не различить.
				var tint = i == 0 ? new Vector3(0f, 0.7f, 1f) : new Vector3(1f, 0.2f, 0.9f);
				overlays.Add((new ProbeDebugOverlay(env.DilApi, graphicsApi, env.BatchRenderer,
					cascades[i].Session, cascades[i].Textures, env.MsaaSamples, format, tint),
					cascades[i].Textures));
			}

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
			EditorConsoleLog.Add(LogLevel.Error, $"Probe GI: debug overlay failed: {ex.Message}");
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

		return BuildOptionsCore(settings);
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
	};
}
