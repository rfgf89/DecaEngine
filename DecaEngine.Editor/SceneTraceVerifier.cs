using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Diligent;

namespace DecaEngine.Editor;

/// <summary>
/// Сверка GPU-трассировки (см. SceneTrace.hlsl) с CPU-эталоном из <see cref="ProbeGiBaker"/>. Один
/// и тот же набор лучей прогоняется обоими путями, дистанции попадания сравниваются.
///
/// Зачем: CPU-трассировщик уже рабочий и на нём испечены все нынешние пробы, так что он - готовый
/// эталон. Без такой сверки ошибка в обходе BVH на GPU (перепутанный ребёнок узла, знак в
/// пересечении с коробкой, раскладка структуры) вылезла бы гораздо позже и выглядела бы как
/// «необъяснимо кривой GI», а не как явный баг трассировки.
/// </summary>
public static class SceneTraceVerifier
{
	/// <summary>Допуск на расхождение дистанции. Нулевого совпадения ждать нельзя: CPU считает в
	/// double-расширенных регистрах, GPU - строго в float, и порядок операций у них разный.
	/// Относительный, потому что абсолютная ошибка растёт с дальностью луча.</summary>
	private const float RelativeTolerance = 1e-3f;

	public readonly record struct Report(int RayCount, int Mismatches, float WorstRelativeError,
		int CpuHits, int GpuHits, int ShaderNodeCount, int UploadedNodeCount);

	/// <summary>Зеркало cbuffer TraceTestParams в SceneTraceTestCS.hlsl - именно uint, а не float.</summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct TraceTestParams
	{
		public uint Count;
		public uint Pad0, Pad1, Pad2;
	}

