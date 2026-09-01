using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using BepuPhysics;
using DecaEngine.Core;
using DecaEngine.Core.Assets;
using DecaEngine.Core.Prefabs;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using Friflo.Engine.ECS;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Scene;
using DecaEngine.Animation;
using DecaEngine.Editor;

namespace DecaEngine.Probes;

/// <summary>
/// Прогон НАСТОЯЩЕЙ сцены с физикой, без окна (DECA_PROBE_SCENE=1).
///
/// Отличие от <see cref="GameplayProbe"/> принципиальное. Тот гоняет драйвер на синтетическом полу -
/// на идеальном квадрате, который сам же и построил. Здесь берётся демо-префаб целиком: та же
/// иерархия, те же компоненты, та же геометрия площадки, прочитанная ТЕМ ЖЕ импортёром и запечённая
/// в статику ТЕМ ЖЕ кодом, что и в редакторе (<see cref="PrefabSceneViewport.AppendModelGeometry"/>).
/// Между «драйвер работает» и «персонаж ходит в сцене» помещается вся эта разница, и в редакторе она
/// уже один раз выстрелила.
///
/// Печатается ТРАЕКТОРИЯ, а не итог. Итог отвечает «дошёл или нет», а вопрос обычно другой: где
/// именно он перестал идти так, как задумано, - и на это отвечает только ряд чисел по секундам.
///
/// Путь префаба можно задать: DECA_PROBE_SCENEPATH=&lt;...&gt;.prefab.json. Без него сцена
/// ГЕНЕРИРУЕТСЯ во временную папку тем же кодом, что и «File → New Project» - проверка не зависит
/// от того, что лежит в чьём-то проекте.
/// </summary>
public static class ScenePhysicsProbe
{
	private const float Step = 1f / 60f;

	/// <summary>Позиция таза каждого рэгдолла в момент «уже улёгся» - точка отсчёта метрики покоя.</summary>
	private static readonly Dictionary<int, Vector3> _hipAtSettle = new();

	/// <summary>Последнее напечатанное состояние ходока - чтобы печатать ПЕРЕХОДЫ, а не состояние
	/// каждого кадра.</summary>
	private static CharacterMotionState? _lastState;

	/// <summary>
	/// Печатает смену состояния цикла «идёт → падает → встаёт».
	///
	/// Именно переходы, а не срез: цикл ломается не «неправильным состоянием», а застреванием в одном
	/// из них - персонаж, который упал и не встал, по любому отдельному кадру выглядит нормально
	/// лежащим. Видно это только по временной шкале переходов.
	/// </summary>
	private static void ReportStateChange(Entity character, float time)
	{
		if (character.IsNull || !character.HasComponent<FallRecoverComponent>())
		{
			return;
		}

		var state = character.GetComponent<FallRecoverComponent>().State;

		if (_lastState == state)
		{
			return;
		}

		_lastState = state;
		Console.WriteLine($"[probe] scene: t={time:0.0} с - персонаж {state}");
	}

	public static void Run(IGraphicsApi api, DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning)
	{
		string prefabPath = Environment.GetEnvironmentVariable("DECA_PROBE_SCENEPATH") ?? string.Empty;

		if (string.IsNullOrEmpty(prefabPath))
		{
			string temp = Path.Combine(Path.GetTempPath(), "DecaSceneProbe", "Assets");
			SamplePrefabBuilder.WriteScene(temp, _ => { });
			prefabPath = Path.Combine(temp, "Animation Sample.prefab.json");
		}

		if (!File.Exists(prefabPath))
		{
			Console.WriteLine($"[probe] scene: префаб не найден: {prefabPath}");
			return;
		}

		Console.WriteLine($"[probe] scene: {prefabPath}");

		string assetsDirectory = Path.GetDirectoryName(Path.GetFullPath(prefabPath)) ?? ".";

		var store = new EntityStore();
		var root = PrefabAsset.Instantiate(store, prefabPath);

		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		// Модели остаются жить до конца процесса намеренно: их CPU-копии вершин уже уехали в BVH
		// статики, а пробник заканчивается сразу за прогоном - выселять их некому и незачем.
		var models = BuildStatics(api, store, root, assetsDirectory, physics, flipWinding: false);

		ProbeWinding(api, store, root, assetsDirectory);
		ProbeReferenceWinding(api);
		ProbeBoneScaleBody(api, store, root, assetsDirectory);
		ProbeEditModeStillness(api, prefabPath, assetsDirectory, skinning);
		ProbeStopRestores(api, prefabPath, assetsDirectory, skinning);
		Simulate(store, root, physics, skinning, models, assetsDirectory);
	}

	/// <summary>
	/// Сцена БЕЗ Play обязана стоять, а с Play - ожить.
	///
	/// Проверяется ПАРОЙ прогонов на одной сцене, отличающихся только флагом: «ничего не движется»
	/// само по себе ничего не доказывает - ровно так же выглядит сцена, в которой физика не
	/// заводится вовсе или анимация сломана. Смысл имеет только разница.
	///
	/// Меряются обе стороны сразу: положение рэгдолльной лисы (падает ли она) и время клипа у ходока
	/// (идёт ли анимация). Гейт легко поставить на одно и забыть про другое, и по картинке это не
	/// видно: стоящий персонаж с текущим временем клипа и лежащий с застывшим выглядят одинаково
	/// неправильно.
	/// </summary>
	private static void ProbeEditModeStillness(IGraphicsApi api, string prefabPath,
		string assetsDirectory, DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning)
	{
		var stopped = RunGated(api, prefabPath, assetsDirectory, skinning, playing: false);
		var playing = RunGated(api, prefabPath, assetsDirectory, skinning, playing: true);

		// Сантиметр по рэгдоллу и сотая секунды по клипу: в остановленной сцене это ровно нули, а
		// допуск оставлен под накопление float, а не под «почти не движется».
		bool stillWhenStopped = stopped.RagdollDrop < 0.01f && stopped.ClipTime < 0.01f;
		bool aliveWhenPlaying = playing.RagdollDrop > 0.05f && playing.ClipTime > 0.1f;

		Console.WriteLine($"[probe] scene: без Play - рэгдолл опустился на {stopped.RagdollDrop:0.####} м, " +
			$"время клипа {stopped.ClipTime:0.###} с {(stillWhenStopped ? "OK (сцена стоит)" : "СЦЕНА ЖИВЁТ БЕЗ PLAY")}");
		Console.WriteLine($"[probe] scene: с Play - рэгдолл опустился на {playing.RagdollDrop:0.####} м, " +
			$"время клипа {playing.ClipTime:0.###} с {(aliveWhenPlaying ? "OK (сцена ожила)" : "СЦЕНА НЕ ОЖИЛА ПО PLAY")}");
	}

