using System;
using System.IO;
using System.Numerics;
using DecaEngine.Core.Assets;
using DecaEngine.Core.Entities;
using DecaEngine.Core.Prefabs;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using Friflo.Engine.ECS;

namespace DecaEngine.Editor;

/// <summary>
/// Генератор демонстрационной СЦЕНЫ (<c>--make-sample-prefab &lt;папка Assets&gt;</c>): площадка со
/// ступенями и пандусом плюс четыре персонажа, каждый под свой слой анимационного стека, и пара
/// punctual-светов. Геометрия площадки генерируется рядом (см. <see cref="SampleGroundBuilder"/>).
///
/// Префаб собирается КОДОМ и пишется штатным <see cref="PrefabAsset.SaveJson"/>, а не выкладывается
/// готовым JSON-файлом. Формат .prefab.json - это сериализация Friflo, в которой имена полей
/// компонентов и раскладка дерева не являются документированным контрактом: написанный руками файл
/// разошёлся бы с движком при первом же переименовании поля, причём молча - компонент просто
/// приехал бы с нулями.
/// </summary>
public static class SamplePrefabBuilder
{
	/// <summary>
	/// Создаёт ПРОЕКТ целиком (<c>--make-sample-project &lt;куда&gt; [имя]</c>) и кладёт в него демо-префаб.
	///
	/// Проект генерируется штатным <see cref="EditorBuilder"/> - тем же, что стоит за «File → New
	/// Project». Написать sln/csproj руками было бы быстрее, но редактор ищет проект по строгой
	/// раскладке (<c>&lt;dir&gt;/&lt;name&gt;.sln</c> + <c>&lt;dir&gt;/&lt;name&gt;/&lt;name&gt;.csproj</c>) и подтягивает ссылки на
	/// сборки движка; самодельный скелет разошёлся бы с ней при первом же изменении шаблона.
	/// </summary>
	public static void RunProject(string[] args)
	{
		string outputPath = args.Length > 1 ? args[1] : ".";
		string projectName = args.Length > 2 ? args[2] : "AnimationSample";

		if (!Microsoft.Build.Locator.MSBuildLocator.IsRegistered)
		{
			// EditorBuilder читает csproj через MSBuild - без регистрации он падает на первой же
			// правке ссылок, уже после того, как dotnet new создаст половину проекта.
			Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
		}

		Console.WriteLine($"[sample] создаю проект '{projectName}' в {Path.GetFullPath(outputPath)} ...");

		// Сцену кладёт САМ сборщик - тем же шаблоном, что и «File -> New Project» в редакторе. Иначе
		// у командной строки и у окна получились бы два разных «демо-проекта», расходящихся при
		// первой же правке одного из них.
		string slnPath = new EditorBuilder().Build(projectName, outputPath,
			ProjectTemplate.AnimationSample, Console.WriteLine);

		Console.WriteLine($"[sample] готово. Открыть: File -> Open Project -> {slnPath}");
	}

	public static void Run(string[] args) => WriteScene(args.Length > 1 ? args[1] : "Assets");

	/// <summary>Масштаб модели Khronos-семпла: она сделана в сантиметрах (габарит ~160 единиц), а
	/// сцена живёт в метрах. 0.01 даёт лису около полутора метров в длину - размер, при котором
	/// ступени в 16 см читаются как ступени, а не как бордюр.</summary>
	private const float FoxScale = 0.01f;