	public static unsafe Report Run(DiligentGraphicsApi api, ProbeGiBaker baker,
		Vector3 boundsMin, Vector3 boundsMax, int rayCount = 4096)
	{
		var (nodes, order, triangles) = baker.ExportBvh();

		// Лучи из детерминированного низкодискрепансного набора: тест обязан быть воспроизводимым,
		// иначе расхождение не переловишь. Начала разбросаны по баундам сцены, направления - по
		// сфере, так что выборка бьёт и в геометрию, и мимо.
		var origins = new Vector4[rayCount];
		var directions = new Vector4[rayCount];
		var size = boundsMax - boundsMin;
		float tMax = baker.RayTMax;
		for (int i = 0; i < rayCount; i++)
		{
			float u1 = Frac(i * 0.7548776662f);
			float u2 = Frac(i * 0.5698402909f);
			float u3 = Frac(i * 0.6180339887f);
			float u4 = Frac(i * 0.8191725134f);
			float u5 = Frac(i * 0.3819660112f);

			origins[i] = new Vector4(boundsMin + new Vector3(size.X * u1, size.Y * u2, size.Z * u3), tMax);

			float z = u4 * 2f - 1f;
			float r = MathF.Sqrt(MathF.Max(1f - z * z, 0f));
			float phi = u5 * 2f * MathF.PI;
			directions[i] = new Vector4(
				Vector3.Normalize(new Vector3(MathF.Cos(phi) * r, z, MathF.Sin(phi) * r)), 0f);
		}

		var device = api.Device;
		var context = api.ImmediateContext;

		using var nodeBuffer = CreateStructured(device, "SceneBvhNodes", nodes, sizeof(BvhNodeGpu));
		using var orderBuffer = CreateStructured(device, "SceneBvhOrder", order, sizeof(uint));
		using var triBuffer = CreateStructured(device, "SceneBvhTriangles", triangles, sizeof(BvhTriangleGpu));
		using var originBuffer = CreateStructured(device, "TestRayOrigin", origins, sizeof(Vector4));
		using var dirBuffer = CreateStructured(device, "TestRayDirection", directions, sizeof(Vector4));

		using var resultBuffer = device.CreateBuffer(new BufferDesc
		{
			Name = "TestResult",
			Usage = Usage.Default,
			BindFlags = BindFlags.UnorderedAccess,
			Mode = BufferMode.Structured,
			ElementByteStride = (uint)sizeof(Vector4),
			Size = (ulong)(rayCount * sizeof(Vector4)),
		});

		using var paramsBuffer = device.CreateBuffer(new BufferDesc
		{
			Name = "TraceTestParams",
			Usage = Usage.Dynamic,
			BindFlags = BindFlags.UniformBuffer,
			CPUAccessFlags = CpuAccessFlags.Write,
			Size = 16,
		});
		// Discard, а не DoNotWait: с DoNotWait маппинг динамического буфера имеет право не удаться,
		// и помощник ТИХО пропускает запись - кбуфер остаётся нулевым, шейдер выходит по guard-у, и
		// выглядит это как «GPU не совпал с CPU по всем лучам». Тип тоже обязан совпадать с
		// объявленным uint4: float 4096 в uint-поле - это не 4096.
		context.UploadBufferExt(paramsBuffer, new TraceTestParams { Count = (uint)rayCount },
			MapFlags.Discard);

		using var factory = api.EngineFactory.CreateDefaultShaderSourceStreamFactory("EditorAssets/shader");
		using var shader = device.CreateShader(new ShaderCreateInfo
		{
			SourceLanguage = ShaderSourceLanguage.Hlsl,
			Desc = new ShaderDesc
			{
				Name = "SceneTraceTestCS",
				ShaderType = ShaderType.Compute,
				UseCombinedTextureSamplers = false,
			},
			EntryPoint = "main",
			FilePath = "SceneTraceTestCS.hlsl",
			ShaderSourceStreamFactory = factory,
		}, out _);

		using var pso = device.CreateComputePipelineState(new ComputePipelineStateCreateInfo
		{
			PSODesc = new PipelineStateDesc
			{
				Name = "SceneTraceTest",
				PipelineType = PipelineType.Compute,
				ResourceLayout = new PipelineResourceLayoutDesc
				{
					DefaultVariableType = ShaderResourceVariableType.Mutable,
				},
			},
			Cs = shader,
		});

		using var srb = pso.CreateShaderResourceBinding(true);
		Bind(srb, "_SceneBvhNodes", nodeBuffer);
		Bind(srb, "_SceneBvhOrder", orderBuffer);
		Bind(srb, "_SceneBvhTriangles", triBuffer);
		Bind(srb, "_TestRayOrigin", originBuffer);
		Bind(srb, "_TestRayDirection", dirBuffer);
		Require(srb, "_TestResult").Set(
			resultBuffer.GetDefaultView(BufferViewType.UnorderedAccess), SetShaderResourceFlags.AllowOverwrite);
		Require(srb, "TraceTestParams").Set(paramsBuffer, SetShaderResourceFlags.AllowOverwrite);

		context.SetPipelineState(pso);
		context.CommitShaderResources(srb, ResourceStateTransitionMode.Transition);
		context.DispatchCompute(new DispatchComputeAttribs
		{
			ThreadGroupCountX = (uint)((rayCount + 63) / 64),
			ThreadGroupCountY = 1,
			ThreadGroupCountZ = 1,
		});

		var gpu = new Vector4[rayCount];
		fixed (Vector4* dst = gpu)
		{
			context.ReadBufferExt<Vector4>(device, resultBuffer, dst, (uint)(rayCount * sizeof(Vector4)));
		}

		// Маркер из шейдера (см. SceneTraceTestCS): без него «расхождение во всех лучах» неотличимо
		// от непривязанного/незаписанного буфера, а это совсем разные баги.
		if (gpu[0].Z != 777f)
		{
			throw new InvalidOperationException(
				$"Compute shader did not write results (marker={gpu[0].Z}, expected 777) - " +
				"dispatch or UAV binding is broken, not the traversal");
		}

		// Число узлов, как его видит шейдер, - только СПРАВКА, не приговор: сам GetDimensions мог
		// оказаться ненадёжным на конкретном бэкенде. Судим по фактическим результатам трассировки.
		int shaderNodeCount = (int)gpu[0].W;

		int mismatches = 0, cpuHits = 0, gpuHits = 0;
		float worst = 0f;
		for (int i = 0; i < rayCount; i++)
		{
			bool cpuHit = baker.TraceRay(
				new Vector3(origins[i].X, origins[i].Y, origins[i].Z),
				new Vector3(directions[i].X, directions[i].Y, directions[i].Z),
				tMax, out float cpuT, out _, out _);

			bool gpuHit = gpu[i].X >= 0f;
			if (cpuHit) cpuHits++;
			if (gpuHit) gpuHits++;

			if (cpuHit != gpuHit)
			{
				mismatches++;
				continue;
			}

			if (!cpuHit)
			{
				continue;
			}

			float error = MathF.Abs(gpu[i].X - cpuT) / MathF.Max(cpuT, 1e-4f);
			worst = MathF.Max(worst, error);
			if (error > RelativeTolerance)
			{
				mismatches++;
			}
		}

		return new Report(rayCount, mismatches, worst, cpuHits, gpuHits, shaderNodeCount, nodes.Length);
	}