	/// <summary>
	/// Stop обязан вернуть сцену к последнему авторскому состоянию.
	///
	/// Проверяются ДВА разных механизма отката, и они не заменяют друг друга:
	/// - трансформы сущностей откатывает снимок Play Mode (ECS-компоненты, см. InspectorWindow.Stop);
	/// - поза персонажа - НЕТ. Тела рэгдолла живут сбоку от ECS, и у упавшей лисы в компонентах
	///   ничего не менялось вовсе (Enabled и Physical у неё авторские). Такой персонаж остаётся
	///   лежать там, где упал, при полностью корректном откате компонентов.
	///
	/// Поэтому меряется именно ПОЗА - высота корня рэгдолла, - а не позиция сущности: по второй
	/// поломка не видна.
	/// </summary>
	private static void ProbeStopRestores(IGraphicsApi api, string prefabPath, string assetsDirectory,
		DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning)
	{
		var store = new EntityStore();
		var root = PrefabAsset.Instantiate(store, prefabPath);

		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildStatics(api, store, root, assetsDirectory, physics, flipWinding: false, quiet: true);

		var animation = new AnimationDriver(skinning) { Physics = physics };
		var driver = new CharacterMotionDriver();
		var skinned = new List<Entity>();
		Entity ragdollFox = default;

		foreach (var entity in Descendants(root))
		{
			if (!entity.HasComponent<ModelRenderer>())
			{
				continue;
			}

			string path = Path.Combine(assetsDirectory,
				entity.GetComponent<ModelRenderer>().modelRef.Path ?? string.Empty);

			if (!File.Exists(path))
			{
				continue;
			}

			var model = ModelLoader.Load(api, path, new ModelLoadOptions
			{
				VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
				PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
				OptimizeMesh = false,
				GenerateLods = false,
			});

			if (model.Skeleton == null)
			{
				continue;
			}

			animation.AddInstance(entity.Id, model, -1);
			animation.SetAvatar(entity.Id, HumanoidAvatarAsset.Load(path) ?? HumanoidAutoMap.Build(model.Skeleton));
			skinned.Add(entity);

			if (entity.HasComponent<RagdollComponent>() && !entity.HasComponent<CircleMoveComponent>() &&
				ragdollFox.IsNull)
			{
				ragdollFox = entity;
			}
		}

		if (ragdollFox.IsNull)
		{
			return;
		}

		void Tick(bool playing)
		{
			physics.Paused = !playing;
			driver.Steer(store, physics, playing, playing ? Step : 0f, animation);
			physics.Update(Step);
			driver.Apply(store, physics);

			animation.BeginFrame();
			foreach (var entity in skinned)
			{
				animation.Update(entity, PrefabSceneViewport.ComputeWorldMatrix(entity), playing ? Step : 0f);
			}
		}

		// Снимок авторского состояния - ровно то, что делает Play Mode при нажатии Play.
		var authored = new Dictionary<int, (Vector3 Position, Quaternion Rotation)>();
		foreach (var entity in Descendants(root))
		{
			authored[entity.Id] = (entity.Position.value, entity.Rotation.value);
		}

		Tick(playing: false);
		animation.TryGetRagdollRootWorld(ragdollFox.Id, out var before);

		for (int i = 0; i < 180; i++)
		{
			Tick(playing: true);
		}

		animation.TryGetRagdollRootWorld(ragdollFox.Id, out var during);

		// Stop: откат компонентов (снимок) + снятие состояния, живущего сбоку.
		foreach (var entity in Descendants(root))
		{
			if (authored.TryGetValue(entity.Id, out var pose))
			{
				entity.Position = new Position(pose.Position.X, pose.Position.Y, pose.Position.Z);
				entity.Rotation = new Rotation(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
			}
		}

		animation.EndPlay();
		Tick(playing: false);

		animation.TryGetRagdollRootWorld(ragdollFox.Id, out var after);

		float fell = before.Y - during.Y;
		float residual = MathF.Abs(after.Y - before.Y);

		// Сантиметр: поза пересобирается из тех же матриц, что и в начале, и расхождение здесь - это
		// разложение матриц, а не «почти вернулось».
		bool restored = residual < 0.01f;

		// Падение обязано быть заметным - иначе сравнивать не с чем и проверка слепа.
		bool fellEnough = fell > 0.1f;

		Console.WriteLine($"[probe] scene: Stop - корень рэгдолла y={before.Y:0.###} → упал до " +
			$"{during.Y:0.###} (на {fell:0.###} м{(fellEnough ? "" : " - СЛИШКОМ МАЛО, проверка слепая")}) " +
			$"→ после Stop {after.Y:0.###}, остаточное расхождение {residual:0.####} " +
			$"{(restored ? "OK (вернулось к авторскому)" : "НЕ ВЕРНУЛОСЬ")}");

		// --- Перенос гизмо в режиме редактирования --------------------------------------------------
		//
		// Физика персонажа обязана ЕХАТЬ ЗА трансформом сущности, пока идёт редактирование. Тела
		// рэгдолла собираются один раз и ведутся СКОРОСТЬЮ - а при нулевом шаге скорость не двигает
		// ничего, и персонаж, которого автор тащит гизмо, оставался стоять позой на прежнем месте:
		// сущность уехала, поза - нет.
		const float shift = 3f;
		var moved = ragdollFox.Position.value;
		ragdollFox.Position = new Position(moved.X + shift, moved.Y, moved.Z);

		Tick(playing: false);
		animation.TryGetRagdollRootWorld(ragdollFox.Id, out var afterMove);

		float travelled = afterMove.X - after.X;

		// Сантиметр от заданного сдвига: тела ставятся ровно в позу, и расхождение здесь - только
		// разложение матриц.
		bool follows = MathF.Abs(travelled - shift) < 0.01f;

		Console.WriteLine($"[probe] scene: перенос в редакторе на {shift} м - корень рэгдолла проехал " +
			$"{travelled:0.###} м {(follows ? "OK (физика едет за трансформом)" : "ФИЗИКА НЕ ЗАВИСИТ ОТ ТРАНСФОРМА")}");
	}

	/// <summary>Две секунды сцены при заданном флаге Play. Возвращает, насколько опустился рэгдолл и
	/// сколько времени накрутил клип.</summary>
	private static (float RagdollDrop, float ClipTime) RunGated(IGraphicsApi api, string prefabPath,
		string assetsDirectory, DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning,
		bool playing)
	{
		// СВОЙ экземпляр сцены на каждый прогон. Проверка ДВИГАЕТ персонажей - это её предмет, - и
		// сделанная на общем сторе, она отравила бы всё, что идёт после: главный прогон стартовал бы
		// из середины круга, с уже сдвинутым таймером падения. Ровно это и случилось при первом
		// запуске: «оборотов 0.933 (ожидалось 1.114) НЕ ДОШЁЛ» на исправном коде.
		var store = new EntityStore();
		var root = PrefabAsset.Instantiate(store, prefabPath);

		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildStatics(api, store, root, assetsDirectory, physics, flipWinding: false, quiet: true);

		// Гейт воспроизводится ТОЧНО так же, как в редакторе (см. PrefabSceneViewport): пауза мира и
		// нулевой шаг анимации. Пробник, гоняющий свой вариант гейта, проверял бы себя.
		physics.Paused = !playing;

		var animation = new AnimationDriver(skinning) { Physics = physics };
		var models = new Dictionary<int, ModelLoader>();
		Entity ragdollFox = default;
		Entity walker = default;

		foreach (var entity in Descendants(root))
		{
			if (!entity.HasComponent<ModelRenderer>())
			{
				continue;
			}

			string path = Path.Combine(assetsDirectory,
				entity.GetComponent<ModelRenderer>().modelRef.Path ?? string.Empty);

			if (!File.Exists(path))
			{
				continue;
			}

			var model = ModelLoader.Load(api, path, new ModelLoadOptions
			{
				VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
				PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
				OptimizeMesh = false,
				GenerateLods = false,
			});

			if (model.Skeleton == null)
			{
				continue;
			}

			models[entity.Id] = model;
			animation.AddInstance(entity.Id, model, -1);
			animation.SetAvatar(entity.Id, HumanoidAvatarAsset.Load(path) ?? HumanoidAutoMap.Build(model.Skeleton));

			// Рэгдолльная лиса - та, что падает без скрипта движения; ходок - та, у которой он есть.
			if (entity.HasComponent<RagdollComponent>() && !entity.HasComponent<CircleMoveComponent>() &&
				ragdollFox.IsNull)
			{
				ragdollFox = entity;
			}

			if (entity.HasComponent<CircleMoveComponent>() && walker.IsNull)
			{
				walker = entity;
			}
		}

		float startY = ragdollFox.IsNull ? 0f : ragdollFox.Position.value.Y;
		var driver = new CharacterMotionDriver();

		for (int i = 0; i < 120; i++)
		{
			driver.Steer(store, physics, playing, playing ? Step : 0f, animation);
			physics.Update(Step);
			driver.Apply(store, physics);

			animation.BeginFrame();
			foreach (var (id, _) in models)
			{
				if (store.TryGetEntityById(id, out var entity))
				{
					animation.Update(entity, PrefabSceneViewport.ComputeWorldMatrix(entity),
						playing ? Step : 0f);
				}
			}
		}

		// Опускание рэгдолла меряется по КОСТИ, а не по трансформу сущности: сущность стоит на месте
		// всё падение, и по ней «упал» и «не упал» неразличимы.
		float drop = 0f;
		if (!ragdollFox.IsNull && animation.TryGetRagdollRootWorld(ragdollFox.Id, out var rootWorld))
		{
			drop = MathF.Max(startY - rootWorld.Y, 0f);
		}

		float clipTime = walker.IsNull || !walker.HasComponent<Animator>()
			? 0f
			: walker.GetComponent<Animator>().Time;

		return (drop, clipTime);
	}

	/// <summary>
	/// В КАКОЙ БУФЕР дебага уезжают каркасы коллайдеров - в депт-тестируемый или в «поверх всего».
	///
	/// Проверяется числом, а не глазами, ровно потому, что глазами разница между «капсулы рисуются с
	/// депт-тестом» и «капсулы не рисуются вовсе» неотличима: и там, и там пустой экран - персонаж
	/// закрывает собой собственный коллайдер. У DebugDraw два независимых буфера (см. его шапку), и
	/// вопрос сводится к тому, в котором из них оказались вершины.
	///
	/// Заодно сверяется, что общий флаг физики коллайдеров НЕ КАСАЕТСЯ: они разъехались на два поля
	/// намеренно, и слияние обратно выглядело бы как «всё работает», пока кто-нибудь не включит
	/// статику сцены и не получит сетку по всему экрану.
	/// </summary>
	private static void ProbeColliderOverlay(ScenePhysics physics)
	{
		var draw = new DebugDraw { Enabled = true };

		var onTopOptions = new PhysicsDebugOptions { Colliders = true, CollidersDepthTested = false };
		draw.Clear();
		PhysicsDebugDraw.Draw(draw, physics, onTopOptions);
		int onTopBucket = draw.OnTopCount;
		int depthBucket = draw.DepthTestedCount;

		var depthOptions = new PhysicsDebugOptions { Colliders = true, CollidersDepthTested = true };
		draw.Clear();
		PhysicsDebugDraw.Draw(draw, physics, depthOptions);
		int depthOnTop = draw.OnTopCount;
		int depthDepth = draw.DepthTestedCount;

		// «Остальное поверх» при выключенных коллайдерах-поверх не должно перетащить их за собой.
		var mixedOptions = new PhysicsDebugOptions
		{
			Colliders = true,
			CollidersDepthTested = true,
			OnTop = true,
		};
		draw.Clear();
		PhysicsDebugDraw.Draw(draw, physics, mixedOptions);
		int mixedOnTop = draw.OnTopCount;

		bool byDefaultOnTop = onTopBucket > 0 && depthBucket == 0;
		bool switchable = depthDepth > 0 && depthOnTop == 0;
		bool independent = mixedOnTop == 0;

		Console.WriteLine($"[probe] scene: коллайдеры в дебаге - по умолчанию поверх: вершин " +
			$"{onTopBucket} поверх / {depthBucket} с депт-тестом {(byDefaultOnTop ? "OK" : "НЕ ПОВЕРХ")}; " +
			$"галочкой обратно: {depthOnTop} / {depthDepth} {(switchable ? "OK" : "НЕ ПЕРЕКЛЮЧАЕТСЯ")}; " +
			$"общий флаг физики их не трогает {(independent ? "OK" : "ТАЩИТ ЗА СОБОЙ")}");

		ReportCapsuleRadii(physics);
	}

	/// <summary>
	/// РАЗБРОС радиусов капсул в мире.
	///
	/// Отвечает ровно на «у лисы все коллайдеры одинаковые»: у рэгдолла, построенного по мешу,
	/// туловище в разы толще лапы, и одинаковые радиусы означают, что радиусы взяты не из меша - а
	/// это ровно то, чего по картинке не отличить от «модель такая». Печатаются min/max и число
	/// РАЗЛИЧНЫХ значений: одно значение на два десятка костей - диагноз сам по себе.
	/// </summary>
	private static void ReportCapsuleRadii(ScenePhysics physics)
	{
		var simulation = physics.World.Simulation;
		var radii = new List<float>();

		for (int setIndex = 0; setIndex < simulation.Bodies.Sets.Length; setIndex++)
		{
			ref var set = ref simulation.Bodies.Sets[setIndex];
			if (!set.Allocated)
			{
				continue;
			}

			for (int i = 0; i < set.Count; i++)
			{
				var shape = simulation.Bodies[set.IndexToHandle[i]].Collidable.Shape;
				if (shape.Exists && shape.Type == BepuPhysics.Collidables.Capsule.Id)
				{
					radii.Add(simulation.Shapes.GetShape<BepuPhysics.Collidables.Capsule>(shape.Index).Radius);
				}
			}
		}

		if (radii.Count == 0)
		{
			Console.WriteLine("[probe] scene: капсул в мире нет - разброс радиусов проверять не на чем");
			return;
		}

		radii.Sort();

		// Округление до десятых миллиметра: радиусы приезжают из усреднения по вершинам, и
		// «различных значений» без округления считалось бы по шуму младших разрядов.
		var distinct = new HashSet<int>();
		foreach (float r in radii)
		{
			distinct.Add((int)MathF.Round(r * 10000f));
		}

		float min = radii[0];
		float max = radii[^1];

		// Считаются ВСЕ капсулы мира, включая тело ходока (CharacterBodyComponent) - оно задано
		// автором и в разброс костей рэгдолла не входит по смыслу. Вердикт поэтому по ЧИСЛУ
		// РАЗЛИЧНЫХ значений, а не по отношению max/min: одно-два значения на три десятка костей
		// означают override, сколько бы ни было между ними раз.
		Console.WriteLine($"[probe] scene: радиусы капсул - {radii.Count} шт (с телом ходока), " +
			$"{min:0.####}..{max:0.####} м, различных значений {distinct.Count} " +
			$"{(distinct.Count > 3 ? "OK - по толщине частей тела" : "ВСЕ ОДИНАКОВЫЕ (BoneRadius override?)")}");
	}

	/// <summary>
	/// Тело РАЗМЕРОМ С КОСТЬ РЭГДОЛЛА на полу сцены.
	///
	/// Кость лисы в мире - это капсула радиусом 2 единицы модели × масштаб сущности 0.01 = 0.02 м, а
	/// <c>PhysicsWorld.AddDynamic</c> заводит тело со спекулятивной маржой 0.1 м по умолчанию. Маржа
	/// впятеро больше самого тела означает, что контакт создаётся за пять радиусов до поверхности и
	/// решатель весь шаг работает с «предсказанным» касанием - на таких телах это даёт дрожание и
	/// разлёт. В картинке это выглядит как разъехавшаяся палитра скиннинга: кости уносит, и меш
	/// растягивает в звезду.
	///
	/// Меряются ДВЕ вещи: осело ли тело на полу и не растёт ли его скорость. Первое ловит проваливание,
	/// второе - расходящуюся симуляцию, и одно без другого не диагноз.
	/// </summary>
	private static void ProbeBoneScaleBody(IGraphicsApi api, EntityStore store, Entity root,
		string assetsDirectory)
	{
		foreach (float margin in new[] { 0.1f, 0.002f })
		{
			using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
			BuildStatics(api, store, root, assetsDirectory, physics, flipWinding: false, quiet: true);

			const float radius = 0.02f;
			var shape = physics.World.AddCapsule(radius, 0.1f);
			var body = physics.World.AddDynamic(new RigidPose(new Vector3(2f, 0.5f, -4.3f)), shape,
				mass: 0.5f, speculativeMargin: margin);

			float peakSpeed = 0f;
			float lateSpeed = 0f;

			for (int i = 0; i < 240; i++)
			{
				physics.Update(Step);

				float speed = physics.World.Simulation.Bodies[body].Velocity.Linear.Length();
				peakSpeed = MathF.Max(peakSpeed, speed);

				// Последняя секунда: тело обязано уже лежать. Скорость здесь - это ровно та энергия,
				// которую симуляция не гасит, а накачивает.
				if (i >= 180)
				{
					lateSpeed = MathF.Max(lateSpeed, speed);
				}
			}

			var pose = physics.World.Simulation.Bodies[body].Pose;
			bool settled = lateSpeed < 0.05f && MathF.Abs(pose.Position.Y) < 0.2f;

			Console.WriteLine($"[probe] scene: тело размером с кость (r={radius}), маржа {margin} - " +
				$"легло на y={pose.Position.Y:0.####}, пик скорости {peakSpeed:0.###}, " +
				$"в конце {lateSpeed:0.####} {(settled ? "OK" : "НЕ УСПОКОИЛОСЬ")}");
		}
	}

	/// <summary>
	/// Тот же вопрос об обходе, но на ЧУЖОЙ модели - Sponza из Khronos-семплов.
	///
	/// Нужна, чтобы отделить два совершенно разных диагноза, которые по демо-сцене неразличимы:
	/// «обход неверен у ГЕНЕРАТОРА площадки» (тогда чинить SampleGroundBuilder) и «обход неверен на
	/// границе движок↔Bepu для ЛЮБОЙ импортированной геометрии» (тогда чинить AddTriangleMesh).
	/// Sponza сделана не нами и заведомо каноническая, поэтому её ответ - про конвенцию, а не про
	/// нашу опечатку.
	/// </summary>
	private static void ProbeReferenceWinding(IGraphicsApi api)
	{
		string path = Path.Combine(AppContext.BaseDirectory, "EditorAssets", "models", "Sponza.gltf");

		if (!File.Exists(path))
		{
			Console.WriteLine("[probe] scene: Sponza не найдена - конвенцию обхода сверить не с чем");
			return;
		}

		var model = ModelLoader.Load(api, path, new ModelLoadOptions
		{
			VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
			PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
			OptimizeMesh = false,
			GenerateLods = false,
		});

		float direct = DropOnModel(model, flipWinding: false);
		float flipped = DropOnModel(model, flipWinding: true);

		// Судить по «упало или нет» здесь нельзя: Sponza - закрытый интерьер, и сфера, прошедшая
		// сквозь верхнюю грань пола, ложится на его ИЗНАНКУ, то есть тоже останавливается. Разница
		// между обходами - в ВЫСОТЕ: правильный останавливает на первой же поверхности, то есть
		// ВЫШЕ. Именно поэтому сравниваются два числа, а не проверяется одно.
		bool directHigher = direct > flipped + 0.1f;

		Console.WriteLine($"[probe] scene: Sponza (чужая модель, конвенция) - штатный обход: " +
			$"y={direct:0.###}, испорченный: y={flipped:0.###} " +
			$"{(directHigher ? "OK (штатный держит на верхней поверхности)" : "ШТАТНЫЙ ПРОПУСКАЕТ СКВОЗЬ ПОЛ")}");
	}

	private static float DropOnModel(ModelLoader model, bool flipWinding)
	{
		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));