	/// <summary>
	/// Демо-сцена целиком: площадка со ступенями и пандусом (см. <see cref="SampleGroundBuilder"/>)
	/// и четыре персонажа, каждый под СВОЙ вопрос к анимационному стеку.
	///
	/// Четыре, а не один со всеми компонентами разом: слои стека взаимно перекрывают позу (рэгдолл
	/// её ЗАМЕНЯЕТ, foot IK правит ноги, spring bones - хвост), и персонаж, у которого включено всё,
	/// показывает только последний слой. Разложенные по отдельным сущностям, они видны одновременно
	/// и сравнимы между собой.
	/// </summary>
	public static void WriteScene(string assetsDirectory, Action<string>? log = null)
	{
		// Журнал приходит параметром: из командной строки это консоль, из «File -> New Project» -
		// консоль редактора. Прибитый Console.WriteLine означал бы, что в редакторе о неудавшейся
		// копии модели никто не узнает - а выглядит она как «префаб открылся пустым».
		log ??= Console.WriteLine;

		Directory.CreateDirectory(assetsDirectory);

		string prefabPath = Path.Combine(assetsDirectory, "Animation Sample.prefab.json");

		CopyFoxModel(assetsDirectory, log);
		WriteGround(assetsDirectory, log);
		WriteFoxAvatar(assetsDirectory, log);

		var store = new EntityStore();
		var root = store.CreateEntity();

		root.AddComponent(new EntityName("Animation Sample"));
		root.AddComponent(new Position(0f, 0f, 0f));
		root.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		root.AddComponent(new Scale3(1f, 1f, 1f));

		root.AddChild(CreateGround(store));
		root.AddChild(CreateClipFox(store));
		root.AddChild(CreateFootIkFox(store));
		root.AddChild(CreateRagdollFox(store, active: false));
		root.AddChild(CreateRagdollFox(store, active: true));
		root.AddChild(CreateCircleFox(store));
		root.AddChild(CreatePlayerFox(store));
		root.AddChild(CreateKeyLight(store));
		root.AddChild(CreateFillLight(store));

		PrefabAsset.SaveJson(root, prefabPath);

		log($"[sample] префаб записан: {Path.GetFullPath(prefabPath)}");
		VerifyRoundTrip(prefabPath, log);
	}

	private static Entity CreateGround(EntityStore store)
	{
		var ground = store.CreateEntity();

		ground.AddComponent(new EntityName("Ground"));
		ground.AddComponent(new Position(0f, 0f, 0f));
		ground.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		ground.AddComponent(new Scale3(1f, 1f, 1f));
		ground.AddComponent(new ModelRenderer { modelRef = new AssetRef("Ground.glb") });

		return ground;
	}

	/// <summary>Персонаж «чистой анимации»: клип, хвост на пружинах, доворот головы. Стоит на ровном
	/// полу намеренно - его задача показать слои, которые от рельефа не зависят вовсе.</summary>
	private static Entity CreateClipFox(EntityStore store)
	{
		var fox = CreateFox(store, "Fox Run (clip + spring + look-at)", new Vector3(0f, 0f, 3.5f));

		fox.AddComponent(new Animator
		{
			ClipName = "Run",
			Speed = 1f,
			Loop = true,
			Playing = true,
			Time = 0f,
		});

		// Хвост лисы - три кости подряд, идеальная цепочка вторичного движения. Гравитация в
		// пространстве МОДЕЛИ и в её масштабе (единицы модели, не метры), поэтому число крупное.
		fox.AddComponent(new SpringBoneComponent
		{
			Enabled = true,
			RootJoint = "b_Tail01_012",
			Length = 3,
			Stiffness = 0.08f,
			Drag = 0.2f,
			TailLength = 10f,
			Gravity = new Vector3(0f, -20f, 0f),
		});

		// Цель look-at - МИРОВАЯ, но пока считается в пространстве модели (см. AnimationDriver:
		// перевод появится вместе с поддержкой смещённых персонажей), поэтому и задана в его
		// единицах. Оси взгляда зависят от рига: у Fox кость головы смотрит вдоль +Z.
		fox.AddComponent(new LookAtComponent
		{
			Enabled = true,
			Joint = "b_Head_05",
			Target = new Vector3(0f, 40f, 120f),
			Forward = Vector3.UnitZ,
			Up = Vector3.UnitY,
			Weight = 0.6f,
		});

		return fox;
	}