	/// <summary>Сверяет GPU-раунд обновления проб (см. <see cref="ProbeRoundGpu"/>) с CPU-эталоном.
	/// Освещение нарочно упрощено - без неба и без переотскока: небо на CPU считается функцией, а
	/// на GPU было бы текстурой, и их расхождение замусорило бы результат, вместо того чтобы
	/// проверить то, что проверяется - генерацию лучей, трассировку, теневые лучи, сборку SH и
	/// смешивание раундов.</summary>
	public readonly record struct RoundReport(int Probes, int Rounds, float WorstRelativeError,
		int Mismatches, float CpuMeanLuminance, float GpuMeanLuminance,
		double CpuMsPerRound, double GpuMsPerRound,
		int SignificantProbes, float WorstAbsoluteError, float MeanMagnitude);

	public static RoundReport VerifyRound(DiligentGraphicsApi api, ProbeGiBaker baker,
		Vector3 boundsMin, Vector3 boundsMax, Vector3 sunDirection, Vector3 sunColor, int rounds = 4,
		IGpuTexture? environmentMap = null, Func<Vector3, Vector3>? skyRadiance = null,
		float envYaw = 0f)
	{
		var (nodes, order, triangles) = baker.ExportBvh();

		// Небо включается, только если вызывающий дал и карту, и её CPU-выборку: сравнивать
		// CPU-функцию с GPU-сэмплером точно нельзя (разная фильтрация и мип-логика), поэтому со
		// светом неба сверка становится проверкой ПОРЯДКА ВЕЛИЧИНЫ, а не побитовой.
		bool withSky = environmentMap != null && skyRadiance != null;

		var options = new ProbeGiBakeOptions
		{
			SkyIntensity = withSky ? 1f : 0f,
			Bounces = 2,         // переотскок включён: сбор поля - самая замысловатая часть порта
			SurfaceCache = true, // и кэш поверхностей, ради которого весь порт и затевался
		};

		var session = baker.BeginBake(boundsMin, boundsMax, sunDirection, sunColor,
			withSky ? envYaw : 0f, skyRadiance ?? (_ => Vector3.Zero), options);

		// Кэш строится лениво первым раундом (см. WantsSurfaceCache), а GPU-стороне он нужен уже
		// при создании буферов. Прогонять ради этого раунд нельзя - CPU ушёл бы на раунд вперёд и
		// сверка потеряла бы смысл; строим захват отдельно.
		baker.EnsureSurfaceCache(session);

		// Атласы обязаны быть привязаны, даже если сверка сравнивает буфер поля: шейдер пишет в них
		// безусловно, а непривязанный UAV - это ошибка валидации (VUID-vkCmdDispatch-None-08114) и
		// поведение «работает, пока не перестанет». Заодно прогоняется путь записи в атласы.
		var atlases = new ProbeGiTextures(api, session.Result, "_probeGiVerify", gpuWritable: true);
		// НЕ using: освобождать надо строго раньше атласов - GPU-объект держит на них
		// представления, и обратный порядок роняет драйвер в Dispose.
		using var pipelines = new ProbeRoundPipelines(api, hardware: false);
		var gpu = new ProbeRoundGpu(api, pipelines, session, baker, atlases, environmentMap,
			withSky ? envYaw : 0f);

		// Раунды гоняются попарно: CPU двигает сессию, GPU получает ТЕ ЖЕ направления лучей и тот
		// же вес раунда. Сравнивать поля после разного числа раундов бессмысленно - бегущее
		// среднее зависит от истории.
		for (int i = 0; i < rounds; i++)
		{
			var directions = ProbeGiBaker.RoundRayDirections(session);
			float alpha = ProbeGiBaker.RoundBlendWeight(session);
			// Раунд идёт порциями - здесь докручиваем его целиком, сверка сравнивает поля после
			// ОДИНАКОВОГО числа раундов.
			while (!gpu.RunRound(session, baker, directions, alpha))
			{
			}

			baker.RunRound(session);
		}

		var gpuField = gpu.ReadField();

		int mismatches = 0, significant = 0;
		float worst = 0f, worstAbs = 0f;
		double cpuSum = 0, gpuSum = 0, magnitudeSum = 0;
		for (int p = 0; p < session.ProbeCount; p++)
		{
			var cpu = session.IrradianceRead[p];
			var got = gpuField[p * 4];
			var gotRgb = new Vector3(got.X, got.Y, got.Z);

			cpuSum += Luminance(cpu);
			gpuSum += Luminance(gotRgb);

			float absError = (gotRgb - cpu).Length();
			worstAbs = MathF.Max(worstAbs, absError);
			magnitudeSum += cpu.Length();

			// Относительная ошибка считается только для проб с ОСМЫСЛЕННОЙ яркостью. На тёмных
			// пробах (внутри геометрии, в глубокой тени) L0 около нуля, и любая разница в последнем
			// бите даёт относительную ошибку в разы - это шум метрики, а не расхождение путей.
			float scale = cpu.Length();
			if (scale < 1e-3f)
			{
				continue;
			}

			significant++;
			float error = absError / scale;
			worst = MathF.Max(worst, error);
			if (error > 2e-2f)
			{
				mismatches++;
			}
		}

		// Замер стоимости раунда - ради него всё и затевалось. GPU меряется с Flush+WaitForIdle:
		// сам по себе диспатч только пишет команды, и «мгновенный» раунд без синка ничего не
		// значил бы. Синк добавляет накладных, так что цифра GPU скорее пессимистична.
		const int timedRounds = 4;
		var context = api.ImmediateContext;

		var swGpu = System.Diagnostics.Stopwatch.StartNew();
		for (int i = 0; i < timedRounds; i++)
		{
			while (!gpu.RunRound(session, baker,
				ProbeGiBaker.RoundRayDirections(session.RaysPerRound, session.Sequence + i, session.FixedRays),
				ProbeGiBaker.RoundBlendWeight(session)))
			{
			}
		}

		context.Flush();
		context.WaitForIdle();
		swGpu.Stop();

		var swCpu = System.Diagnostics.Stopwatch.StartNew();
		for (int i = 0; i < timedRounds; i++)
		{
			baker.RunRound(session);
		}

		swCpu.Stop();
		gpu.Dispose();
		atlases.Release();

		return new RoundReport(session.ProbeCount, rounds, worst, mismatches,
			(float)(cpuSum / Math.Max(session.ProbeCount, 1)),
			(float)(gpuSum / Math.Max(session.ProbeCount, 1)),
			swCpu.Elapsed.TotalMilliseconds / timedRounds,
			swGpu.Elapsed.TotalMilliseconds / timedRounds,
			significant, worstAbs, (float)(magnitudeSum / Math.Max(session.ProbeCount, 1)));
	}