		var positions = new List<Vector3>();
		var indices = new List<uint>();

		var instanceIndices = new int[model.instances.Count];
		for (int i = 0; i < instanceIndices.Length; i++)
		{
			instanceIndices[i] = i;
		}

		PrefabSceneViewport.AppendModelGeometry(model, instanceIndices, Matrix4x4.Identity,
			positions, indices);

		if (flipWinding)
		{
			for (int i = 0; i + 2 < indices.Count; i += 3)
			{
				(indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
			}
		}

		physics.BeginStatics();
		physics.AddStaticMesh(
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(positions),
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));
		physics.EndStatics();

		var body = physics.World.AddDynamic(new RigidPose(new Vector3(0f, 3f, 0f)),
			physics.World.AddSphere(0.25f), mass: 10f);

		for (int i = 0; i < 120; i++)
		{
			physics.Update(Step);
		}

		return physics.World.Simulation.Bodies[body].Pose.Position.Y;
	}

	/// <summary>
	/// Держит ли статика сцены тело ВООБЩЕ - и в каком обходе треугольников.
	///
	/// Меш в Bepu ОДНОСТОРОННИЙ, и цена ошибки обхода не «нормали чуть не те», а полное отсутствие
	/// столкновений: тело уходит сквозь пол в свободном падении. Определяется ЭКСПЕРИМЕНТОМ, а не по
	/// памяти о конвенции: сфера роняется на настоящую геометрию сцены обоими обходами, и держит её
	/// ровно один. Ровно тот же способ, которым обход выбирали для PhysicsWorld.AddTriangleMesh.
	/// </summary>
	private static void ProbeWinding(IGraphicsApi api, EntityStore store, Entity root, string assetsDirectory)
	{
		float direct = DropSphere(api, store, root, assetsDirectory, flipWinding: false);
		float flipped = DropSphere(api, store, root, assetsDirectory, flipWinding: true);

		// Сфера падает с двух метров на пол сцены (y=0) и обязана лечь на свой радиус.
		const float expected = 0.25f;
		bool directOk = MathF.Abs(direct - expected) < 0.02f;
		bool flippedOk = MathF.Abs(flipped - expected) < 0.02f;

		// Держать обязан ШТАТНЫЙ путь: разворот под односторонний меш Bepu делает сам
		// PhysicsWorld.AddTriangleMesh, и «развёрнутый» здесь - это геометрия, испорченная нарочно.
		Console.WriteLine($"[probe] scene: обход треугольников - штатный: сфера на y={direct:0.###} " +
			$"{(directOk ? "ДЕРЖИТ OK" : "ПРОВАЛИЛАСЬ")}, испорченный нарочно: y={flipped:0.###} " +
			$"{(flippedOk ? "тоже держит - РАЗВОРОТ НИ НА ЧТО НЕ ВЛИЯЕТ, проверка слепая" : "проваливается, как и должна")}");
	}

	private static float DropSphere(IGraphicsApi api, EntityStore store, Entity root,
		string assetsDirectory, bool flipWinding)
	{
		using var physics = new ScenePhysics(new Vector3(0f, -9.81f, 0f));
		BuildStatics(api, store, root, assetsDirectory, physics, flipWinding, quiet: true);

		// Точка старта - там же, где стоит персонаж: пол там ровный, и вопрос ровно о нём.
		var body = physics.World.AddDynamic(new RigidPose(new Vector3(2f, 2f, -4.3f)),
			physics.World.AddSphere(0.25f), mass: 10f);

		for (int i = 0; i < 120; i++)
		{
			physics.Update(Step);
		}

		return physics.World.Simulation.Bodies[body].Pose.Position.Y;
	}

	/// <summary>
	/// Статика сцены - тем же правилом, что и в редакторе: все НЕскиннед-модели одним мешом, каждая
	/// со своей мировой матрицей из иерархии префаба. Скиннед в статику не идут (персонаж не должен
	/// быть полом сам себе).
	/// </summary>
	private static Dictionary<int, ModelLoader> BuildStatics(IGraphicsApi api, EntityStore store,
		Entity root, string assetsDirectory, ScenePhysics physics, bool flipWinding, bool quiet = false)
	{
		var loaded = new Dictionary<int, ModelLoader>();
		var positions = new List<Vector3>();
		var indices = new List<uint>();

		physics.BeginStatics();

		foreach (var entity in Descendants(root))
		{
			if (!entity.HasComponent<ModelRenderer>())
			{
				continue;
			}

			string reference = entity.GetComponent<ModelRenderer>().modelRef.Path ?? string.Empty;
			if (reference.Length == 0)
			{
				continue;
			}

			string path = Path.Combine(assetsDirectory, reference);
			if (!File.Exists(path))
			{
				Console.WriteLine($"[probe] scene: модель не найдена: {path}");
				continue;
			}

			var model = ModelLoader.Load(api, path, new ModelLoadOptions
			{
				VertexShader = new EditorRef("shader/UnlitInstancedVS.hlsl"),
				PixelShader = new EditorRef("shader/UnlitInstancedPS.hlsl"),
				OptimizeMesh = false,
				GenerateLods = false,
			});

			loaded[entity.Id] = model;

			if (model.Skeleton != null)
			{
				if (!quiet)
				{
					// Габарит скиннед-модели В МИРЕ - тем же составлением трансформа, которым её
					// ставит в сцену рендер (ComposeInstanceTransform: локальный трансформ инстанса ×
					// мировая матрица сущности). Число отвечает на вопрос, который по картинке
					// решить нельзя: персонаж «гигантский» потому, что рендер потерял масштаб
					// сущности, или потому, что его разорвала палитра скиннинга. Первое видно здесь,
					// второе - нет, и разделить их можно только так.
					positions.Clear();
					indices.Clear();

					var all = new int[model.instances.Count];
					for (int i = 0; i < all.Length; i++)
					{
						all[i] = i;
					}

					PrefabSceneViewport.AppendModelGeometry(model, all,
						PrefabSceneViewport.ComputeWorldMatrix(entity), positions, indices);

					var min = new Vector3(float.MaxValue);
					var max = new Vector3(float.MinValue);
					foreach (var p in positions)
					{
						min = Vector3.Min(min, p);
						max = Vector3.Max(max, p);
					}

					var size = positions.Count > 0 ? max - min : Vector3.Zero;

					Console.WriteLine($"[probe] scene: '{entity.GetComponent<EntityName>().value}' - " +
						$"скиннед ({model.Skeleton.JointCount} костей), в статику не идёт; " +
						$"габарит в мире (bind) {size.X:0.###}×{size.Y:0.###}×{size.Z:0.###}, " +
						$"низ y={min.Y:0.###}");
				}

				continue;
			}

			var instanceIndices = new int[model.instances.Count];
			for (int i = 0; i < instanceIndices.Length; i++)
			{
				instanceIndices[i] = i;
			}

			positions.Clear();
			indices.Clear();
			PrefabSceneViewport.AppendModelGeometry(model, instanceIndices,
				PrefabSceneViewport.ComputeWorldMatrix(entity), positions, indices);

			if (flipWinding)
			{
				for (int i = 0; i + 2 < indices.Count; i += 3)
				{
					(indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
				}
			}

			physics.AddStaticMesh(
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(positions),
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices));

			if (!quiet)
			{
				Console.WriteLine($"[probe] scene: '{entity.GetComponent<EntityName>().value}' - " +
					$"{indices.Count / 3} треугольников в статику");
			}
		}

		physics.EndStatics();

		if (!quiet)
		{
			Console.WriteLine($"[probe] scene: статика собрана, треугольников {physics.StaticTriangleCount}");
		}

		return loaded;
	}

	/// <summary>
	/// Шагает сцену КАК РЕДАКТОР: тот же порядок кадра, что у PrefabSceneViewport (Steer → шаг
	/// физики → Apply → AnimationDriver.Update на каждого персонажа), с настоящим AnimationDriver -
	/// клипы, foot IK и рэгдоллы работают, а не имитируются. Печатает траекторию ходока по секундам
	/// и, главное, ГАБАРИТ ДЕФОРМИРОВАННОГО МЕША каждого персонажа: CPU-скиннинг вершин той самой
	/// палитрой, которая ушла бы в GPU. «Части гигантского размера» на скриншоте - это палитра, и
	/// увидеть её иначе, чем прогнав весь путь позы, нельзя.
	/// </summary>
	private static void Simulate(EntityStore store, Entity root, ScenePhysics physics,
		DecaEngine.Graphics.Diligent.DiligentSkinningPass skinning,
		Dictionary<int, ModelLoader> models, string assetsDirectory)
	{
		var driver = new CharacterMotionDriver();

		// Персонажи - через настоящий AnimationDriver, без участков палитры (offset -1: инстансы в
		// батч-рендерере не регистрируются, заливать нечего). Аватар - из файла рядом с моделью, как
		// в редакторе; без файла - автоматический.
		var animation = new AnimationDriver(skinning) { Physics = physics };
		var skinnedEntities = new List<Entity>();
		var hipJointOf = new Dictionary<int, int>();

		foreach (var entity in Descendants(root))
		{
			if (models.TryGetValue(entity.Id, out var model) && model.Skeleton != null)
			{
				animation.AddInstance(entity.Id, model, -1);

				string modelPath = Path.Combine(assetsDirectory,
					entity.GetComponent<ModelRenderer>().modelRef.Path ?? string.Empty);
				var avatar = HumanoidAvatarAsset.Load(modelPath) ?? HumanoidAutoMap.Build(model.Skeleton);
				animation.SetAvatar(entity.Id, avatar);

				hipJointOf[entity.Id] = avatar.Resolve(model.Skeleton)[(int)HumanoidBone.Hips];
				skinnedEntities.Add(entity);
			}
		}

		Entity character = default;
		var move = default(CircleMoveComponent);

		store.Query<CircleMoveComponent, CharacterBodyComponent>().ForEachEntity(
			(ref CircleMoveComponent m, ref CharacterBodyComponent body, Entity entity) =>
		{
			character = entity;
			move = m;
		});

		if (character.IsNull)
		{
			Console.WriteLine("[probe] scene: персонажа со скриптом движения и Character Body в сцене нет");
			return;
		}

		var start = character.Position.value;
		Console.WriteLine($"[probe] scene: персонаж '{character.GetComponent<EntityName>().value}' " +
			$"из {start}, круг R={move.Radius} вокруг {move.Center}, {move.Speed} ед/с");

		// Длину прогона можно вытянуть переменной: 14 секунд покрывают ОДИН цикл падения, а утечка
		// на повторных циклах (рэгдолл теперь пересобирается на каждом падении) видна только на
		// длинной дистанции - см. счётчик тел в строках траектории.
		float seconds = float.TryParse(
			Environment.GetEnvironmentVariable("DECA_PROBE_SCENESECONDS"),
			System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out float custom) && custom > 0f
			? custom
			: 14f;
		int steps = (int)MathF.Round(seconds / Step);

		float worstRadius = 0f;
		float lowest = start.Y;
		float highest = start.Y;
		float highestAt = 0f;
		float movingSeconds = 0f;
		float turned = 0f;
		float previousAngle = CircleMotion.AngleOf(move, start);
		bool walkerIkSeen = false;
		float? walkerLowY = null;
		float walkerWalkWeight = -1f;
		float walkerLyingIdleWeight = -1f;
		float playerIdleWeight = -1f;
		float reactionPeak = 0f;
		float reactionAfter = -1f;
		float parkedPurity = -1f;
		float parkedSpeed = -1f;
		var recoveryPrevHip = Vector3.Zero;
		var recoveryPrevState = CharacterMotionState.Moving;
		bool recoveryPrevValid = false;
		float worstRecoveryJump = -1f;
		float worstFrameJump = 0f;
		float worstFrameJumpAt = 0f;
		var characterInfos = new List<AnimationDriver.CharacterInfo>();

		// Лиса игрока: в headless-прогоне ввода нет, и её локомоушен обязан стоять В СТОЙКЕ - это
		// проверка пары «идущий в шаге, стоящий в стойке» на одной сцене одним механизмом.
		Entity player = default;
		store.Query<PlayerMoveComponent, CharacterBodyComponent>().ForEachEntity(
			(ref PlayerMoveComponent _, ref CharacterBodyComponent _, Entity entity) => player = entity);

		// Кочка - лучом ДО прогона: «капсула не поднялась» не различает «геометрии нет», «геометрия
		// изнанкой вверх» и «капсула не взяла склон», а луч над гребнем отвечает на первые два.
		// Луч на ЗЕРКАЛЬНОЙ стороне - ловушка RH→LH импорта: площадка до кочки была z-симметричной,
		// и зеркалирование Z в ней было невидимо; уехавшая на +z кочка на самой сцене выглядит так
		// же, как невзятая.
		var crest = physics.SampleGround(new Vector3(0f, 1f, -2.3f), -Vector3.UnitY, 2f);
		var mirrored = physics.SampleGround(new Vector3(0f, 1f, 2.3f), -Vector3.UnitY, 2f);
		Console.WriteLine($"[probe] scene: кочка - луч над гребнем (z=-2.3) " +
			$"{(crest.Hit ? $"y={crest.Position.Y:0.###}" : "МИМО")}, " +
			$"на зеркальной стороне (z=+2.3) {(mirrored.Hit ? $"y={mirrored.Position.Y:0.###}" : "МИМО")}");

		// DECA_PROBE_SCENEINPUT=1 - синтетический ввод игрока: направление медленно вращается, бег
		// перемежается. За долгий прогон игрок побывает у стен, на кочке, в лежащем рэгдолле и в
		// идущей лисе - ровно те столкновения, которых у сцены без ввода не бывает вовсе, а у живого
		// игрока случаются в первую минуту.
		bool syntheticInput = Environment.GetEnvironmentVariable("DECA_PROBE_SCENEINPUT") == "1";

		for (int i = 0; i < steps; i++)
		{
			if (syntheticInput)
			{
				float now = (i + 1) * Step;
				float heading = now * 0.7f;

				driver.Input = new PlayerInput
				{
					MoveWorld = new Vector3(MathF.Cos(heading), 0f, MathF.Sin(heading)),
					Run = ((int)(now / 10f) & 1) == 0,
				};
			}

			// С пятой секунды игрок бежит НАИСКОСЬ В ПРАВУЮ СТЕНУ и скользит вдоль неё на ~1.7 м/с -
			// между Walk и Run. Это решающая проверка дискретного аллюра: у весов по скорости
			// персонаж, паркующийся между аллюрами, вечно жил бы в полусмеси (передние ноги галопа
			// в корпусе шага), у гистерезиса он обязан прийти к ЧИСТОМУ аллюру. До t=4 игрок стоит -
			// срез «стойка без ввода» на t=3 остаётся честным. Направление (2,0,1) выбрано, чтобы
			// скольжение шло В СТОРОНУ ОТКРЫТОГО КРАЯ, но не доходило до него за прогон: первая
			// версия с (1,1) бежала вдоль стены быстрее и УБЕГАЛА С ПЛОЩАДКИ - на срезе парковки
			// «прижатый к стене» игрок свободно падал за краем мира на полной скорости.
			// После среза парковки (t=10) ввод отпускается: скольжение вдоль стены идёт в сторону
			// открытого края площадки, и к t=14 игрок успевал убежать за него - в отчёте появлялся
			// «низ в мире y=-12», который через месяц кто-нибудь примет за провал сквозь пол.
			if (!syntheticInput && i + 1 > 240 && i + 1 <= 600)
			{
				driver.Input = new PlayerInput { MoveWorld = new Vector3(2f, 0f, 1f), Run = true };
			}

			// Порядок кадра - как в PrefabSceneViewport: физика (рулевое → шаг → перенос поз) СТРОГО
			// до анимации, потому что луч foot IK щупает мир этого кадра, а рэгдолл читает уже
			// проинтегрированные тела.
			driver.Steer(store, physics, active: true, Step, animation);
			physics.Update(Step);
			driver.Apply(store, physics);

			animation.BeginFrame();
			foreach (var skinnedEntity in skinnedEntities)
			{
				animation.Update(skinnedEntity, PrefabSceneViewport.ComputeWorldMatrix(skinnedEntity), Step);
			}

			ReportStateChange(character, (i + 1) * Step);
			LegSnapshotProbe.Poll(physics, animation, skinnedEntities, models, (i + 1) * Step, Step);

			// Конечность координат - КАЖДЫЙ шаг: улетевшее в бесконечность тело даёт NaN-габарит, и
			// широкая фаза Bepu умирает переполнением стека при построении дерева, не сказав, какое
			// тело виновато. Ловить надо на первом же нефинитном значении, пока стек цел.
			foreach (var skinnedEntity in skinnedEntities)
			{
				var entityPos = skinnedEntity.Position.value;
				bool finite = float.IsFinite(entityPos.X) && float.IsFinite(entityPos.Y) &&
					float.IsFinite(entityPos.Z);

				if (finite && animation.TryGetRagdollRootWorld(skinnedEntity.Id, out var ragdollRoot))
				{
					finite = float.IsFinite(ragdollRoot.X) && float.IsFinite(ragdollRoot.Y) &&
						float.IsFinite(ragdollRoot.Z);
				}

				if (!finite)
				{
					Console.WriteLine($"[probe] scene: НЕФИНИТНАЯ ПОЗА у " +
						$"'{skinnedEntity.GetComponent<EntityName>().value}' на t={(i + 1) * Step:0.00} с - " +
						$"сущность {entityPos}, дальше мир не жилец");
					return;
				}
			}

			// Время ХОДЬБЫ, а не время прогона: лежащий и встающий персонаж по кругу не движется,
			// и ожидание оборотов из полного времени было выполнимо только с посторонней тягой.
			// Ровно так и было: проверка «оборотов 1.07 из 1.11 OK» годами проходила потому, что
			// капсулу выталкивали kinematic-тела собственного рэгдолла и она ехала быстрее заказа.
			if (!character.HasComponent<FallRecoverComponent>() ||
				character.GetComponent<FallRecoverComponent>().State == CharacterMotionState.Moving)
			{
				movingSeconds += Step;
			}

			// НЕПРЕРЫВНОСТЬ подъёма: мировая позиция таза в кадре Falling→Recovering обязана
			// остаться у места лёжки. Сущность в этом кадре ПЕРЕНОСИТСЯ к рэгдоллу, и снимок
			// лежачей позы обязан быть ребейзнут в новый трансформ (см. BeginRecovery) - без
			// ребейза видимая поза прыгала на весь увоз рэгдолла («встаёт телепортом», жалоба
			// с толчка тряпичной лисы).
			if (character.HasComponent<FallRecoverComponent>() &&
				hipJointOf.TryGetValue(character.Id, out int walkerHip) && walkerHip >= 0 &&
				animation.TryGetPose(character.Id, out var walkerPose, out _))
			{
				var hipWorld = Vector3.Transform(walkerPose[walkerHip].Translation,
					PrefabSceneViewport.ComputeWorldMatrix(character));
				var walkerState = character.GetComponent<FallRecoverComponent>().State;

				if (recoveryPrevValid)
				{
					float jump = Vector3.Distance(hipWorld, recoveryPrevHip);

					if (walkerState == CharacterMotionState.Recovering &&
						recoveryPrevState == CharacterMotionState.Falling)
					{
						worstRecoveryJump = MathF.Max(worstRecoveryJump, jump);
					}

					// Худший скачок ЗА ВЕСЬ прогон - ловец любых телепортов позы: разворота
					// вставания, рестарта конверта хит-реакции, потери направления при пересоздании
					// тела. Честные скорости (падение, толчок) дают сантиметры за кадр.
					if (jump > worstFrameJump)
					{
						worstFrameJump = jump;
						worstFrameJumpAt = (i + 1) * Step;
					}
				}

				recoveryPrevHip = hipWorld;
				recoveryPrevState = walkerState;
				recoveryPrevValid = true;
			}

			var position = character.Position.value;

			float dx = position.X - move.Center.X;
			float dz = position.Z - move.Center.Z;
			float distance = MathF.Sqrt(dx * dx + dz * dz);

			worstRadius = MathF.Max(worstRadius, MathF.Abs(distance - move.Radius));
			lowest = MathF.Min(lowest, position.Y);

			if (position.Y > highest)
			{
				highest = position.Y;
				highestAt = (i + 1) * Step;
			}

			// Foot IK и веса локомоушена ходока снимаются НА ГРЕБНЕ КОЧКИ (t=3, angle=pi/2), а не в
			// конце прогона: к 14-й секунде персонаж лежит рэгдоллом, и оба ответа там не про то.
			if (i + 1 == 180)
			{
				animation.DescribeCharacters(characterInfos);
				foreach (var info in characterInfos)
				{
					if (info.EntityId == character.Id)
					{
						// НЕ МЕНЬШЕ двух ног: задняя пара обязательна, а точное равенство устарело с
					// приходом FrontLegs - у четвероногого ходока ног четыре.
					walkerIkSeen = info.LegCount >= 2 && info.IkApplied;
						walkerWalkWeight = info.Locomotion ? info.LocoWalkWeight : -1f;

						// Информативно, без вердикта: фазы события аллюра в клипах (см.
						// GaitPhaseOffset). Ноль у ОБОИХ - подозрение на потерянную humanoid-разметку
						// (выравнивание тогда молча мертво), но и легальный случай клипов, авторски
						// начатых с события.
						Console.WriteLine($"[probe] scene: локомоушен - фазы аллюра walk=" +
							$"{info.LocoWalkPhaseOffset:0.00}, run={info.LocoRunPhaseOffset:0.00}");
					}
					else if (!player.IsNull && info.EntityId == player.Id)
					{
						playerIdleWeight = info.Locomotion ? info.LocoIdleWeight : -1f;
					}
				}
			}

			// Хит-реакция: толчок ходоку на t=3.5 (идёт по ровному после кочки, до падения на 6-й
			// ещё далеко). Пик ищется МАКСИМУМОМ по окну реакции, а не срезом на фиксированном
			// кадре: форма конверта - деталь реализации, и проверка не должна знать, где у него
			// вершина. «После» - срез на t=4.6, когда конверт обязан истечь.
			if (i + 1 == 210)
			{
				animation.TriggerHitReaction(character.Id, new Vector3(0f, 0.8f, 2.2f));
			}

			if (i + 1 > 210 && i + 1 <= 270)
			{
				animation.DescribeCharacters(characterInfos);
				foreach (var info in characterInfos)
				{
					if (info.EntityId == character.Id)
					{
						reactionPeak = MathF.Max(reactionPeak, info.ReactionDeviation);
					}
				}
			}

			if (i + 1 == 276)
			{
				animation.DescribeCharacters(characterInfos);
				foreach (var info in characterInfos)
				{
					if (info.EntityId == character.Id)
					{
						reactionAfter = info.ReactionDeviation;
					}
				}
			}

			// Срез парковки - t=10: игрок трётся о стену уже секунды четыре, все кроссфейды давно
			// закончились, и вес обязан быть чистым.
			if (i + 1 == 600 && !player.IsNull)
			{
				animation.DescribeCharacters(characterInfos);
				foreach (var info in characterInfos)
				{
					if (info.EntityId == player.Id && info.Locomotion)
					{
						parkedPurity = MathF.Max(info.LocoWalkWeight, info.LocoRunWeight);
						parkedSpeed = info.LocoSpeed;
					}
				}
			}

			// Лежачий срез - ПО СОСТОЯНИЮ, а не по секунде: момент подъёма зависит от того, как
			// улёгся рэгдолл, и фиксированное «t=8» попадает то в лежание, то уже в разгон после
			// подъёма (замерено: стойка 1.00 в одном прогоне и 0.20 в другом на исправном коде).
			if (character.HasComponent<FallRecoverComponent>() &&
				character.GetComponent<FallRecoverComponent>() is { State: CharacterMotionState.Falling, StateTime: > 1f })
			{
				animation.DescribeCharacters(characterInfos);
				foreach (var info in characterInfos)
				{
					if (info.EntityId == character.Id)
					{
						walkerLyingIdleWeight = info.Locomotion ? info.LocoIdleWeight : -1f;
					}
				}
			}

			float angle = MathF.Atan2(dz, dx);
			turned += CircleMotion.Wrap(angle - previousAngle);
			previousAngle = angle;

			// Раз в секунду - строка. Персонаж, который встал на четвёртой секунде, и персонаж,
			// который не пошёл вовсе, по итоговым числам неразличимы.
			if ((i + 1) % 60 == 0)
			{
				Console.WriteLine($"[probe] scene: t={(i + 1) * Step:0.0} с  " +
					$"поз=({position.X:0.00}, {position.Y:0.000}, {position.Z:0.00})  " +
					$"радиус={distance:0.000}  оборотов={turned / MathF.Tau:0.000}  " +
					$"тел={physics.BodyCount}");
			}

			// Дебаг коллайдеров - на первом же кадре, когда тела персонажей уже заведены: до первого
			// Steer/Update в мире одна статика, и проверка «в какой буфер уехали каркасы» ответила бы
			// «ни в какой», не заметив разницы между «не поверх» и «нечего рисовать».
			if (i == 0)
			{
				ProbeColliderOverlay(physics);
			}

			// Габарит деформированного меша - в НАЧАЛЕ (первый кадр физики) и в конце: разрыв,
			// который случается от первого же шага симуляции, и разрыв, который накапливается,
			// диагностируются по-разному.
			if (i == 0 || i == steps - 1)
			{
				foreach (var skinnedEntity in skinnedEntities)
				{
					float? low = ReportDeformedExtents(animation, models[skinnedEntity.Id], skinnedEntity,
						(i + 1) * Step);

					// Низ ИДУЩЕГО персонажа на последнем кадре - ловец «вминается в пол»: провал
					// был в ПОЗЕ (foot IK прижимал лапу в замахе и утягивал таз), и ни одна метрика
					// тела его не видела - капсула шла ровно по поверхности.
					if (i == steps - 1 && skinnedEntity.Id == character.Id)
					{
						walkerLowY = low;
					}
				}
			}

			// Таз каждого рэгдолльного персонажа - для метрики ПОКОЯ ниже. Снимается на 6-й секунде
			// (упасть с 1.8 м и улечься - меньше двух) и в конце: разница между этими точками и есть
			// «уполз».
			// По НОМЕРУ шага, не по времени: накопленное (i+1)*Step никогда не равно 6.0 точно.
			if (i + 1 == 360 || i + 1 == steps)
			{
				foreach (var skinnedEntity in skinnedEntities)
				{
					// Метрика покоя - только для персонажей, которые ДОЛЖНЫ лежать. У ходока и
					// игрока снос равен пройденному пути, и «ПОЛЗЁТ» на них означало бы, что они
					// исправно ходят (игрок ловится на прогоне с синтетическим вводом).
					if (skinnedEntity.HasComponent<CircleMoveComponent>() ||
						skinnedEntity.HasComponent<PlayerMoveComponent>() ||
						!skinnedEntity.HasComponent<RagdollComponent>() ||
						!hipJointOf.TryGetValue(skinnedEntity.Id, out int hip) || hip < 0 ||
						!animation.TryGetPose(skinnedEntity.Id, out var modelMatrices, out _))
					{
						continue;
					}

					var world = PrefabSceneViewport.ComputeWorldMatrix(skinnedEntity);
					var hipWorld = (modelMatrices[hip] * world).Translation;

					if (i + 1 == 360)
					{
						_hipAtSettle[skinnedEntity.Id] = hipWorld;
					}
					else
					{
						// Рэгдолл, ЛЕЖАЩИЙ на полу, за восемь секунд никуда не ползёт: снос таза -
						// это ровно то «катится по полу, как камни», которое видно в редакторе и
						// которое не ловится ни габаритом палитры, ни скоростями отдельного кадра.
						var settled = _hipAtSettle.TryGetValue(skinnedEntity.Id, out var s) ? s : hipWorld;
						float drift = new Vector2(hipWorld.X - settled.X, hipWorld.Z - settled.Z).Length();

						Console.WriteLine($"[probe] scene: '{skinnedEntity.GetComponent<EntityName>().value}' " +
							$"покой - таз на y={hipWorld.Y:0.###}, снос за 6..14 с {drift:0.###} м " +
							$"{(drift < 0.15f ? "ЛЕЖИТ OK" : "ПОЛЗЁТ/КАТИТСЯ")}" +
							$"{(hipWorld.Y < -0.05f ? " ПОД ПОЛОМ" : "")}");
					}
				}
			}
		}

		float expectedTurns = movingSeconds * move.Speed / (MathF.Tau * move.Radius);

		bool onGround = MathF.Abs(lowest - start.Y) < 0.05f;
		bool circleOk = worstRadius < 0.1f;
		bool progressOk = MathF.Abs(turned / MathF.Tau - expectedTurns) < 0.1f;

		// Кочка на пути круга (SampleGroundBuilder.AddMound, высота 0.12): подъём тела на её высоту
		// доказывает, что капсула склон ВЗЯЛА, а не проехала сквозь или обогнула. Нижняя граница -
		// чуть ниже высоты гребня по хорде пути; верхняя ловит подлёт, и она ТЕСНАЯ не из
		// перфекционизма: капсула, дравшаяся с вывернутой изнанкой шапки гребня, взлетала на 0.288
		// при гребне 0.18 - по щедрому допуску это выглядело бы «взял кочку особенно хорошо».
		bool moundOk = highest - start.Y > 0.08f && highest - start.Y < 0.18f;

		Console.WriteLine($"[probe] scene: ИТОГ - оборотов {turned / MathF.Tau:0.000} " +
			$"(ожидалось {expectedTurns:0.000} за {movingSeconds:0.0} с ходьбы) " +
			$"{(progressOk ? "OK" : "НЕ ДОШЁЛ")}, " +
			$"худшее отклонение радиуса {worstRadius:0.####} {(circleOk ? "OK" : "СОШЁЛ С КРУГА")}, " +
			$"ниже всего опускался на {lowest - start.Y:0.####} {(onGround ? "OK" : "ПРОВАЛИЛСЯ")}, " +
			$"выше всего поднимался на {highest - start.Y:0.###} (t={highestAt:0.00}) " +
			$"{(moundOk ? "КОЧКУ ВЗЯЛ OK" : "КОЧКУ НЕ ВЗЯЛ")}, " +
			$"foot IK на гребне {(walkerIkSeen ? "применён OK" : "НЕ ПРИМЕНЁН")}, " +
			// Нижняя граница - по природному провису САМОГО КЛИПА: у лисы без foot IK вовсе (Run)
			// низ меша -0.036, и требовать от IK строже, чем от клипа, значит ловить не провал, а
			// анимацию. Старый провал (лапа в замахе прижималась к полу и тянула таз) давал десятки
			// сантиметров.
			$"лапы в конце y={walkerLowY:0.###} " +
			$"{(walkerLowY is > -0.06f and < 0.05f ? "НА ПОЛУ OK" : "ВМИНАЕТСЯ/ВИСИТ")}");

		// Порог - обычное кадровое движение падающего тела (сантиметры), а не точность: при
		// пропавшем ребейзе снимка скачок равен всему увозу рэгдолла от точки падения (дециметры
		// и больше, чем сильнее толкнули). -1 - подъём за прогон не случился, и проверка молчит.
		if (worstRecoveryJump >= 0f)
		{
			bool recoveryOk = worstRecoveryJump < 0.1f && worstFrameJump < 0.15f;
			Console.WriteLine($"[probe] scene: непрерывность позы - скачок таза в кадре старта подъёма " +
				$"{worstRecoveryJump:0.###} м, худший за прогон {worstFrameJump:0.###} м " +
				$"(t={worstFrameJumpAt:0.00}) {(recoveryOk ? "БЕЗ ТЕЛЕПОРТОВ OK" : "ЕСТЬ ТЕЛЕПОРТ ПОЗЫ")}");
		}

		// Локомоушен - ПАРАМИ на одной сцене: идущий в шаге И стоящий в стойке (игрок без ввода),
		// идущий в шаге И он же лёжа в стойке. Один вес в одиночку не доказывает ничего: вечная
		// единица у walk выглядит так же, как работающий бленд, ровно до первой остановки.
		bool locoOk = walkerWalkWeight > 0.8f && walkerLyingIdleWeight > 0.8f && playerIdleWeight > 0.8f;

		Console.WriteLine($"[probe] scene: локомоушен - ходок на гребне шаг={walkerWalkWeight:0.00}, " +
			$"он же лёжа стойка={walkerLyingIdleWeight:0.00}, игрок без ввода стойка={playerIdleWeight:0.00} " +
			$"{(locoOk ? "OK" : "ВЕСА НЕ ТЕ")}");

		// Пик доказывает, что физика реально двигала кости (отклонение - разница блендованной позы
		// с анимационной, единицы модели), «после» - что реакция ЗАКОНЧИЛАСЬ: застрявший конверт на
		// глаз неотличим от прошедшего, персонаж просто «как-то странно держит спину». Круговые
		// метрики выше заодно доказывают, что толчок не сломал ходьбу.
		bool reactionOk = reactionPeak > 1.5f && reactionAfter >= 0f && reactionAfter < 0.2f;

		Console.WriteLine($"[probe] scene: хит-реакция - отклонение позы в пике {reactionPeak:0.##} " +
			$"ед. модели, после спада {reactionAfter:0.###} {(reactionOk ? "OK" : "НЕ КАЧНУЛО/ЗАСТРЯЛО")}");

		// Игрок трётся о стену посреди отрезка Walk..Run: вес обязан быть чистым аллюром, а
		// замеренная скорость - действительно посередине (иначе проверка проверяет не парковку, а
		// свободный бег, у которого вес чист и со сломанным переключением).
		bool parkedOk = parkedPurity > 0.9f && parkedSpeed > 1.2f && parkedSpeed < 2.7f;

		Console.WriteLine($"[probe] scene: парковка между аллюрами - игрок у стены {parkedSpeed:0.00} м/с, " +
			$"чистота аллюра {parkedPurity:0.00} {(parkedOk ? "ЧИСТЫЙ OK" : "ПОЛУСМЕСЬ/НЕ ТА СКОРОСТЬ")}");
	}

	/// <summary>
	/// CPU-скиннинг вершин ТОЙ ЖЕ палитрой, что ушла бы в GPU, и сравнение габарита деформированного
	/// меша с bind-габаритом. Это и есть числовой ответ на «части персонажа гигантского размера»:
	/// у здоровой палитры отношение около единицы (поза меняет размах процентов на десятки), у
	/// палитры без обратной bind-матрицы или с разлетевшимся рэгдоллом - в разы и десятки раз.
	/// </summary>
	private static unsafe float? ReportDeformedExtents(AnimationDriver animation, ModelLoader model,
		Entity entity, float time)
	{
		if (!animation.TryGetPose(entity.Id, out _, out var skin))
		{
			return null;
		}

		var world = PrefabSceneViewport.ComputeWorldMatrix(entity);
		var min = new Vector3(float.MaxValue);
		var max = new Vector3(float.MinValue);
		var bindMin = min;
		var bindMax = max;
		float worldLowY = float.MaxValue;
		bool finite = true;
		int counted = 0;

		for (int meshIndex = 0; meshIndex < model.Meshes.Count; meshIndex++)
		{
			var skinStream = meshIndex < model.MeshSkin.Count ? model.MeshSkin[meshIndex] : null;
			var mesh = model.Meshes[meshIndex];

			if (skinStream == null || mesh.VertexData == null)
			{
				continue;
			}

			int vertexCount = Math.Min(UnsafeArray.GetLength(mesh.VertexData), skinStream.Length);
			var vertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);

			for (int v = 0; v < vertexCount; v++)
			{
				var bind = vertices[v].Position;
				bindMin = Vector3.Min(bindMin, bind);
				bindMax = Vector3.Max(bindMax, bind);

				var s = skinStream[v];
				if (s.IsUnskinned)
				{
					continue;
				}

				// Та же свёртка, что в SkinningCS.hlsl: сумма weight * (skin[joint] * bindPos).
				var deformed =
					Vector3.Transform(bind, skin[s.J0]) * (s.W0 / SkinVertex.WeightScale) +
					Vector3.Transform(bind, skin[s.J1]) * (s.W1 / SkinVertex.WeightScale) +
					Vector3.Transform(bind, skin[s.J2]) * (s.W2 / SkinVertex.WeightScale) +
					Vector3.Transform(bind, skin[s.J3]) * (s.W3 / SkinVertex.WeightScale);

				finite &= float.IsFinite(deformed.X) && float.IsFinite(deformed.Y) && float.IsFinite(deformed.Z);
				min = Vector3.Min(min, deformed);
				max = Vector3.Max(max, deformed);
				worldLowY = MathF.Min(worldLowY, Vector3.Transform(deformed, world).Y);
				counted++;
			}
		}

		if (counted == 0)
		{
			return null;
		}

		float bindExtent = (bindMax - bindMin).Length();
		float deformedExtent = (max - min).Length();
		float ratio = bindExtent > 1e-6f ? deformedExtent / bindExtent : 0f;

		// Тройка - щедрый потолок: живая поза (бег, свернувшийся рэгдолл) меняет размах в разы
		// меньше, а сломанная палитра даёт десятки (кости уезжают на всю длину скелета от корня).
		Console.WriteLine($"[probe] scene: '{entity.GetComponent<EntityName>().value}' t={time:0.0} с - " +
			$"деформированный габарит {deformedExtent:0.##} (bind {bindExtent:0.##}, ×{ratio:0.##}), " +
			$"низ в мире y={worldLowY:0.###} " +
			$"{(!finite ? "NAN В ПАЛИТРЕ" : ratio < 3f ? "OK" : "ПАЛИТРА РАЗОРВАНА")}");

		return worldLowY;
	}

	private static IEnumerable<Entity> Descendants(Entity entity)
	{
		yield return entity;

		foreach (var child in entity.ChildEntities)
		{
			foreach (var descendant in Descendants(child))
			{
				yield return descendant;
			}
		}
	}
}