	/// <summary>
	/// Персонаж foot IK - НА СТУПЕНЯХ. Это не декорация: на ровном полу солвер неотличим от своего
	/// отсутствия, и включённый компонент выглядит работающим независимо от того, работает ли он.
	/// Ноги на разной высоте - единственный случай, в котором видно и подстройку стоп, и опускание
	/// таза, и доворот по нормали.
	/// </summary>
	private static Entity CreateFootIkFox(EntityStore store)
	{
		var fox = CreateFox(store, "Fox Walk (foot IK on stairs)", new Vector3(2.6f, 0.35f, 0f));

		fox.AddComponent(new Animator
		{
			ClipName = "Walk",
			Speed = 1f,
			Loop = true,
			Playing = true,
			Time = 0f,
		});

		// Кости НЕ ЗАДАНЫ намеренно - они берутся из humanoid-разметки модели (см. окно Humanoid и
		// AnimationDriver.JointOf). Так и должен выглядеть типичный персонаж: имена костей вписывают
		// руками только там, где автоматическая разметка ошиблась, а не под каждый риг.
		//
		// Все размеры здесь - в единицах МОДЕЛИ (в тех, в которых заданы позиции джойнтов), а не в
		// метрах сцены: солвер работает в пространстве модели, и метры в этих полях означали бы
		// эффект в сто раз меньше заказанного.
		fox.AddComponent(new FootIkComponent
		{
			Enabled = true,
			AnkleHeight = 1f,
			MaxPelvisDrop = 25f,
			Smoothing = 12f,
			Weight = 1f,
			AlignToNormal = true,

			// Четвероногое: передние ноги из arm-слотов разметки + наклон корпуса по рельефу.
			FrontLegs = true,
		});

		return fox;
	}

	/// <summary>
	/// Рэгдолл над пандусом: тряпичный и active (с сервоприводами). Оба висят НАД поверхностью и
	/// падают при первом же кадре симуляции - лежащий рэгдолл не отличим от неработающего, а
	/// падающий показывает и суставы, и пределы, и то, гасится ли энергия.
	/// </summary>
	private static Entity CreateRagdollFox(EntityStore store, bool active)
	{
		var fox = CreateFox(store,
			active ? "Fox Active Ragdoll (servo)" : "Fox Ragdoll (limp)",
			new Vector3(-3.2f, 1.8f, active ? 2.2f : -2.2f));

		// Клип продолжает играть и в рэгдолле: в тряпичном режиме он не виден (позу задают тела), а
		// в active - именно он служит целью сервоприводам, и без него персонаж «держится» за
		// bind-позу, что выглядит как судорога.
		fox.AddComponent(new Animator
		{
			ClipName = "Survey",
			Speed = 1f,
			Loop = true,
			Playing = true,
			Time = 0f,
		});

		fox.AddComponent(new RagdollComponent
		{
			Enabled = true,
			Physical = true,
			ServoStrength = active ? 60f : 0f,

			// Корень не задан - берётся таз из humanoid-разметки, как и кости foot IK выше.
			MaxDepth = 4,

			// Ноль = радиусы капсул ИЗ МЕША, по толщине каждой части тела (см.
			// AnimationDriver.MeasureBoneRadii). Единый авторский радиус здесь был бы неверен по
			// построению: туловище лисы втрое толще лапы, и с «толщиной лапы» на всех костях персонаж
			// лежал наполовину в полу.
			BoneRadius = 0f,
			TotalMass = 12f,
		});

		return fox;
	}

	/// <summary>Центр круга, по которому ходит лиса, и его радиус. Место выбрано на РОВНОЙ части
	/// площадки: лестница занимает x&gt;=1.5, пандус x&lt;=-1.5, и оба - в полосе |z|&lt;=2, поэтому круг
	/// целиком уходит за z=-2.3. Пока движение идёт прямым заданием трансформа, пересечение с
	/// геометрией выглядело бы как «персонаж лезет сквозь ступени» и мешало бы смотреть на само
	/// движение.</summary>
	private static readonly Vector3 CircleCenter = new(0f, 0f, -4.3f);

	private const float CircleRadius = 2f;

	/// <summary>Метров в секунду. Пока это просто «похоже на шаг»: связка с клипом (скорость шага
	/// против скорости тела) - отдельный шаг работы, и подгонять её на глаз здесь значило бы
	/// закрепить в сцене случайное число вместо измеренного.</summary>
	private const float CircleSpeed = 1f;