	/// <summary>Замер МЕРЦАНИЯ поля в режиме реального времени. Отвечает на вопрос, который на глаз
	/// не различить: поле «кипит» от дисперсии оценки (каждая проба скачет сама по себе, средняя
	/// яркость стоит) или РАСКАЧИВАЕТСЯ петлёй мультибаунса (вся сцена дышит целиком)? Лечится это
	/// противоположным - в первом случае лучами и весом раунда, во втором обратной связью, - поэтому
	/// разделить причины важнее, чем померить амплитуду.
	///
	/// MeanRelativeDelta - средняя по пробам относительная смена L0 за раунд (кипение).
	/// MeanLuminance* - разброс СРЕДНЕЙ по всей сетке яркости по раундам (дыхание).</summary>
	public readonly record struct FlickerReport(int Probes, int Rays, float Alpha, int Rounds,
		float MeanRelativeDelta, float MaxRelativeDelta,
		float MeanLuminanceMin, float MeanLuminanceMax, float MeanLuminanceAvg,
		float P50, float P90, float P99, float ShareAbove10,
		float Variability, float SkippedRoundShare)
	{
		/// <summary>Размах средней яркости в долях от неё самой: дыхание всей сцены.</summary>
		public float GlobalSwing => MeanLuminanceAvg > 1e-6f
			? (MeanLuminanceMax - MeanLuminanceMin) / MeanLuminanceAvg
			: 0f;
	}