	/// <summary>
	/// Персонаж, идущий ПО КРУГУ, - первый геймплейный скрипт в сцене (см.
	/// <see cref="CircleMoveComponent"/>). В отличие от остальных четырёх, он не показывает слой
	/// анимационного стека: он показывает, что сцена вообще ЖИВЁТ по кнопке Play, а не только
	/// проигрывает клипы.
	///
	/// Стартовая позиция и поворот выставлены СОГЛАСОВАННО с нулевой фазой (точка на +X от центра,
	/// касательная вдоль +Z). Иначе на первом же кадре Play персонаж прыгал бы на круг - а прыжок в
	/// момент старта неотличим от «скрипт сломал трансформ».
	/// </summary>
	private static Entity CreateCircleFox(EntityStore store)
	{
		var fox = CreateFox(store, "Fox Circle (gameplay script)",
			CircleCenter + new Vector3(CircleRadius, 0f, 0f));

		// Тот же поворот, который система задаст на первом кадре при нулевой фазе: касательная там
		// вдоль +Z, а морда модели смотрит в -Z (см. FoxForward), то есть разворот - ровно на 180°.
		// Считается формулой, а не вписывается числом: разойдясь с системой, число дало бы рывок в
		// момент нажатия Play, который выглядит как ошибка скрипта, а не как ошибка сцены.
		var facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY,
			MathF.Atan2(0f, 1f) - MathF.Atan2(FoxForward.X, FoxForward.Z));
		fox.AddComponent(new Rotation(facing.X, facing.Y, facing.Z, facing.W));

		// Animator остаётся ФОЛЛБЕКОМ локомоушена (нет ozz, не нашлись клипы) - см. LocomotionComponent.
		fox.AddComponent(new Animator
		{
			ClipName = "Walk",
			Speed = 1f,
			Loop = true,
			Playing = true,
			Time = 0f,
		});

		// Клип подстраивается под ЗАМЕРЕННУЮ скорость тела: на подъёме и в столкновениях лиса
		// замедляется, и шаг замедляется вместе с ней, а лёжа (цикл падения) вес уходит в стойку.
		fox.AddComponent(new LocomotionComponent
		{
			IdleClip = "Survey",
			WalkSpeed = 1f,
			RunSpeed = 3f,
		});

		// Частичный бленд (ozz partial_blend): шея с головой играют осмотр ПОВЕРХ цикла шага -
		// лиса идёт и оглядывается. Корень - шея ИМЕНЕМ, а не слотом по умолчанию: слот «грудь»
		// у четвероногого несёт передние лапы, и осмотр на груди остановил бы их шаг.
		fox.AddComponent(new OverlayClipComponent
		{
			Enabled = true,
			ClipName = "Survey",
			RootJoint = "b_Neck_04",
			Weight = 1f,
			Speed = 1f,
			Loop = true,
		});

		fox.AddComponent(new CircleMoveComponent
		{
			Enabled = true,
			Center = CircleCenter,
			Radius = CircleRadius,
			Speed = CircleSpeed,
			Angle = 0f,
			FaceMotion = true,
			Forward = FoxForward,

			// Предел доворота: на круге он почти не работает (касательная меняется плавно), но
			// после подъёма из рэгдолла корпус доворачивается к касательной, а не прыгает на неё.
			TurnSpeed = 360f,
		});

		// Тело - ОТДЕЛЬНЫМ компонентом: габарит принадлежит персонажу, а не кругу, по которому он
		// ходит. Размеры в МЕТРАХ сцены, а не в единицах модели: тело живёт в мире, а не в
		// пространстве скелета. Ориентир измерен отчётом по клипам (DECA_PROBE_ANIMREPORT): таз лисы
		// держится на y≈41 единиц модели, то есть на 0.41 м при масштабе 0.01. Капсула заметно ниже
		// холки: у четвероногого «тело» - это туловище.
		fox.AddComponent(new CharacterBodyComponent
		{
			Radius = 0.18f,
			Height = 0.5f,
			Mass = 12f,
		});

		// Подстройка лап под поверхность - у ИДУЩЕГО персонажа, а не только у демонстрационного на
		// ступенях: круг проходит через кочку (см. SampleGroundBuilder.AddMound), и без foot IK лапы
		// на её склоне висят в воздухе с одной стороны и уходят в грунт с другой. Настройки те же,
		// что у лисы на лестнице, - модель одна, и разъехавшиеся числа означали бы, что один из двух
		// персонажей настроен неверно, причём неизвестно какой.
		fox.AddComponent(new FootIkComponent
		{
			Enabled = true,
			AnkleHeight = 1f,
			MaxPelvisDrop = 25f,
			Smoothing = 12f,
			Weight = 1f,
			AlignToNormal = true,

			// Четвероногое: передние ноги из arm-слотов разметки + наклон корпуса по рельефу.
			FrontLegs = true,
		});

		// Рэгдолл ВЫКЛЮЧЕН и не физический: его включает и роняет цикл падения (см. ниже). Собирать
		// его заранее незачем - это два десятка тел и связей на персонажа, который большую часть
		// времени просто идёт.
		fox.AddComponent(new RagdollComponent
		{
			Enabled = false,
			Physical = false,
			MaxDepth = 4,
			BoneRadius = 0f,
			TotalMass = 12f,
		});

		// Падать раз в шесть секунд: круг длиной 12.6 м проходится за 12.6 с, то есть падение
		// случается дважды за оборот - достаточно часто, чтобы увидеть его сразу после Play, и
		// достаточно редко, чтобы успеть разглядеть саму ходьбу.
		fox.AddComponent(new FallRecoverComponent
		{
			// Авторские клипы подъёма (добавлены в Fox.glb поверх ориджинала): выбор по позе
			// лёжки, стык - вливанием снимка в начало клипа за GetUpDuration (0.7 - перетекание
			// в сидячую стартовую позу нарочно неторопливое).
			GetUpBackClip = "GetUp_FromBack",
			GetUpBellyClip = "GetUp_FromBelly",
			GetUpDuration = 0.7f,

			FallEvery = 6f,
			MinFallTime = 1.2f,
			SettleTimeout = 4f,
			SettleSpeed = 0.05f,
		});