	public static FlickerReport MeasureFlicker(DiligentGraphicsApi api, ProbeGiBaker baker,
		Vector3 boundsMin, Vector3 boundsMax, Vector3 sunDirection, Vector3 sunColor,
		int raysPerRound, int settleRounds, int measureRounds, float maxRayLuminance = 0f,
		float blend = 0f, float skyIntensity = 1f, float gridDensity = 0f, float maxStep = -1f,
		float relocation = -1f, float gamma = -1f, float variabilityThreshold = 0f,
		bool hardware = false,
		IGpuTexture? environmentMap = null, Func<Vector3, Vector3>? skyRadiance = null,
		float envYaw = 0f)
	{
		bool withSky = environmentMap != null && skyRadiance != null;
		var options = new ProbeGiBakeOptions
		{
			// Яркость неба сюда вынесена не для полноты: она задаёт КОНТРАСТ между небом и
			// поверхностями, а он и есть источник разброса пробы. Луч в небо приносит радианс в
			// skyIntensity раз больший, чем луч в стену, поэтому поворот веера, перекинувший пару
			// лучей через край арки, двигает оценку тем сильнее, чем выше эта ручка.
			SkyIntensity = withSky ? skyIntensity : 0f,
			Bounces = 2,
			SurfaceCache = true,
			// Именно РЕАЛТАЙМОВЫЙ бюджет лучей: сессия в этом режиме берёт его, а не RaysPerRound
			// (см. ProbeGiBakeSession.RaysPerRound) - иначе замер молча мерил бы дефолт.
			RealtimeRaysPerRound = raysPerRound,
			RealtimeMaxRayLuminance = maxRayLuminance,
			// -1 = «оставить дефолт», 0 = явно выключить: ноль тут осмысленное значение, поэтому
			// признаком «не задано» служит отрицательное.
			RealtimeMaxStep = maxStep < 0f ? new ProbeGiBakeOptions().RealtimeMaxStep : maxStep,
			RealtimeRelocation = relocation < 0f
				? new ProbeGiBakeOptions().RealtimeRelocation
				: relocation,
			RealtimeGamma = gamma < 0f ? new ProbeGiBakeOptions().RealtimeGamma : gamma,
			// По умолчанию остановка сошедшегося объёма ВЫКЛЮЧЕНА: замер меряет мерцание, а
			// пропущенный раунд даёт нулевую разницу и подменил бы метрику нулями. Включается
			// отдельно (DECA_PROBE_FLICKERVAR), чтобы померить долю пропусков.
			RealtimeVariabilityThreshold = variabilityThreshold,
			RealtimeBlend = blend > 0f ? blend : ProbeGiBaker.RealtimeBlend,
			// Ради чего всё и меряется: в запечке вес раунда падает к нулю, и любое мерцание
			// затухает само - вопрос стоит только для постоянного веса.
			Realtime = true,
		};

		if (gridDensity > 0f)
		{
			options.GridDensity = gridDensity;
			options.MaxProbes = ProbeGiBaker.MaxProbeBudget;
		}

		var session = baker.BeginBake(boundsMin, boundsMax, sunDirection, sunColor,
			withSky ? envYaw : 0f, skyRadiance ?? (_ => Vector3.Zero), options);
		baker.EnsureSurfaceCache(session);

		var atlases = new ProbeGiTextures(api, session.Result, "_probeGiFlicker", gpuWritable: true);

		// Аппаратная трассировка здесь не роскошь: на плотной сетке (density 64 - это сотни тысяч
		// проб) программный обход считает раунд секундами, и замер из инструмента превращается в
		// получасовое ожидание.
		using var pipelines = new ProbeRoundPipelines(api, hardware);
		using var accel = hardware ? new ProbeSceneAccel(api, baker.InstancedGeometry) : null;
		var gpu = new ProbeRoundGpu(api, pipelines, session, baker, atlases, environmentMap,
			withSky ? envYaw : 0f, accel);

		void RunOne()
		{
			while (!gpu.RunRound(session, baker,
				ProbeGiBaker.RoundRayDirections(session),
				ProbeGiBaker.RoundBlendWeight(session)))
			{
			}

			session.AdvanceRound();
		}

		// Разгон: первые раунды идут с полным весом и мерцания не показывают - поле в них ещё
		// строится, а не колеблется вокруг решения.
		for (int i = 0; i < settleRounds; i++)
		{
			RunOne();
		}

		int skippedBefore = gpu.SkippedRounds;
		var previous = gpu.ReadField();
		double deltaSum = 0;
		float lumMin = float.MaxValue, lumMax = 0f;
		double lumSum = 0;
		int probes = session.ProbeCount;

		// Относительные смены ПО ВСЕМ пробам и раундам: максимум сам по себе врёт (одна выродившаяся
		// проба из тысяч выглядит как катастрофа), а видно глазу распределение - какая ДОЛЯ сетки
		// заметно дёргается.
		var deltas = new List<float>(probes * measureRounds);

		for (int round = 0; round < measureRounds; round++)
		{
			RunOne();
			var current = gpu.ReadField();

			double roundDelta = 0, roundMagnitude = 0, roundLum = 0;
			for (int p = 0; p < probes; p++)
			{
				var before = previous[p * 4];
				var after = current[p * 4];
				var d = new Vector3(after.X - before.X, after.Y - before.Y, after.Z - before.Z);
				var magnitude = new Vector3(after.X, after.Y, after.Z);

				roundDelta += d.Length();
				roundMagnitude += magnitude.Length();
				roundLum += Luminance(magnitude);

				// Пробы у нуля из относительной метрики выбрасываются: там любое дрожание в
				// последнем бите даёт разы, и распределение перестало бы что-либо значить.
				if (magnitude.Length() > 1e-2f)
				{
					deltas.Add(d.Length() / magnitude.Length());
				}
			}

			deltaSum += roundMagnitude > 1e-9 ? roundDelta / roundMagnitude : 0.0;
			float meanLum = (float)(roundLum / Math.Max(probes, 1));
			lumMin = MathF.Min(lumMin, meanLum);
			lumMax = MathF.Max(lumMax, meanLum);
			lumSum += meanLum;
			previous = current;
		}

		float variability = gpu.AverageVariability;
		int skippedRounds = gpu.SkippedRounds - skippedBefore;

		deltas.Sort();
		float Percentile(double q) => deltas.Count == 0
			? 0f
			: deltas[Math.Clamp((int)(q * (deltas.Count - 1)), 0, deltas.Count - 1)];

		float worst = deltas.Count > 0 ? deltas[^1] : 0f;
		float shareAbove10 = deltas.Count > 0
			? deltas.Count(d => d > 0.1f) / (float)deltas.Count
			: 0f;

		gpu.Dispose();
		atlases.Release();

		return new FlickerReport(probes, session.RaysPerRound,
			ProbeGiBaker.RoundBlendWeight(session), measureRounds,
			(float)(deltaSum / Math.Max(measureRounds, 1)), worst,
			lumMin, lumMax, (float)(lumSum / Math.Max(measureRounds, 1)),
			Percentile(0.50), Percentile(0.90), Percentile(0.99), shareAbove10,
			variability, measureRounds > 0 ? skippedRounds / (float)measureRounds : 0f);
	}