		return fox;
	}

	/// <summary>
	/// Персонаж под управлением ИГРОКА (см. <see cref="PlayerMoveComponent"/>): в Play WASD/стрелки
	/// его двигают, Shift - бег. Стоит рядом с кругом на ровном полу - чтобы первое же нажатие W было
	/// видно, а не искалось по сцене. Полный современный набор: капсула, локомоушен-бленд по
	/// скорости, foot IK - тот же, что у лисы круга, только рулит человек, а не скрипт.
	/// </summary>
	private static Entity CreatePlayerFox(EntityStore store)
	{
		var fox = CreateFox(store, "Fox Player (WASD in Play)", new Vector3(3.5f, 0f, -4.3f));

		// Тот же разворот, что у лисы круга: до первого нажатия персонаж стоит мордой в +Z мира,
		// а не задом - у Khronos Fox морда в -Z (см. FoxForward).
		var facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY,
			MathF.Atan2(0f, 1f) - MathF.Atan2(FoxForward.X, FoxForward.Z));
		fox.AddComponent(new Rotation(facing.X, facing.Y, facing.Z, facing.W));

		// Аддитивный слой: дельта Survey (поводит головой и ушами) поверх любого аллюра - в
		// отличие от Overlay Clip лисы круга, ходьба на этих костях НЕ стирается, дельта лишь
		// докручивает их. Полвеса - лёгкое шевеление, а не полный размах осмотра.
		fox.AddComponent(new AdditiveClipComponent
		{
			Enabled = true,
			ClipName = "Survey",
			Weight = 0.5f,
			Speed = 1f,
			Loop = true,
		});

		fox.AddComponent(new Animator
		{
			ClipName = "Walk",
			Speed = 1f,
			Loop = true,
			Playing = true,
			Time = 0f,
		});

		fox.AddComponent(new LocomotionComponent
		{
			IdleClip = "Survey",
			WalkSpeed = 1f,
			RunSpeed = 3f,
		});

		fox.AddComponent(new CharacterBodyComponent
		{
			Radius = 0.18f,
			Height = 0.5f,
			Mass = 12f,
		});

		fox.AddComponent(new PlayerMoveComponent
		{
			WalkSpeed = 1f,
			RunSpeed = 3f,
			FaceMotion = true,
			Forward = FoxForward,
		});

		fox.AddComponent(new FootIkComponent
		{
			Enabled = true,
			AnkleHeight = 1f,
			MaxPelvisDrop = 25f,
			Smoothing = 12f,
			Weight = 1f,
			AlignToNormal = true,

			// Четвероногое: передние ноги из arm-слотов разметки + наклон корпуса по рельефу.
			FrontLegs = true,
		});

		// Рэгдолл выключен - он нужен ХИТ-РЕАКЦИИ (таран другого персонажа): реакция собирает тела
		// на время толчка и сносит после, а без компонента персонажу нечем реагировать вовсе.
		fox.AddComponent(new RagdollComponent
		{
			Enabled = false,
			Physical = false,
			MaxDepth = 4,
			BoneRadius = 0f,
			TotalMass = 12f,
		});

		return fox;
	}

	/// <summary>Куда смотрит морда Khronos Fox в пространстве модели. НЕ +Z: договорённость движка
	/// («вперёд = поворот × +Z») - это про сущность, а не про содержимое .glb, и модель ей не обязана.
	/// Проверяется только глазами - на неподвижном кадре персонаж, идущий задом наперёд, неотличим от
	/// идущего вперёд, и никакая числовая метрика разницы не видит.</summary>
	private static readonly Vector3 FoxForward = -Vector3.UnitZ;

	private static Entity CreateFox(EntityStore store, string name, Vector3 position)
	{
		var fox = store.CreateEntity();

		fox.AddComponent(new EntityName(name));
		fox.AddComponent(new Position(position.X, position.Y, position.Z));
		fox.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		fox.AddComponent(new Scale3(FoxScale, FoxScale, FoxScale));

		// Путь ОТНОСИТЕЛЬНО папки Assets: именно так его резолвит сцена (см.
		// PrefabSceneViewport.ResolveAssetPath - поиск папки "Assets" вверх от файла префаба).
		fox.AddComponent(new ModelRenderer { modelRef = new AssetRef("Fox.glb") });

		return fox;
	}

	/// <summary>Ключевой spot сверху - под тени punctual-светов и объёмный свет. Направление света
	/// берётся из ПОВОРОТА сущности (forward = поворот * +Z, см. CullingAndRenderSystem), поэтому
	/// «вниз» - это поворот на 90° вокруг X, а не отдельное поле.</summary>
	private static Entity CreateKeyLight(EntityStore store)
	{
		var light = store.CreateEntity();
		var down = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI * 0.5f);

		light.AddComponent(new EntityName("Key Spot"));
		light.AddComponent(new Position(1.5f, 4.5f, -1.5f));
		light.AddComponent(new Rotation(down.X, down.Y, down.Z, down.W));
		light.AddComponent(new Scale3(1f, 1f, 1f));

		// Интенсивность в единицах ДВИЖКА, а не в люменах: масштаб задан тем, что уже есть в
		// движке (FullLoopProbe ставит punctual-светам 5 и 8, EditorManager - 1). Первая версия
		// этой сцены получила «физичные» 120 - и сцена превращалась в мешанину цветных пятен:
		// пересвеченные стены накачивали probe GI, а автоэкспозиция добивала кадр до белого.
		light.AddComponent(new LightComponent
		{
			Type = LightType.Spot,
			Color = new Vector3(1f, 0.93f, 0.82f),
			Intensity = 8f,
			Range = 14f,
			SpotAngle = 70f,
			InnerSpotAngle = 40f,
			ShadowStrength = 1f,

			// Радиус светящегося тела - от него PCSS выводит ширину полутени. 10 см: полутень видна
			// на ступенях, но тень не расплывается в пятно.
			SourceRadius = 0.1f,
		});

		return light;
	}

	/// <summary>Заполняющий point у красной стены - под цветовую подкраску probe GI: у стены он
	/// добавляет к отражённому свету собственный, и разница между «GI работает» и «GI выключен»
	/// становится видна не только по яркости, но и по цвету.</summary>
	private static Entity CreateFillLight(EntityStore store)
	{
		var light = store.CreateEntity();

		light.AddComponent(new EntityName("Fill Point"));
		light.AddComponent(new Position(-5.5f, 1.8f, 0f));
		light.AddComponent(new Rotation(0f, 0f, 0f, 1f));
		light.AddComponent(new Scale3(1f, 1f, 1f));

		light.AddComponent(new LightComponent
		{
			Type = LightType.Point,
			Color = new Vector3(0.75f, 0.85f, 1f),
			Intensity = 3f,
			Range = 8f,
			ShadowStrength = 1f,
			SourceRadius = 0.15f,
		});

		return light;
	}

	/// <summary>Генерирует геометрию площадки рядом с префабом. Ошибка здесь НЕ должна ронять
	/// генерацию префаба целиком: без пола сцена остаётся осмысленной (персонажи видны, клипы
	/// играют), а вот без префаба смотреть не на что вовсе.</summary>
	/// <summary>
	/// Кладёт рядом с моделью её humanoid-разметку (см. <see cref="HumanoidAvatar"/>).
	///
	/// Разметка СОХРАНЯЕТСЯ файлом, а не оставляется автоматической, и это не удобство: без файла
	/// сцена работает на догадке автомата, которая пересчитывается при каждой загрузке и молча
	/// меняется вместе с кодом разметки. Демо-сцена должна показывать штатный путь целиком - от
	/// файла аватара до foot IK и рэгдолла, которые берут кости из него (в префабе имена костей у
	/// них не заданы намеренно).
	///
	/// Модель читается ЛЕГКОВЕСНО, только скелет: полная загрузка тянет за собой GPU, которого у
	/// генератора префаба нет вовсе.
	/// </summary>
	private static void WriteFoxAvatar(string assetsDirectory, Action<string> log)
	{
		string modelPath = Path.Combine(assetsDirectory, "Fox.glb");

		if (!File.Exists(modelPath))
		{
			return;
		}

		try
		{
			var skeleton = SkinningImport.BuildSkeleton(
				SharpGLTF.Schema2.ModelRoot.Load(modelPath), out _);
			if (skeleton == null || skeleton.JointCount == 0)
			{
				log("[sample] ВНИМАНИЕ: скелет Fox.glb не прочитан - аватар не записан");
				return;
			}

			var avatar = HumanoidAutoMap.Build(skeleton);
			var issues = HumanoidValidation.Validate(avatar, skeleton);

			// Референсная поза берётся из bind: у Fox модель экспортирована в своей стойке, и она же
			// служит точкой отсчёта. Оценка печатается ЧЕСТНО - лиса четвероногая, и её «руки»
			// (передние лапы) смотрят вниз, а не в стороны; это не ошибка разметки, а свойство
			// модели, и демо-сцена не должна делать вид, будто у неё идеальная T-поза.
			HumanoidReferencePose.CaptureFromBind(avatar, skeleton);
			var pose = HumanoidReferencePose.Evaluate(avatar, skeleton);

			HumanoidAvatarAsset.Save(avatar, modelPath);

			log($"[sample] аватар записан: {HumanoidAvatarAsset.PathFor(modelPath)} " +
				$"(костей {skeleton.JointCount}, проблем разметки {issues.Count}, " +
				$"отклонение от T-позы до {pose.Worst:0.#}°)");
		}
		catch (Exception ex)
		{
			// Без аватара сцена остаётся рабочей - разметка просто станет автоматической.
			log($"[sample] ВНИМАНИЕ: аватар не записан ({ex.Message}) - разметка будет автоматической");
		}
	}

	private static void WriteGround(string assetsDirectory, Action<string> log)
	{
		string path = Path.Combine(assetsDirectory, "Ground.glb");

		try
		{
			SampleGroundBuilder.Write(path);
			log($"[sample] площадка сгенерирована: {Path.GetFullPath(path)}");
		}
		catch (Exception ex)
		{
			log($"[sample] ВНИМАНИЕ: площадка не сгенерирована ({ex.Message}) - " +
				"персонажи окажутся в пустоте, foot IK и рэгдолл проверять будет не на чем");
		}
	}

	/// <summary>
	/// Читает записанный префаб обратно и проверяет, что компоненты доехали. Проверка не
	/// формальная: имена полей в .prefab.json - это сериализация Friflo, и компонент, который она не
	/// восстановила, приезжает НЕ с ошибкой, а с нулевыми полями - в редакторе это выглядит как
	/// «префаб открылся, но анимация не играет».
	/// </summary>
	private static void VerifyRoundTrip(string prefabPath, Action<string> log)
	{
		var store = new EntityStore();
		var loaded = PrefabAsset.Instantiate(store, prefabPath);

		int children = 0;
		int models = 0;
		int animators = 0;
		int springs = 0;
		int lookAts = 0;
		int footIk = 0;
		int ragdolls = 0;
		int lights = 0;
		int circles = 0;
		int bodies = 0;
		int falls = 0;
		int locomotions = 0;
		int players = 0;

		foreach (var child in loaded.ChildEntities)
		{
			children++;

			models += child.HasComponent<ModelRenderer>() ? 1 : 0;
			animators += child.HasComponent<Animator>() ? 1 : 0;
			springs += child.HasComponent<SpringBoneComponent>() ? 1 : 0;
			lookAts += child.HasComponent<LookAtComponent>() ? 1 : 0;
			footIk += child.HasComponent<FootIkComponent>() ? 1 : 0;
			ragdolls += child.HasComponent<RagdollComponent>() ? 1 : 0;
			lights += child.HasComponent<LightComponent>() ? 1 : 0;
			circles += child.HasComponent<CircleMoveComponent>() ? 1 : 0;
			bodies += child.HasComponent<CharacterBodyComponent>() ? 1 : 0;
			falls += child.HasComponent<FallRecoverComponent>() ? 1 : 0;
			locomotions += child.HasComponent<LocomotionComponent>() ? 1 : 0;
			players += child.HasComponent<PlayerMoveComponent>() ? 1 : 0;
		}

		// Ожидания перечислены ЧИСЛАМИ, а не «есть/нет»: потерянный при сериализации компонент у
		// одного из четырёх персонажей выглядел бы как «есть» ровно до тех пор, пока сцену не
		// откроют и не удивятся, почему рэгдолл падает только один.
		bool ok = children == 9 && models == 7 && animators == 6 && springs == 1 && lookAts == 1 &&
			footIk == 3 && ragdolls == 4 && lights == 2 && circles == 1 && bodies == 2 && falls == 1 &&
			locomotions == 2 && players == 1;

		log($"[sample] round-trip: детей={children}/9, ModelRenderer={models}/7, " +
			$"Animator={animators}/6, SpringBone={springs}/1, LookAt={lookAts}/1, " +
			$"FootIk={footIk}/3, Ragdoll={ragdolls}/4, Light={lights}/2, CircleMove={circles}/1, " +
			$"CharacterBody={bodies}/2, FallRecover={falls}/1, Locomotion={locomotions}/2, " +
			$"PlayerMove={players}/1 {(ok ? "OK" : "ПОТЕРЯНЫ КОМПОНЕНТЫ")}");
	}

	/// <summary>Кладёт саму модель рядом с префабом. Без неё ModelRenderer не найдёт файл, и в
	/// сцене окажется пустая сущность - выглядит как «префаб сломан», хотя сломана лишь ссылка.</summary>
	private static void CopyFoxModel(string assetsDirectory, Action<string> log)
	{
		string source = Path.Combine(AppContext.BaseDirectory, "EditorAssets", "models", "Fox.glb");
		string destination = Path.Combine(assetsDirectory, "Fox.glb");

		if (!File.Exists(source))
		{
			log($"[sample] ВНИМАНИЕ: {source} не найден - положите Fox.glb в {assetsDirectory} вручную");
			return;
		}

		File.Copy(source, destination, overwrite: true);
	}
}