	private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

	private static void Bind(IShaderResourceBinding srb, string name, IBuffer buffer)
	{
		// Явное описание представления, а не GetDefaultView. Строго говоря, работает и дефолтное:
		// подозрение на пустое SRV у D3D12 не подтвердилось - врал сам GetDimensions, которым это
		// проверялось (на D3D12 он даёт ноль при полностью исправной привязке, см. отчёт
		// ShaderNodeCount). Явный вид оставлен как менее двусмысленный.
		var desc = buffer.GetDesc();
		using var view = buffer.CreateView(new BufferViewDesc
		{
			Name = $"{desc.Name} SRV",
			ViewType = BufferViewType.ShaderResource,
			ByteOffset = 0,
			ByteWidth = desc.Size,
		});

		Require(srb, name).Set(view, SetShaderResourceFlags.AllowOverwrite);
	}

	/// <summary>Привязка ОБЯЗАНА найтись: молча проглоченный промах имени даёт нулевой буфер и
	/// «расхождение» во всех лучах - самая дорогая для отладки форма отказа.</summary>
	private static IShaderResourceVariable Require(IShaderResourceBinding srb, string name) =>
		srb.GetVariableByName(ShaderType.Compute, name)
		?? throw new InvalidOperationException(
			$"Compute shader has no resource variable '{name}' - it was likely optimised out or renamed");

	private static unsafe IBuffer CreateStructured<T>(IRenderDevice device, string name, T[] data, int stride)
		where T : unmanaged
	{
		var desc = new BufferDesc
		{
			Name = name,
			// Default, а не Immutable: у D3D12-бэкенда immutable structured-буфер с начальными
			// данными не доезжал до шейдера - тот видел ноль элементов (поймано сверкой, см. Run).
			Usage = Usage.Default,
			BindFlags = BindFlags.ShaderResource,
			Mode = BufferMode.Structured,
			ElementByteStride = (uint)stride,
			Size = (ulong)(data.Length * stride),
		};

		fixed (T* ptr = data)
		{
			return device.CreateBuffer(desc, new BufferData
			{
				Data = new IntPtr(ptr),
				DataSize = (ulong)(data.Length * stride),
			});
		}
	}

	private static float Frac(float v) => v - MathF.Floor(v);
}
