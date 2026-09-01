using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Physics;
using Friflo.Engine.ECS;

// В Friflo есть свой Transform-компонент, а поза скелета оперирует TRS движка - без явного алиаса
// имя разрешается неоднозначно.
using Transform = DecaEngine.Graphics.Transform;

namespace DecaEngine.Editor;

/// <summary>
/// Мост между авторскими компонентами (<see cref="Animator"/>, <see cref="SpringBoneComponent"/>,
/// <see cref="LookAtComponent"/>, <see cref="FootIkComponent"/>, <see cref="RagdollComponent"/>) и
/// рантаймом анимации. Держит для каждой сущности со скиннед-моделью её позу, проигрыватель,
/// настроенные цепочки, ноги и рэгдолл, читает компоненты каждый кадр и заливает готовую палитру в
/// GPU-проход скиннинга.
///
/// Состояние ЗДЕСЬ, а не в компонентах, - см. шапку AnimationComponents.cs: поза и нативные хендлы
/// ozz не переживают копирование, которое ECS-хранилище делает при смене архетипа сущности.
///
/// Одна сущность префаба со скиннед-моделью порождает НЕСКОЛЬКО инстансов в батч-рендерере (по
/// одному на меш модели), и у каждого свой участок палитры. Поза при этом ОДНА: это один персонаж, и
/// считать её на меш значило бы гонять один и тот же скелет по нескольку раз за кадр.
/// </summary>
public sealed class AnimationDriver : IDisposable
{
	private sealed class Character : IDisposable
	{
		public ModelLoader Model = null!;
		public PreparedSkeleton Skeleton = null!;

		public OzzSkeleton? Ozz;
		public OzzPose? Pose;
		public readonly Dictionary<PreparedAnimation, OzzClip?> Clips = new();

		public SkeletonPose Managed = null!;
		public readonly AnimationPlayer Player = new();

		public Transform[] Locals = [];
		public Matrix4x4[] Models = [];

		/// <summary>Трансформ сущности этого кадра: поза считается в пространстве МОДЕЛИ, а физика
		/// живёт в мире, и переход между ними нужен обеим сторонам - и лучу foot IK, и рэгдоллу.</summary>
		public Matrix4x4 ModelToWorld = Matrix4x4.Identity;

		/// <summary>Характерный размер скелета - средняя длина кости. Им масштабируется ВЕСЬ дебаг:
		/// у лисы габарит ~160 единиц, у метрового персонажа - 1.8, и любая константа в единицах мира
		/// на одном из них выглядит либо точкой, либо забором во весь экран.</summary>
		public float Scale = 1f;

		/// <summary>Участки палитры всех инстансов этого персонажа.</summary>
		public readonly List<int> Palettes = new();

		public readonly List<SpringBoneChain> Chains = new();

		/// <summary>Слепок настроек цепочек, по которому определяется, что их нужно пересобрать.
		/// Пересобирать каждый кадр нельзя: пересборка роняет накопленную инерцию, и хвост переставал
		/// бы колыхаться вовсе - он бесконечно начинал бы движение заново.</summary>
		public SpringBoneComponent ChainSource;
		public bool ChainsBuilt;

		public string AppliedClip = string.Empty;

		// --- Локомоушен ----------------------------------------------------------------------------

		/// <summary>Scratch-позы под два слоя бленда. Свои, а не переиспользование <see cref="Pose"/>:
		/// бленд пишет В неё, и семплировать слой в неё же значило бы затирать вход выходом.</summary>
		public OzzPose? LocoPoseA;
		public OzzPose? LocoPoseB;

		public PreparedAnimation? LocoIdle;
		public PreparedAnimation? LocoWalk;
		public PreparedAnimation? LocoRun;
		public string LocoClipsKey = string.Empty;

		/// <summary>Нормированная фаза цикла шага, 0..1 - ОБЩАЯ для Walk и Run. Смешивать клипы по
		/// своим секундам нельзя: у шага и бега разная длительность цикла, и на полпути бленда левая
		/// нога одного клипа встречается с правой другого.</summary>
		public float LocoPhase;

		/// <summary>Фазовые сдвиги клипов до ОБЩЕГО события аллюра (нижняя точка задней левой лапы).
		/// Общей фазы самой по себе мало: авторские клипы начинают цикл с произвольного момента, у
		/// Walk и Run лисы фаза 0 приходится на разные части аллюра, и в середине бленда задние ноги
		/// галопа (поджатые под корпус) складывались с передними ногами шага - персонаж «ходил
		/// ногами в собственное тело» всякий раз, когда скорость застревала между Walk и Run
		/// (например, капсула трётся о стену).</summary>
		public float LocoWalkPhaseOffset;
		public float LocoRunPhaseOffset;
		public bool LocoOffsetsValid;

		/// <summary>Аллюр - ДИСКРЕТНОЕ состояние с гистерезисом, бленд шаг↔бег - кроссфейд по
		/// ВРЕМЕНИ, а не вес по скорости. Выравнивание по событию одной лапы делает когерентными
		/// задние ноги, но у шага и галопа разные фазовые соотношения МЕЖДУ передними и задними -
		/// единым сдвигом это не лечится, они разные аллюры. Персонаж, чья скорость паркуется между
		/// WalkSpeed и RunSpeed (трётся о стену на бегу), обязан стоять в ЧИСТОМ аллюре с
		/// масштабированным темпом, а не жить в вечной полусмеси с передними ногами в корпусе.</summary>
		public bool LocoRunGait;
		public float LocoGaitBlend;

		/// <summary>ПРИРОДНАЯ скорость шага клипа - скорость опорной лапы в пространстве модели на
		/// авторском темпе (ед. модели/с), замеренная из самого клипа. Темп подгоняется ПО НЕЙ, а не
		/// по авторским WalkSpeed/RunSpeed: те - пороги переключения аллюра, и когда их принимали за
		/// природную скорость, галоп (реальная скорость которого у Khronos Fox другая) ехал лапами
		/// по земле, а foot locking, честно прибив лапу к миру, каждый такт утаскивал её под тело до
		/// предела вытяжения - «нога в теле» на ЧИСТОМ беге.</summary>
		public float LocoWalkStride;
		public float LocoRunStride;

		public float LocoIdleTime;

		/// <summary>Сглаженная замеренная скорость, м/с.</summary>
		public float LocoSpeed;

		public Vector3 LocoPrevWorld;
		public bool LocoHasPrev;

		/// <summary>Снимок кадра для дебага: ведёт ли позу локомоушен и с какими весами.</summary>
		public bool LocoActive;
		public float LocoIdleWeight;
		public float LocoWalkWeight;
		public float LocoRunWeight;

		/// <summary>Humanoid-разметка рига (см. <see cref="HumanoidAvatar"/>); null - модель не
		/// размечена. Из неё берутся кости, которые автор не задал в компонентах руками: смысл
		/// разметки в том, чтобы foot IK и рэгдолл настраивались ОДИН раз на все модели, а не
		/// именами костей под каждый риг.</summary>
		public HumanoidAvatar? Avatar;

		// --- Root motion ---------------------------------------------------------------------------

		/// <summary>Клип, для которого резолвлена корневая кость движения, и сама кость: корень -
		/// самый верхний предок таза разметки (авторское движение живёт на корневом узле рига).</summary>
		public PreparedAnimation? MotionClip;
		public int MotionJoint = -1;

		// --- Частичный бленд (OverlayClipComponent) ------------------------------------------------

		public OzzPose? OverlayPose;
		public PreparedAnimation? OverlayClip;
		public string OverlayClipName = string.Empty;
		public float OverlayTime;

		/// <summary>Посуставные веса слоёв: base = 1 вне поддерева и 1-w внутри, overlay = w внутри
		/// и 0 вне - сумма на каждом суставе единица, и rest-поза ozz не подмешивается никогда.</summary>
		public float[]? OverlayMaskBase;
		public float[]? OverlayMaskLayer;
		public int OverlayRoot = -1;
		public float OverlayWeight = -1f;

		// --- Аддитивный слой (AdditiveClipComponent) -----------------------------------------------

		public OzzPose? AdditivePose;
		public PreparedAnimation? AdditiveSource;
		public PreparedAnimation? AdditiveDelta;
		public string AdditiveClipName = string.Empty;
		public float AdditiveTime;

		// --- Foot IK -------------------------------------------------------------------------------

		public readonly List<FootIkLeg> Legs = new();
		public readonly FootIkSettings IkSettings = new();
		public FootIkComponent LegSource;
		public bool LegsBuilt;

		/// <summary>Применился ли IK в этом кадре. Не то же самое, что «компонент включён»: без
		/// нативного ozz или без физики солвер штатно возвращает false, и дебаг обязан показывать
		/// именно фактическое положение дел.</summary>
		public bool IkApplied;

		// --- Рэгдолл -------------------------------------------------------------------------------

		public Ragdoll? Ragdoll;
		public RagdollComponent RagdollSource;
		public bool RagdollBuilt;

		/// <summary>Масштаб трансформа сущности в момент сборки рэгдолла. Тела и связи Bepu строятся
		/// в МИРЕ и запекают в себя размеры капсул и точки крепления - изменение Scale3 сущности их
		/// не трогает, и персонаж, уменьшенный вдвое, остался бы в рэгдолле прежнего размера. Снаружи
		/// это выглядит как «физика отвязалась от модели», а не как «забыли пересобрать».</summary>
		public float RagdollBuildScale;

		/// <summary>Поза анимации в МИРЕ - цель для тел рэгдолла.</summary>
		public Matrix4x4[] JointWorld = [];

		/// <summary>Поза, прочитанная ИЗ тел. Отдельный массив, а не тот же: <see cref="JointWorld"/>
		/// в этот момент ещё нужен как цель сервоприводов.</summary>
		public Matrix4x4[] RagdollWorld = [];

		/// <summary>Джойнты, позу которых задаёт физика. Остальные пересчитываются от них по
		/// иерархии - см. ReadRagdollPose.</summary>
		public bool[] RagdollOwned = [];

		// --- Хит-реакция -----------------------------------------------------------------------------

		/// <summary>Оставшаяся реакция: 0 - реакции нет. Тела временно живут в физике с сильными
		/// сервоприводами, и их поза подмешивается к анимации ПО МАСКЕ (корпус - да, ноги - нет).</summary>
		public float ReactionElapsed;
		public float ReactionDuration;
		public float ReactionStrength;

		/// <summary>Толчок, ожидающий первого кадра с телами: реакция может стартовать до того, как
		/// рэгдолл собран (у идущего персонажа тел нет вовсе).</summary>
		public Vector3 ReactionImpulse;
		public bool ReactionImpulsePending;

		/// <summary>Вес реакции на джойнт: корпус/шея/голова/хвост 1, таз приглушён, ноги 0 -
		/// персонаж качается, продолжая идти. Строится по humanoid-разметке один раз.</summary>
		public float[] ReactionMask = [];
		public bool ReactionMaskBuilt;

		/// <summary>Анимационная поза кадра ДО подмешивания физики - вторая половина бленда.</summary>
		public Matrix4x4[] ReactionAnimated = [];

		/// <summary>Снимок кадра для дебага и пробника: текущий вес конверта и максимальное
		/// отклонение позы от анимации (единицы модели). Отклонение - прямое доказательство, что
		/// физика реально двигает кости, а не только конверт тикает.</summary>
		public float ReactionWeight;
		public float ReactionDeviation;

		// --- Снимок для дебага ---------------------------------------------------------------------
		//
		// Дебаг рисуется В КОНЦЕ кадрового шага, по итоговой позе, а данные для него появляются на
		// промежуточных стадиях (цель look-at, состав цепочки). Складывать их сюда дешевле, чем
		// перечитывать компоненты второй раз, и честнее: показывается ровно то, что применилось.

		public bool HasLookAt;
		public Vector3 LookAtTarget;
		public int LookAtJoint = -1;

		// --- Подъём из рэгдолла ----------------------------------------------------------------------

		/// <summary>Поза, из которой персонаж встаёт (снимок в момент начала подъёма). null - подъём
		/// ни разу не начинался.</summary>
		public Transform[]? RecoveryFrom;

		/// <summary>Клип подъёма, ведущий позу на время восстановления; null - процедурный морф.</summary>
		public PreparedAnimation? GetUpClip;

		/// <summary>Окно вливания снимка лёжки. У морфа равно всей длительности (прежнее поведение),
		/// у клипа - короткое: его первый кадр авторски лежачий, и долгий морф разбавлял бы клип.</summary>
		public float RecoveryBlendSeconds;

		public float RecoveryDuration;
		public float RecoveryElapsed;

		/// <summary>Шаг времени этого кадра. Переход позы идёт последней стадией, куда deltaSeconds
		/// уже не передаётся: тащить его сквозь пять вызовов ради одного сложения незачем.</summary>
		public float LastDelta;

		public void Dispose()
		{
			Ragdoll?.Destroy();
			Ragdoll = null;

			foreach (var clip in Clips.Values)
			{
				clip?.Dispose();
			}

			Clips.Clear();
			Pose?.Dispose();
			LocoPoseA?.Dispose();
			LocoPoseB?.Dispose();
			Ozz?.Dispose();
		}
	}

	private readonly DiligentSkinningPass _skinning;
	private readonly Dictionary<int, Character> _characters = new();

	public AnimationDriver(DiligentSkinningPass skinning) => _skinning = skinning;

	public int CharacterCount => _characters.Count;

	/// <summary>
	/// Палитра персонажа - для проб. ЖИВЫЕ массивы, не копия: проба читает их сразу после
	/// <see cref="Update"/>, ровно то, что ушло бы в GPU этим кадром.
	///
	/// Нужен, потому что снаружи палитру видно только КАРТИНКОЙ: разорванный персонаж на скриншоте -
	/// это разъехавшиеся skin-матрицы, а какие именно и насколько - по пикселям не скажешь. Проба
	/// CPU-скиннит вершины этой палитрой и меряет габарит числом.
	/// </summary>
	public bool TryGetPose(int entityId, out Matrix4x4[] modelMatrices, out Matrix4x4[] skinMatrices)
	{
		if (_characters.TryGetValue(entityId, out var character))
		{
			modelMatrices = character.Managed.ModelMatrices;
			skinMatrices = character.Managed.SkinMatrices;
			return true;
		}

		modelMatrices = [];
		skinMatrices = [];
		return false;
	}

	/// <summary>Мир физики сцены. null - foot IK и рэгдолл штатно выключены: обоим нужна геометрия
	/// мира, и подставлять вместо неё плоскость y=0 значило бы показывать персонажа стоящим на
	/// поверхности, которой в сцене нет.</summary>
	public ScenePhysics? Physics { get; set; }

	/// <summary>Приёмник дебаг-геометрии. null или выключенный - ни одна стадия ничего не рисует.</summary>
	public DebugDraw? Debug { get; set; }

	public AnimationDebugOptions DebugOptions { get; set; } = new();

	/// <summary>Кость, подсвеченная окном Humanoid. Рисуется НЕЗАВИСИМО от галочек дебага: она
	/// отвечает на вопрос «какая это кость», который задают как раз тогда, когда никакие слои ещё
	/// не включены.</summary>
	public string HighlightJoint { get; set; } = string.Empty;

	/// <summary>Сколько персонажей в этом кадре реально считались рэгдоллом - для окна дебага.</summary>
	public int ActiveRagdollCount { get; private set; }

	/// <summary>
	/// Заводит (или дополняет) персонажа: сущность <paramref name="entityId"/> получила ещё один
	/// скиннед-инстанс с участком палитры <paramref name="paletteOffset"/>.
	/// </summary>
	public void AddInstance(int entityId, ModelLoader model, int paletteOffset)
	{
		if (model.Skeleton == null)
		{
			return;
		}

		if (!_characters.TryGetValue(entityId, out var character))
		{
			int jointCount = model.Skeleton.JointCount;

			character = new Character
			{
				Model = model,
				Skeleton = model.Skeleton,
				Managed = new SkeletonPose(model.Skeleton),
				Locals = new Transform[jointCount],
				Models = new Matrix4x4[jointCount],
				JointWorld = new Matrix4x4[jointCount],
				RagdollWorld = new Matrix4x4[jointCount],
				RagdollOwned = new bool[jointCount],
			};

			character.Ozz = OzzSkeleton.Build(model.Skeleton);
			character.Pose = character.Ozz != null ? OzzPose.Create(character.Ozz) : null;
			character.Scale = MeasureScale(model.Skeleton);

			_characters[entityId] = character;
		}

		// Отрицательный офсет - персонаж БЕЗ участка палитры: headless-пробы гоняют полный путь позы
		// (клип, IK, рэгдолл), не регистрируя инстансов в батч-рендерере, и заливать им нечего и
		// некуда. SetPalette с выдуманным офсетом затёр бы чужой участок.
		if (paletteOffset >= 0)
		{
			character.Palettes.Add(paletteOffset);
		}
	}

	/// <summary>
	/// Задаёт humanoid-разметку персонажу. Отдельным методом, а не параметром
	/// <see cref="AddInstance"/>: аватар правится в окне Humanoid на живой сцене, и персонажа при
	/// этом никто не пересоздаёт.
	///
	/// Смена разметки сбрасывает уже собранные ноги и рэгдолл: они собраны ПО СТАРЫМ костям, и
	/// оставить их значит показывать результат разметки, которой больше нет.
	/// </summary>
	public void SetAvatar(int entityId, HumanoidAvatar? avatar)
	{
		if (!_characters.TryGetValue(entityId, out var character) ||
			ReferenceEquals(character.Avatar, avatar))
		{
			return;
		}

		character.Avatar = avatar;
		character.LegsBuilt = false;
		character.Legs.Clear();
		DestroyRagdoll(character);
	}

	public void Remove(int entityId)
	{
		if (_characters.Remove(entityId, out var character))
		{
			character.Dispose();
		}
	}

	public void Clear()
	{
		foreach (var character in _characters.Values)
		{
			character.Dispose();
		}

		_characters.Clear();
	}

	/// <summary>
	/// Отвязывает драйвер от мира физики: сносит все рэгдоллы и забывает мир. Звать ПЕРЕД
	/// уничтожением <see cref="ScenePhysics"/> - иначе рэгдоллы остались бы хендлами тел в
	/// уничтоженной симуляции, то есть падением при первом же кадре.
	///
	/// Именно отвязка, а не <see cref="Clear"/>: персонажи здесь не при чём. Они держат позы, ozz-
	/// хендлы и, главное, УЧАСТКИ ПАЛИТРЫ, выданные при инстанцировании, - снести их значит оставить
	/// скиннинг без палитры до следующей пересборки сцены, то есть схлопнуть всех персонажей в точку
	/// из-за выключенной галочки физики.
	/// </summary>
	public void DetachPhysics()
	{
		foreach (var character in _characters.Values)
		{
			DestroyRagdoll(character);
		}

		Physics = null;
	}

	public void Dispose() => Clear();

	/// <summary>
	/// Кадровый шаг для одной сущности. Порядок стадий фиксирован и переставлять его нельзя: клип
	/// задаёт позу, процедурные эффекты её ПРАВЯТ, и любая стадия, пересчитывающая позу из клипа,
	/// стирает всё, что сделано до неё.
	///
	/// <paramref name="modelToWorld"/> - мировой трансформ сущности префаба. Раньше он не был нужен
	/// вовсе (поза целиком жила в пространстве модели), а с появлением физики стал обязательным:
	/// луч foot IK щупает ПОЛ, а пол - объект мира.
	/// </summary>
	public void Update(Entity entity, in Matrix4x4 modelToWorld, float deltaSeconds)
	{
		if (!_characters.TryGetValue(entity.Id, out var character))
		{
			return;
		}

		character.ModelToWorld = modelToWorld;
		character.LastDelta = deltaSeconds;
		character.IkApplied = false;
		character.HasLookAt = false;

		// Клип подъёма ведёт позу ЦЕЛИКОМ: наложения, look-at и foot IK на нём выключены - поза
		// вставания авторская и цельная, а IK по лежащей на боку позе тянул бы лапы в никуда.
		if (!ApplyGetUpClip(character))
		{
			if (!ApplyLocomotion(entity, character, deltaSeconds))
			{
				ApplyClip(entity, character, deltaSeconds);
			}

			ApplyOverlayClip(entity, character, deltaSeconds);
			ApplyAdditiveClip(entity, character, deltaSeconds);
			ApplyLookAt(entity, character);
			ApplyFootIk(entity, character, deltaSeconds);
		}

		ApplySpringBones(entity, character, deltaSeconds);
		SyncRagdoll(entity, character, deltaSeconds);
		ApplyRecoveryBlend(character);

		// Палитра - последней, по итоговой позе. Всем инстансам персонажа одна и та же: они делят
		// скелет, различаются только мешами.
		character.Managed.ComputeSkinMatrices();

		foreach (int palette in character.Palettes)
		{
			_skinning.SetPalette(palette, character.Managed.SkinMatrices);
		}

		DrawDebug(character);
	}

	/// <summary>Сбрасывает счётчики кадра. Звать ОДИН РАЗ перед обходом сущностей: счётчики
	/// накапливаются по всем персонажам, и обнулять их внутри <see cref="Update"/> значило бы
	/// оставить в окне дебага данные последнего персонажа вместо суммы.</summary>
	public void BeginFrame() => ActiveRagdollCount = 0;

	// --- Подъём из рэгдолла ------------------------------------------------------------------------

	/// <summary>
	/// Начинает переход «поза рэгдолла → поза анимации»: запоминает ТЕКУЩУЮ позу как исходную.
	///
	/// Снимок обязателен. Рэгдолл к этому моменту лежит в произвольной позе, а клип начинается со
	/// своей; переключить одно на другое мгновенно - это рывок на весь размах позы, ровно то, что в
	/// игре читается как «персонаж дёрнулся и телепортировался в стойку».
	///
	/// <paramref name="modelToWorld"/> - трансформ сущности ПОСЛЕ переноса к месту лёжки: снимок
	/// РЕБЕЙЗИТСЯ в него, потому что модельные матрицы позы считаны ещё в старом. Без ребейза
	/// лежачая поза рендерилась под новым трансформом со сдвигом на весь перенос - «телепорт» в
	/// момент начала подъёма, тем заметнее, чем дальше утолкали рэгдолл от точки падения.
	/// </summary>
	public void BeginRecovery(int entityId, float duration, in Matrix4x4 modelToWorld,
		string getUpClip = "")
	{
		if (!_characters.TryGetValue(entityId, out var character) || duration <= 0f)
		{
			return;
		}

		var rebase = Matrix4x4.Identity;
		if (Matrix4x4.Invert(modelToWorld, out var worldToNew))
		{
			// Старая модель -> мир -> новая модель. Без переноса матрицы совпадают, и ребейз
			// вырождается в единичный сам собой.
			rebase = character.ModelToWorld * worldToNew;
		}

		character.RecoveryFrom ??= new Transform[character.Skeleton.JointCount];
		DecomposeModelMatrices(character, rebase, character.RecoveryFrom);

		// Авторский клип подъёма (пусто или не нашёлся - процедурный морф, прежнее поведение):
		// клип ведёт позу целиком на всю свою длительность (см. ApplyGetUpClip).
		character.GetUpClip = string.IsNullOrEmpty(getUpClip) ? null : FindClip(character, getUpClip);

		if (character.GetUpClip != null && character.GetUpClip.Duration > 0f)
		{
			// Окно вливания снимка в начальную позу клипа - авторское (duration = GetUpDuration
			// компонента): им регулируется, как быстро лежащий перетекает в сидячую стартовую позу.
			// Кламп половиной клипа: окно длиннее половины разбавляло бы снимком уже сам подъём.
			character.RecoveryDuration = character.GetUpClip.Duration;
			character.RecoveryBlendSeconds = MathF.Min(duration, character.GetUpClip.Duration * 0.5f);
		}
		else
		{
			character.GetUpClip = null;
			character.RecoveryDuration = duration;
			character.RecoveryBlendSeconds = duration;
		}

		character.RecoveryElapsed = 0f;
	}

	/// <summary>
	/// Снимает состояние, накопленное за игру и живущее СБОКУ от ECS. Звать на выходе из Play.
	///
	/// Всё, что лежит в компонентах (время клипа, состояние цикла падения), откатывает снимок Play
	/// Mode. А переход позы при подъёме - нет: он живёт здесь. Персонаж, на котором нажали Stop в
	/// середине подъёма, остался бы навсегда смешанным между лежачей и стоячей позой, и выглядело бы
	/// это как «поза сломалась», а не как «забыли сбросить».
	/// </summary>
	public void EndPlay()
	{
		foreach (var character in _characters.Values)
		{
			character.RecoveryElapsed = 0f;
			character.RecoveryDuration = 0f;
			character.GetUpClip = null;

			// Локомоушен - тот же случай, что и переход позы: фаза, замер скорости и его история
			// живут сбоку от ECS, снимком Play Mode не откатываются и накапливаются за игру.
			character.LocoPhase = 0f;
			character.LocoIdleTime = 0f;
			character.LocoSpeed = 0f;
			character.LocoHasPrev = false;

			character.LocoRunGait = false;
			character.LocoGaitBlend = 0f;

			// Хит-реакция - тоже: Stop посреди толчка не должен оставлять персонажа полукачнувшимся.
			character.ReactionDuration = 0f;
			character.ReactionElapsed = 0f;
			character.ReactionImpulsePending = false;

			// Рэгдолл СНОСИТСЯ, а не «возвращается в анимацию». Его тела - это и есть накопленное за
			// игру состояние: персонаж, упавший за секунду до Stop, лежит там, где упал, и никакой
			// откат КОМПОНЕНТОВ его оттуда не поднимет - в компонентах ничего и не менялось
			// (Enabled и Physical у него авторские). Снесённый рэгдолл на следующем же кадре
			// собирается заново по восстановленной позе, то есть ровно там, где его поставил автор.
			DestroyRagdoll(character);

			// Цепочки spring bones копят инерцию - тот же случай. Пересобираются по позе.
			character.Chains.Clear();
			character.ChainsBuilt = false;
		}
	}

	/// <summary>
	/// Запускает хит-реакцию: временный частичный рэгдолл. Корпус получает толчок
	/// <paramref name="velocityChange"/> (м/с, приращение скорости - от массы не зависит) и на
	/// <paramref name="duration"/> секунд поза корпуса подмешивается из физики, ноги продолжают
	/// идти анимацией. Требует <see cref="RagdollComponent"/> на сущности (нечем реагировать);
	/// выключенный компонент - нормальный случай, тела соберутся на время реакции и снесутся после.
	/// Повторный удар во время реакции ПЕРЕЗАПУСКАЕТ конверт и добавляет толчок - очередь ударов
	/// сливается в один длинный, а не теряется.
	/// </summary>
	public void TriggerHitReaction(int entityId, Vector3 velocityChange, float duration = 0.7f,
		float strength = 1f)
	{
		if (!_characters.TryGetValue(entityId, out var character) || duration <= 0f)
		{
			return;
		}

		// Перезапуск ПОВЕРХ идущей реакции - БЕЗ обнуления конверта: атака стартует с ТЕКУЩЕГО
		// веса, а не с нуля. Обнуление на кадр возвращало позу в чистую анимацию и тут же снова
		// роняло в физику - при серии ударов (капсулы в контакте, кулдаун тарана короче конверта)
		// это читалось как «дёргается между рэгдоллом и анимацией».
		float carried = 0f;
		if (character.ReactionDuration > 0f && character.ReactionElapsed < character.ReactionDuration)
		{
			float t = Math.Clamp(character.ReactionElapsed / character.ReactionDuration, 0f, 1f);
			float attack = Math.Clamp(character.ReactionElapsed / ReactionAttackSeconds, 0f, 1f);
			float release = 1f - t * t * (3f - 2f * t);
			carried = character.ReactionStrength * attack * release;
		}

		character.ReactionElapsed = Math.Clamp(carried, 0f, 1f) * ReactionAttackSeconds;
		character.ReactionDuration = duration;
		character.ReactionStrength = Math.Clamp(strength, 0f, 1f);
		character.ReactionImpulse = velocityChange;
		character.ReactionImpulsePending = true;
	}

	/// <summary>Длительность атаки конверта реакции, с: толчок обязан быть виден почти сразу.</summary>
	private const float ReactionAttackSeconds = 0.06f;

	/// <summary>Идёт ли ещё подъём. По нему вызывающий понимает, когда персонаж снова управляем.</summary>
	public bool IsRecovering(int entityId) =>
		_characters.TryGetValue(entityId, out var character) && character.RecoveryElapsed < character.RecoveryDuration;

	/// <summary>
	/// Успокоился ли рэгдолл: скорость самой быстрой кости в ДОЛЯХ характерного размера скелета за
	/// секунду. Доля, а не абсолют - у лисы габарит 160 единиц модели, у метрового персонажа 1.8, и
	/// одно и то же число означает для них совершенно разное.
	/// </summary>
	public bool IsRagdollSettled(int entityId, float relativeSpeed)
	{
		if (!_characters.TryGetValue(entityId, out var character) || character.Ragdoll == null ||
			Physics == null)
		{
			return true;
		}

		float threshold = relativeSpeed * character.Scale * WorldScaleOf(character.ModelToWorld);
		var bodies = Physics.World.Simulation.Bodies;

		for (int i = 0; i < character.Ragdoll.BoneCount; i++)
		{
			if (bodies[character.Ragdoll.BodyOf(i)].Velocity.Linear.Length() > threshold)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Куда «смотрит» лежащий персонаж: горизонтальная проекция оси таз→шея текущей позы в мире.
	/// Для разворота сущности ПЕРЕД подъёмом: встать вдоль тела, а не докручиваться из поворота,
	/// с которым персонаж когда-то упал, - укатившийся рэгдолл лежит под произвольным углом, и
	/// подъём без разворота проворачивал корпус на весь этот угол («странно поднимается»).
	/// Ложь (false) - у почти вертикально лежащей оси (рэгдолл замер сидя): горизонтальной
	/// проекции не из чего взяться, и прежний поворот честнее случайного.
	/// </summary>
	public bool TryGetLyingFacing(int entityId, out Vector3 worldForward)
	{
		worldForward = default;

		if (!_characters.TryGetValue(entityId, out var character) || character.Avatar == null)
		{
			return false;
		}

		int hips = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Hips] ?? string.Empty);
		int neck = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Neck] ?? string.Empty);

		if (neck < 0)
		{
			neck = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Head] ?? string.Empty);
		}

		if (hips < 0 || neck < 0)
		{
			return false;
		}

		var direction =
			Vector3.Transform(character.Models[neck].Translation, character.ModelToWorld) -
			Vector3.Transform(character.Models[hips].Translation, character.ModelToWorld);
		direction.Y = 0f;

		// Порог - доля длины оси: лежащее тело даёт почти всю длину в горизонталь, сидящее - крохи.
		float span = Vector3.Distance(character.Models[neck].Translation, character.Models[hips].Translation) *
			WorldScaleOf(character.ModelToWorld);

		if (direction.Length() < 0.3f * MathF.Max(span, 1e-6f))
		{
			return false;
		}

		worldForward = Vector3.Normalize(direction);
		return true;
	}

	/// <summary>Мировая позиция таза (или корня рэгдолла) - туда персонаж встаёт. Именно кость, а не
	/// трансформ сущности: сущность всё это время стояла там, откуда персонаж упал, а лежит он уже в
	/// другом месте.</summary>
	public bool TryGetRagdollRootWorld(int entityId, out Vector3 position)
	{
		position = Vector3.Zero;

		if (!_characters.TryGetValue(entityId, out var character) || character.Ragdoll == null ||
			character.Ragdoll.BoneCount == 0 || Physics == null)
		{
			return false;
		}

		position = Physics.World.Simulation.Bodies[character.Ragdoll.BodyOf(0)].Pose.Position;
		return true;
	}

	/// <summary>
	/// Поза подъёма из АВТОРСКОГО клипа (см. BeginRecovery): семплирует клип по времени
	/// восстановления, без зацикливания. Возвращает true, пока подъём ведёт позу, - обычный стек
	/// (локомоушен, наложения, IK) в это время не работает. Снимок лёжки вливается поверх в
	/// ApplyRecoveryBlend коротким окном.
	/// </summary>
	private bool ApplyGetUpClip(Character character)
	{
		if (character.GetUpClip == null)
		{
			return false;
		}

		if (character.Pose == null || character.RecoveryElapsed >= character.RecoveryDuration)
		{
			character.GetUpClip = null;
			return false;
		}

		var clip = GetOzzClip(character, character.GetUpClip);
		if (clip == null || clip.Duration <= 0f)
		{
			character.GetUpClip = null;
			return false;
		}

		bool ok =
			character.Pose.Sample(clip, MathF.Min(character.RecoveryElapsed, clip.Duration)) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (!ok)
		{
			character.GetUpClip = null;
			return false;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		return true;
	}

	/// <summary>
	/// На спине ли лежит персонаж: куда смотрит в мире «спинной верх» таза - ось, которая в
	/// bind-позе смотрела в модельный +Y. Для выбора клипа подъёма (со спины/с живота).
	/// </summary>
	public bool TryGetLyingSide(int entityId, out bool onBack)
	{
		onBack = false;

		if (!_characters.TryGetValue(entityId, out var character) || character.Avatar == null)
		{
			return false;
		}

		int hips = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Hips] ?? string.Empty);
		if (hips < 0)
		{
			return false;
		}

		if (!Matrix4x4.Invert(BindModelMatrix(character.Skeleton, hips), out var inverseBind))
		{
			return false;
		}

		var upLocal = Vector3.TransformNormal(Vector3.UnitY, inverseBind);
		var upWorld = Vector3.TransformNormal(upLocal, character.Models[hips] * character.ModelToWorld);

		if (upWorld.LengthSquared() < 1e-10f)
		{
			return false;
		}

		onBack = Vector3.Normalize(upWorld).Y < 0f;
		return true;
	}

	/// <summary>Модельная матрица джойнта в BIND-позе - композицией локалей вверх по родителям.</summary>
	private static Matrix4x4 BindModelMatrix(PreparedSkeleton skeleton, int joint)
	{
		var result = Matrix4x4.Identity;

		for (int j = joint; j >= 0; j = skeleton.Parents[j])
		{
			var bind = skeleton.BindLocals[j];
			result *= MathUtils.CreateTrs(bind.position, bind.rotation, bind.scale);
		}

		return result;
	}

	/// <summary>
	/// Смешивает позу подъёма с позой анимации. Идёт ПОСЛЕДНЕЙ стадией, после рэгдолла: он к этому
	/// моменту уже переведён в режим анимации и позу не пишет, а всё, что до него, - это как раз та
	/// целевая поза, к которой персонаж встаёт.
	///
	/// Смешиваются РАЗЛОЖЕННЫЕ TRS, а не матрицы напрямую: покомпонентная интерполяция матриц
	/// поворота даёт неортогональный базис в середине перехода, то есть кости, которые на полпути
	/// сплющиваются и растягиваются.
	/// </summary>
	private void ApplyRecoveryBlend(Character character)
	{
		if (character.RecoveryElapsed >= character.RecoveryDuration || character.RecoveryFrom == null)
		{
			return;
		}

		character.RecoveryElapsed += character.LastDelta;

		// Вес - по ОКНУ ВЛИВАНИЯ, не по всей длительности: у морфа они совпадают (прежнее
		// поведение), у авторского клипа окно короткое - дальше клип ведёт позу сам.
		float window = character.RecoveryBlendSeconds > 0f
			? character.RecoveryBlendSeconds
			: character.RecoveryDuration;
		float t = Math.Clamp(character.RecoveryElapsed / window, 0f, 1f);

		// Сглаживание на концах (smoothstep): линейный вес даёт заметный излом скорости в начале и в
		// конце подъёма - персонаж трогается и останавливается рывком.
		float weight = t * t * (3f - 2f * t);

		for (int i = 0; i < character.Models.Length; i++)
		{
			if (!Matrix4x4.Decompose(character.Models[i], out var scale, out var rotation, out var translation))
			{
				continue;
			}

			var from = character.RecoveryFrom[i];

			character.Models[i] = MathUtils.CreateTrs(
				Vector3.Lerp(from.position, translation, weight),
				Quaternion.Slerp(from.rotation, rotation, weight),
				Vector3.Lerp(from.scale, scale, weight));
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	private static void DecomposeModelMatrices(Character character, in Matrix4x4 rebase, Transform[] target)
	{
		for (int i = 0; i < character.Models.Length; i++)
		{
			if (Matrix4x4.Decompose(character.Models[i] * rebase, out var scale, out var rotation, out var translation))
			{
				target[i] = new Transform { position = translation, rotation = rotation, scale = scale };
			}
			else
			{
				target[i] = new Transform { position = Vector3.Zero, rotation = Quaternion.Identity, scale = Vector3.One };
			}
		}
	}

	/// <summary>
	/// Локомоушен-бленд (см. <see cref="LocomotionComponent"/>): стойка/шаг/бег по замеренной
	/// скорости сущности, темп шага масштабируется под неё. Возвращает false, когда позу вести
	/// нечем (нет компонента, выключен, нет ozz, клипы не нашлись) - тогда позой занимается
	/// обычный <see cref="Animator"/>. Причины фоллбека снаружи неразличимы намеренно: их
	/// показывает окно дебага, а вызывающему важно только «кто ведёт позу».
	/// </summary>
	private bool ApplyLocomotion(Entity entity, Character character, float deltaSeconds)
	{
		character.LocoActive = false;

		if (character.Pose == null || !entity.HasComponent<LocomotionComponent>())
		{
			return false;
		}

		var settings = entity.GetComponent<LocomotionComponent>();
		if (!settings.Enabled)
		{
			return false;
		}

		// Клипы ищутся по именам только при их СМЕНЕ - как AppliedClip у Animator.
		string key = $"{settings.IdleClip}\n{settings.WalkClip}\n{settings.RunClip}";
		if (!string.Equals(key, character.LocoClipsKey, StringComparison.Ordinal))
		{
			character.LocoClipsKey = key;
			character.LocoIdle = FindClip(character, settings.IdleClip ?? string.Empty);
			character.LocoWalk = FindClip(character, settings.WalkClip ?? string.Empty);
			character.LocoRun = FindClip(character, settings.RunClip ?? string.Empty);
			character.LocoOffsetsValid = false;
		}

		// Все три клипа обязательны. Смешивать «что нашлось» нельзя: ozz добирает недостающий вес
		// rest-позой, и персонаж с опечаткой в имени клипа ходил бы полурастворённым в bind-позу -
		// это хуже честного фоллбека на Animator, который сразу видно.
		if (character.LocoIdle == null || character.LocoWalk == null || character.LocoRun == null)
		{
			return false;
		}

		var idleClip = GetOzzClip(character, character.LocoIdle);
		var walkClip = GetOzzClip(character, character.LocoWalk);
		var runClip = GetOzzClip(character, character.LocoRun);

		if (idleClip == null || walkClip == null || runClip == null ||
			idleClip.Duration <= 0f || walkClip.Duration <= 0f || runClip.Duration <= 0f)
		{
			return false;
		}

		character.LocoPoseA ??= OzzPose.Create(character.Ozz);
		character.LocoPoseB ??= OzzPose.Create(character.Ozz);

		if (character.LocoPoseA == null || character.LocoPoseB == null)
		{
			return false;
		}

		if (!character.LocoOffsetsValid)
		{
			character.LocoWalkPhaseOffset = GaitPhaseOffset(character, walkClip);
			character.LocoRunPhaseOffset = GaitPhaseOffset(character, runClip);
			character.LocoWalkStride = MeasureStrideSpeed(character, walkClip);
			character.LocoRunStride = MeasureStrideSpeed(character, runClip);
			character.LocoOffsetsValid = true;
		}

		float walkSpeed = MathF.Max(settings.WalkSpeed, 1e-3f);
		float runSpeed = MathF.Max(settings.RunSpeed, walkSpeed + 1e-3f);

		// Скорость меряется по XZ-перемещению сущности: вертикаль - это кочки и падения, темпу шага
		// она не принадлежит. При нулевом шаге (режим редактирования) не двигается ничего - поза
		// считается по текущим фазе и скорости, как и весь остальной стек.
		if (deltaSeconds > 0f)
		{
			var worldPos = character.ModelToWorld.Translation;
			float raw = character.LocoSpeed;

			if (character.LocoHasPrev)
			{
				var delta = worldPos - character.LocoPrevWorld;
				raw = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z) / deltaSeconds;

				// Потолок - от телепортов: перенос сущности при подъёме из рэгдолла - это метры за
				// кадр, и без потолка каждый подъём начинался бы со вспышки бега.
				raw = MathF.Min(raw, runSpeed * 2f);
			}

			character.LocoPrevWorld = worldPos;
			character.LocoHasPrev = true;

			float alpha = settings.Smoothing > 0f ? 1f - MathF.Exp(-settings.Smoothing * deltaSeconds) : 1f;
			character.LocoSpeed += (raw - character.LocoSpeed) * alpha;
		}

		float speed = character.LocoSpeed;

		// Два активных слоя и общая нормированная фаза. Частота цикла на отрезке стойка-шаг растёт
		// пропорционально скорости (длина шага авторская, темп подгоняется), на отрезке шаг-бег -
		// линейно между авторскими темпами: скорость в точке бленда по построению равна
		// lerp(WalkSpeed, RunSpeed, t), и отдельного множителя «догнать скорость» не нужно.
		OzzClip layerA, layerB;
		float timeA, timeB, weightA, weightB, frequency;

		// Время слоя - от общей фазы плюс СДВИГ ДО СОБЫТИЯ АЛЛЮРА клипа (см. LocoWalkPhaseOffset):
		// сама по себе общая фаза выравнивает только темп, а не то, ЧТО в этот момент делают ноги.
		float walkTime = (character.LocoPhase + character.LocoWalkPhaseOffset) % 1f * walkClip.Duration;
		float runTime = (character.LocoPhase + character.LocoRunPhaseOffset) % 1f * runClip.Duration;

		// Аллюр переключается с ГИСТЕРЕЗИСОМ (вверх на 60% отрезка, вниз на 40%), бленд - кроссфейд
		// по времени ~0.2 с (см. LocoRunGait). Темп внутри аллюра масштабируется под скорость:
		// застрявший на 2 м/с бегун - это замедленный ЧИСТЫЙ галоп, а не полусмесь аллюров.
		float switchUp = walkSpeed + 0.6f * (runSpeed - walkSpeed);
		float switchDown = walkSpeed + 0.4f * (runSpeed - walkSpeed);

		if (!character.LocoRunGait && speed > switchUp)
		{
			character.LocoRunGait = true;
		}
		else if (character.LocoRunGait && speed < switchDown)
		{
			character.LocoRunGait = false;
		}

		if (deltaSeconds > 0f)
		{
			float goal = character.LocoRunGait ? 1f : 0f;
			character.LocoGaitBlend += (goal - character.LocoGaitBlend) *
				(1f - MathF.Exp(-8f * deltaSeconds));
		}

		// Множитель темпа - от ПРИРОДНОЙ скорости шага клипа (см. LocoWalkStride), в единицах
		// модели: скорость сущности переводится масштабом. Авторские WalkSpeed/RunSpeed - только
		// пороги аллюра. Фоллбек на них - когда замер не удался (лапа не размечена).
		float worldScale = MathF.Max(WorldScaleOf(character.ModelToWorld), 1e-6f);
		float speedModel = speed / worldScale;

		float walkRate = character.LocoWalkStride > 1e-3f
			? speedModel / character.LocoWalkStride
			: speed / walkSpeed;
		float runRate = character.LocoRunStride > 1e-3f
			? speedModel / character.LocoRunStride
			: speed / runSpeed;

		if (speed <= walkSpeed && character.LocoGaitBlend < 0.5f)
		{
			float t = Math.Clamp(speed / walkSpeed, 0f, 1f);

			layerA = idleClip;
			timeA = character.LocoIdleTime;
			weightA = 1f - t;

			layerB = walkClip;
			timeB = walkTime;
			weightB = t;

			frequency = walkRate / walkClip.Duration;

			character.LocoIdleWeight = weightA;
			character.LocoWalkWeight = weightB;
			character.LocoRunWeight = 0f;
		}
		else
		{
			float t = Math.Clamp(character.LocoGaitBlend, 0f, 1f);

			layerA = walkClip;
			timeA = walkTime;
			weightA = 1f - t;

			layerB = runClip;
			timeB = runTime;
			weightB = t;

			// Темп каждого слоя гонится за реальной скоростью в ЕГО аллюре, между ними -
			// кроссфейдный вес: и разогнанный шаг, и замедленный галоп держат длину шага.
			float walkFrequency = walkRate / walkClip.Duration;
			float runFrequency = runRate / runClip.Duration;
			frequency = walkFrequency + (runFrequency - walkFrequency) * t;

			character.LocoIdleWeight = 0f;
			character.LocoWalkWeight = weightA;
			character.LocoRunWeight = weightB;
		}

		if (deltaSeconds > 0f)
		{
			character.LocoPhase = (character.LocoPhase + frequency * deltaSeconds) % 1f;
			character.LocoIdleTime = (character.LocoIdleTime + deltaSeconds) % idleClip.Duration;
		}

		bool ok =
			character.LocoPoseA.Sample(layerA, timeA) &&
			character.LocoPoseB.Sample(layerB, timeB) &&
			character.Pose.Blend([character.LocoPoseA, character.LocoPoseB], [weightA, weightB]) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (!ok)
		{
			return false;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		character.LocoActive = true;
		return true;
	}

	/// <summary>
	/// Фаза СОБЫТИЯ АЛЛЮРА в клипе: нижняя точка задней левой лапы (по humanoid-разметке), 0..1.
	/// Считается один раз при резолве клипов перебором 32 семплов - выравнивание грубое, но у цикла
	/// шага событие размазано на десятки миллисекунд, и тридцать второй доли цикла хватает.
	/// Без разметки (или кость не нашлась) сдвиг нулевой - то есть ровно прежнее поведение.
	/// </summary>
	private static float GaitPhaseOffset(Character character, OzzClip clip)
	{
		string footName = character.Avatar?[HumanoidBone.LeftFoot] ?? string.Empty;
		int foot = string.IsNullOrEmpty(footName) ? -1 : character.Skeleton.FindJoint(footName);

		if (foot < 0 || character.LocoPoseA == null)
		{
			return 0f;
		}

		const int samples = 32;
		var models = new Matrix4x4[character.Skeleton.JointCount];

		float bestPhase = 0f;
		float bestHeight = float.MaxValue;

		for (int k = 0; k < samples; k++)
		{
			float phase = (float)k / samples;

			if (!character.LocoPoseA.Sample(clip, phase * clip.Duration) ||
				!character.LocoPoseA.LocalToModel() ||
				!character.LocoPoseA.ReadModelMatrices(models))
			{
				return 0f;
			}

			float height = models[foot].Translation.Y;
			if (height < bestHeight)
			{
				bestHeight = height;
				bestPhase = phase;
			}
		}

		// Время слоя считается как (фаза + сдвиг): на общей фазе 0 клип стоит ровно в своём событии,
		// то есть сдвиг - это ФАЗА СОБЫТИЯ в клипе, как она есть.
		return bestPhase;
	}

	/// <summary>
	/// Природная скорость шага клипа: средняя горизонтальная скорость задней левой лапы в
	/// пространстве модели за её ТАКТ ОПОРЫ (нижняя четверть размаха высоты), на авторском темпе.
	/// У in-place клипа опорная лапа едет назад ровно со скоростью, с которой персонаж «должен»
	/// ехать вперёд, - это и есть скорость, при которой лапы не скользят. Ноль - замер не удался
	/// (нет разметки, лапа не циклится), вызывающий откатывается на авторские числа.
	/// </summary>
	private static float MeasureStrideSpeed(Character character, OzzClip clip)
	{
		string footName = character.Avatar?[HumanoidBone.LeftFoot] ?? string.Empty;
		int foot = string.IsNullOrEmpty(footName) ? -1 : character.Skeleton.FindJoint(footName);

		if (foot < 0 || character.LocoPoseA == null)
		{
			return 0f;
		}

		const int samples = 48;
		var positions = new Vector3[samples];
		float minHeight = float.MaxValue;
		float maxHeight = float.MinValue;

		for (int k = 0; k < samples; k++)
		{
			if (!character.LocoPoseA.Sample(clip, clip.Duration * k / samples) ||
				!character.LocoPoseA.LocalToModel() ||
				!character.LocoPoseA.ReadModelMatrices(character.Models))
			{
				return 0f;
			}

			positions[k] = character.Models[foot].Translation;
			minHeight = MathF.Min(minHeight, positions[k].Y);
			maxHeight = MathF.Max(maxHeight, positions[k].Y);
		}

		float threshold = minHeight + 0.25f * (maxHeight - minHeight);
		float dt = clip.Duration / samples;
		float travel = 0f;
		float seconds = 0f;

		for (int k = 0; k < samples; k++)
		{
			int next = (k + 1) % samples;
			if (positions[k].Y >= threshold || positions[next].Y >= threshold)
			{
				continue;
			}

			var step = positions[next] - positions[k];
			travel += MathF.Sqrt(step.X * step.X + step.Z * step.Z);
			seconds += dt;
		}

		return seconds > 1e-4f ? travel / seconds : 0f;
	}

	private void ApplyClip(Entity entity, Character character, float deltaSeconds)
	{
		if (!entity.HasComponent<Animator>())
		{
			SamplePose(character);
			return;
		}

		// Компонент правится ПО ССЫЛКЕ. Прежний вариант читал копию и возвращал её через
		// AddComponent каждый кадр - а это обращение к хранилищу сущностей на каждом кадре на
		// каждого персонажа, которое в худшем случае двигает сущность между архетипами. Здесь нужно
		// изменить одно поле, и ref-доступ делает ровно это, ничего не трогая в структуре стора.
		ref var animator = ref entity.GetComponent<Animator>();

		// Клип ищется по имени только при СМЕНЕ имени: линейный поиск по списку клипов дёшев, но
		// делать его каждый кадр на каждого персонажа незачем.
		if (!string.Equals(animator.ClipName ?? string.Empty, character.AppliedClip, StringComparison.Ordinal))
		{
			character.AppliedClip = animator.ClipName ?? string.Empty;
			character.Player.Clip = FindClip(character, character.AppliedClip);
		}

		character.Player.Loop = animator.Loop;
		character.Player.Speed = animator.Speed;

		// Время живёт в КОМПОНЕНТЕ (его видно и можно скрабить в инспекторе), но двигает его плеер:
		// только он знает про зацикливание и про конец незацикленного клипа.
		character.Player.Time = animator.Time;
		float timeBefore = character.Player.Time;

		if (animator.Playing)
		{
			character.Player.Advance(deltaSeconds);
		}

		animator.Time = character.Player.Time;

		SamplePose(character);

		if (animator.RootMotion)
		{
			ApplyRootMotion(entity, character, in animator, timeBefore);
		}
	}

	/// <summary>
	/// Root motion по образцу ozz motion_playback: XZ-трансляция КОРНЕВОЙ кости клипа вычитается из
	/// позы (персонаж остаётся на месте в пространстве модели) и накапливается дельтами в позицию
	/// сущности - тело движется со скоростью, которую задал автор анимации, включая заворот лупа.
	/// Вертикаль остаётся в позе: прыжок в клипе - это движение позы, а не сущности.
	///
	/// Не сочетается с Character Body (телом владеет его рулевое) и, как остальная процедурка,
	/// требует нативного ozz - без него стадия молча пропускается.
	/// </summary>
	private static void ApplyRootMotion(Entity entity, Character character, in Animator animator,
		float timeBefore)
	{
		var clip = character.Player.Clip;

		if (clip == null || character.Pose == null || !entity.HasComponent<Position>() ||
			entity.HasComponent<CharacterBodyComponent>())
		{
			return;
		}

		if (!ReferenceEquals(character.MotionClip, clip))
		{
			character.MotionClip = clip;
			character.MotionJoint = MotionJointOf(character);
		}

		if (character.MotionJoint < 0 || character.MotionJoint >= clip.Tracks.Length)
		{
			return;
		}

		var track = clip.Tracks[character.MotionJoint];
		if (track.TranslationTimes.Length < 2)
		{
			// Одного ключа (или пустого канала) движению не хватает по построению.
			return;
		}

		// Компенсация: корень возвращается к ПЕРВОМУ ключу по XZ - поза шагает на месте, а весь
		// путь уходит в сущность. Y не трогается: вертикаль клипа - это поза, не путь.
		var atTime = SampleMotion(track, character.Player.Time);
		var offset = atTime - track.Translations[0];
		offset.Y = 0f;

		character.Locals[character.MotionJoint].position -= offset;

		if (!character.Pose.WriteLocalTransforms(character.Locals) ||
			!character.Pose.LocalToModel() ||
			!character.Pose.ReadModelMatrices(character.Models))
		{
			return;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);

		// Дельта пути за кадр - с учётом заворота лупа: время после меньше времени до (при прямом
		// ходе) означает, что плеер завернулся, и к дельте добавляется полный путь цикла.
		var delta = atTime - SampleMotion(track, timeBefore);

		if (animator.Loop && clip.Duration > 1e-6f)
		{
			var net = track.Translations[^1] - track.Translations[0];

			if (character.Player.Speed >= 0f && character.Player.Time < timeBefore - 1e-6f)
			{
				delta += net;
			}
			else if (character.Player.Speed < 0f && character.Player.Time > timeBefore + 1e-6f)
			{
				delta -= net;
			}
		}

		delta.Y = 0f;

		if (delta.LengthSquared() < 1e-12f)
		{
			return;
		}

		// Дельта живёт в пространстве МОДЕЛИ, позиция сущности - в пространстве РОДИТЕЛЯ:
		// модель -> мир -> родитель.
		var worldDelta = Vector3.TransformNormal(delta, character.ModelToWorld);
		var parentToWorld = PrefabSceneViewport.ParentToWorldMatrix(entity);

		if (Matrix4x4.Invert(parentToWorld, out var worldToParent))
		{
			entity.GetComponent<Position>().value += Vector3.TransformNormal(worldDelta, worldToParent);
		}
	}

	/// <summary>Самый верхний предок таза разметки (без разметки - нулевой джойнт): авторское
	/// движение живёт на корневом узле рига, а не на тазе - таз качается внутри цикла.</summary>
	private static int MotionJointOf(Character character)
	{
		int joint = character.Skeleton.FindJoint(character.Avatar?[HumanoidBone.Hips] ?? string.Empty);

		if (joint < 0)
		{
			joint = 0;
		}

		while (character.Skeleton.Parents[joint] >= 0)
		{
			joint = character.Skeleton.Parents[joint];
		}

		return joint;
	}

	/// <summary>Линейная интерполяция дорожки трансляции корня. Линейный проход осознанно: у
	/// motion-дорожки единицы-десятки ключей, и звать её приходится дважды за кадр.</summary>
	private static Vector3 SampleMotion(JointTrack track, float time)
	{
		var times = track.TranslationTimes;
		var values = track.Translations;

		if (time <= times[0])
		{
			return values[0];
		}

		for (int i = 1; i < times.Length; i++)
		{
			if (time <= times[i])
			{
				float span = times[i] - times[i - 1];
				float t = span > 1e-9f ? (time - times[i - 1]) / span : 1f;
				return Vector3.Lerp(values[i - 1], values[i], t);
			}
		}

		return values[^1];
	}

	/// <summary>Семплирует клип в позу: нативным ozz, если он есть, иначе C#-семплером. Оба пути
	/// оставляют результат в одном виде - модельных матрицах <see cref="Character.Models"/> и
	/// локальных TRS, - поэтому процедурные стадии ниже про этот выбор не знают.</summary>
	private static void SamplePose(Character character)
	{
		var clip = character.Player.Clip;
		var ozzClip = clip != null ? GetOzzClip(character, clip) : null;

		if (character.Pose != null && ozzClip != null &&
			character.Pose.Sample(ozzClip, character.Player.Time) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals))
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
			return;
		}

		character.Player.Apply(character.Managed);
		character.Managed.ModelMatrices.CopyTo(character.Models, 0);
		character.Managed.Locals.CopyTo(character.Locals, 0);
	}

	private static OzzClip? GetOzzClip(Character character, PreparedAnimation clip)
	{
		if (character.Ozz == null)
		{
			return null;
		}

		if (!character.Clips.TryGetValue(clip, out var built))
		{
			// Неудачная сборка кешируется как null: иначе кадр за кадром повторялась бы одна и та же
			// провалившаяся перепаковка клипа в сжатый формат ozz.
			built = OzzClip.Build(character.Ozz, clip);
			character.Clips[clip] = built;
		}

		return built;
	}

	private static PreparedAnimation? FindClip(Character character, string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return null;
		}

		foreach (var clip in character.Model.Animations)
		{
			if (string.Equals(clip.Name, name, StringComparison.Ordinal))
			{
				return clip;
			}
		}

		return null;
	}

	/// <summary>
	/// Частичный бленд: поддерево от корневой кости играет свой клип поверх базовой позы (ozz
	/// partial_blend). Идёт ПОСЛЕ базы (клип или локомоушен) и ДО look-at/foot IK: те правят уже
	/// смешанную позу. Веса - ПОСУСТАВНЫЕ и комплементарные (сумма на каждом суставе единица),
	/// поэтому вне поддерева база проходит нетронутой побитово, а rest-поза ozz не подмешивается.
	/// </summary>
	private void ApplyOverlayClip(Entity entity, Character character, float deltaSeconds)
	{
		if (character.Pose == null || !entity.HasComponent<OverlayClipComponent>())
		{
			return;
		}

		var settings = entity.GetComponent<OverlayClipComponent>();
		float weight = Math.Clamp(settings.Weight, 0f, 1f);

		if (!settings.Enabled || weight <= 0f || string.IsNullOrEmpty(settings.ClipName))
		{
			return;
		}

		if (!string.Equals(settings.ClipName, character.OverlayClipName, StringComparison.Ordinal))
		{
			character.OverlayClipName = settings.ClipName;
			character.OverlayClip = FindClip(character, settings.ClipName);
			character.OverlayTime = 0f;
		}

		if (character.OverlayClip == null)
		{
			return;
		}

		var clip = GetOzzClip(character, character.OverlayClip);
		if (clip == null || clip.Duration <= 0f)
		{
			return;
		}

		character.OverlayPose ??= OzzPose.Create(character.Ozz);
		if (character.OverlayPose == null)
		{
			return;
		}

		// Корень поддерева: авторское имя старше разметки, как везде. Слот по умолчанию - грудь;
		// у четвероногого она несёт передние лапы, и для «оглядывается» автор ставит шею.
		int root = character.Skeleton.FindJoint(
			JointOf(character, settings.RootJoint, HumanoidBone.Chest));
		if (root < 0)
		{
			return;
		}

		EnsureOverlayMasks(character, root, weight);

		if (deltaSeconds > 0f)
		{
			character.OverlayTime += deltaSeconds * MathF.Max(settings.Speed, 0f);
			character.OverlayTime = settings.Loop
				? character.OverlayTime % clip.Duration
				: MathF.Min(character.OverlayTime, clip.Duration);
		}

		// Приёмник бленда намеренно совпадает со слоем базы: ozz пишет выход посуставно после
		// чтения слоёв того же сустава (см. OzzPose.Blend), и отдельная копия позы не нужна.
		bool ok =
			character.OverlayPose.Sample(clip, character.OverlayTime) &&
			character.Pose.Blend([character.Pose, character.OverlayPose], [1f, 1f],
				[character.OverlayMaskBase, character.OverlayMaskLayer]) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (ok)
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}
	}

	/// <summary>
	/// Аддитивный слой: дельта клипа (см. <see cref="AdditiveClip"/>) суммируется поверх текущей
	/// позы через additive_layers ozz. Идёт ПОСЛЕ overlay (дельта ложится и на его результат) и
	/// ДО look-at/foot IK. База не участвует в усреднении - слой чистая добавка, и вес просто
	/// масштабирует её к единичной трансформации.
	/// </summary>
	private void ApplyAdditiveClip(Entity entity, Character character, float deltaSeconds)
	{
		if (character.Pose == null || !entity.HasComponent<AdditiveClipComponent>())
		{
			return;
		}

		var settings = entity.GetComponent<AdditiveClipComponent>();
		float weight = Math.Clamp(settings.Weight, 0f, 1f);

		if (!settings.Enabled || weight <= 0f || string.IsNullOrEmpty(settings.ClipName))
		{
			return;
		}

		if (!string.Equals(settings.ClipName, character.AdditiveClipName, StringComparison.Ordinal))
		{
			character.AdditiveClipName = settings.ClipName;
			character.AdditiveSource = FindClip(character, settings.ClipName);
			character.AdditiveDelta = character.AdditiveSource != null
				? AdditiveClip.Build(character.AdditiveSource, character.Skeleton)
				: null;
			character.AdditiveTime = 0f;
		}

		if (character.AdditiveDelta == null)
		{
			return;
		}

		var clip = GetOzzClip(character, character.AdditiveDelta);
		if (clip == null || clip.Duration <= 0f)
		{
			return;
		}

		character.AdditivePose ??= OzzPose.Create(character.Ozz);
		if (character.AdditivePose == null)
		{
			return;
		}

		if (deltaSeconds > 0f)
		{
			character.AdditiveTime += deltaSeconds * MathF.Max(settings.Speed, 0f);
			character.AdditiveTime = settings.Loop
				? character.AdditiveTime % clip.Duration
				: MathF.Min(character.AdditiveTime, clip.Duration);
		}

		bool ok =
			character.AdditivePose.Sample(clip, character.AdditiveTime) &&
			character.Pose.BlendLayered([character.Pose, character.AdditivePose], [1f, weight],
				[null, null], [false, true]) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals);

		if (ok)
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}
	}

	/// <summary>Перестраивает посуставные веса при смене корня или веса. Принадлежность поддереву -
	/// подъёмом по родителям: скелет в два-три десятка костей, и кэшировать тут нечего.</summary>
	private static void EnsureOverlayMasks(Character character, int root, float weight)
	{
		if (character.OverlayRoot == root && character.OverlayWeight == weight &&
			character.OverlayMaskBase != null && character.OverlayMaskLayer != null)
		{
			return;
		}

		int jointCount = character.Skeleton.JointCount;
		character.OverlayMaskBase ??= new float[jointCount];
		character.OverlayMaskLayer ??= new float[jointCount];

		for (int joint = 0; joint < jointCount; joint++)
		{
			bool inSubtree = false;
			for (int j = joint; j >= 0; j = character.Skeleton.Parents[j])
			{
				if (j == root)
				{
					inSubtree = true;
					break;
				}
			}

			character.OverlayMaskBase[joint] = inSubtree ? 1f - weight : 1f;
			character.OverlayMaskLayer[joint] = inSubtree ? weight : 0f;
		}

		character.OverlayRoot = root;
		character.OverlayWeight = weight;
	}

	private static void ApplyLookAt(Entity entity, Character character)
	{
		if (character.Pose == null || !entity.HasComponent<LookAtComponent>())
		{
			return;
		}

		var lookAt = entity.GetComponent<LookAtComponent>();
		if (!lookAt.Enabled || lookAt.Weight <= 0f || string.IsNullOrEmpty(lookAt.Joint))
		{
			return;
		}

		int joint = character.Skeleton.FindJoint(lookAt.Joint);
		if (joint < 0)
		{
			return;
		}

		// Цель приходит МИРОВОЙ, а IK работает в пространстве модели. Здесь они совпадают: сущность
		// префаба ставит модель своим трансформом, а поза считается в её локальном пространстве -
		// перевод появится вместе с поддержкой смещённых персонажей.
		if (character.Pose.AimIk(joint, lookAt.Target, lookAt.Forward, lookAt.Up, lookAt.Up, lookAt.Weight) &&
			character.Pose.LocalToModel() &&
			character.Pose.ReadModelMatrices(character.Models) &&
			character.Pose.ReadLocalTransforms(character.Locals))
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}

		character.HasLookAt = true;
		character.LookAtTarget = lookAt.Target;
		character.LookAtJoint = joint;
	}

	// --- Foot IK -----------------------------------------------------------------------------------

	/// <summary>
	/// Привязка стоп к полу. Идёт ПОСЛЕ look-at и ДО spring bones: look-at правит верх скелета и с
	/// ногами не пересекается, а вторичное движение обязано считаться по уже окончательной позе -
	/// иначе цепочка начинает каждый кадр от позы, которой в кадре не будет.
	///
	/// Молча выходит без физики или без нативного ozz: two-bone IK живёт в шиме, а луч - в мире.
	/// Это штатная деградация, и её видно в окне дебага счётчиком применённых ног.
	/// </summary>
	private void ApplyFootIk(Entity entity, Character character, float deltaSeconds)
	{
		if (character.Pose == null || Physics == null || !entity.HasComponent<FootIkComponent>())
		{
			character.LegsBuilt = false;
			character.Legs.Clear();
			return;
		}

		var settings = entity.GetComponent<FootIkComponent>();
		if (!settings.Enabled || settings.Weight <= 0f)
		{
			return;
		}

		if (!character.LegsBuilt || !SameLegSource(character.LegSource, settings))
		{
			BuildLegs(character, settings);
			character.LegSource = settings;
			character.LegsBuilt = true;
		}

		if (character.Legs.Count == 0)
		{
			return;
		}

		// Числовые ручки обновляются каждый кадр (их крутят ползунками во время проигрывания), а
		// состав ног - только при пересборке выше: пересборка роняет сглаживание, и стопа поехала бы
		// к земле заново на каждом кадре.
		character.IkSettings.Weight = Math.Clamp(settings.Weight, 0f, 1f);
		character.IkSettings.MaxPelvisDrop = settings.MaxPelvisDrop;
		character.IkSettings.Smoothing = settings.Smoothing;
		character.IkSettings.AlignToNormal = settings.AlignToNormal;
		character.IkSettings.LockFeet = settings.LockFeet;
		character.IkSettings.AlignBodyToSlope = settings.AlignBodyToSlope;

		foreach (var leg in character.Legs)
		{
			leg.AnkleHeight = settings.AnkleHeight;
		}

		var physics = Physics;

		character.IkApplied = FootIk.Solve(character.Pose, character.Skeleton, character.Legs,
			character.IkSettings, character.ModelToWorld, character.Locals, character.Models,
			(origin, direction, maximumT) => physics.SampleGround(origin, direction, maximumT),
			deltaSeconds);

		if (character.IkApplied)
		{
			character.Models.CopyTo(character.Managed.ModelMatrices, 0);
		}
	}

	/// <summary>
	/// Имя кости: заданное автором, а если оно пусто - из humanoid-разметки модели.
	///
	/// Приоритет у АВТОРА, и это не мелочь: разметка автоматическая, и на нестандартном риге она
	/// может ошибиться. Пустое поле означает «возьми из аватара», непустое - «я знаю лучше», и
	/// перебивать второе первым значило бы, что ручную настройку нельзя сделать в принципе.
	/// </summary>
	private static string JointOf(Character character, string authored, HumanoidBone slot) =>
		!string.IsNullOrEmpty(authored) ? authored : character.Avatar?[slot] ?? string.Empty;

	private static void BuildLegs(Character character, in FootIkComponent settings)
	{
		character.Legs.Clear();

		string pelvis = JointOf(character, settings.PelvisJoint, HumanoidBone.Hips);

		character.IkSettings.PelvisJoint = string.IsNullOrEmpty(pelvis)
			? -1
			: character.Skeleton.FindJoint(pelvis);

		// Дальность луча - в масштабе СКЕЛЕТА, а не в метрах: у лисы длина кости ~10 единиц, и луч
		// «полтора метра вниз» не дотянулся бы до пола, под которым она стоит. Это ровно тот случай,
		// когда абсолютная константа выглядит как «IK не работает».
		//
		// Старт - ВЫСОКО над стопой (3 длины кости; канонический сэмпл ozz берёт полметра): лапа на
		// ступенях и склонах уходит ВНУТРЬ рельефа, и луч, рождённый под поверхностью, пролетает
		// односторонний меш насквозь и находит «пол» этажом ниже - IK тянет ногу туда.
		character.IkSettings.ProbeUp = character.Scale * 3f;
		character.IkSettings.ProbeDown = character.Scale * 2f;

		// Носок - точка ОПОРЫ дигитиграда: у лисы «стопа» разметки - скакательный сустав, и без
		// носка IK щупал пол под суставом, висящим над землёй в стороне от места контакта.
		AddLeg(character,
			JointOf(character, settings.LeftUpperJoint, HumanoidBone.LeftUpperLeg),
			JointOf(character, settings.LeftLowerJoint, HumanoidBone.LeftLowerLeg),
			JointOf(character, settings.LeftFootJoint, HumanoidBone.LeftFoot),
			JointOf(character, settings.LeftToeJoint, HumanoidBone.LeftToes));

		AddLeg(character,
			JointOf(character, settings.RightUpperJoint, HumanoidBone.RightUpperLeg),
			JointOf(character, settings.RightLowerJoint, HumanoidBone.RightLowerLeg),
			JointOf(character, settings.RightFootJoint, HumanoidBone.RightFoot),
			JointOf(character, settings.RightToeJoint, HumanoidBone.RightToes),
			right: true);

		// Передние ноги четвероногого - из ARM-слотов разметки (лисе автомаппинг кладёт передние
		// ноги именно туда). Только по явной галочке: у двуногого те же слоты - его руки. Носок
		// передней ноги - только авторский: слота «пальцы кисти» в разметке нет.
		if (settings.FrontLegs)
		{
			AddLeg(character,
				JointOf(character, settings.FrontLeftUpperJoint, HumanoidBone.LeftUpperArm),
				JointOf(character, settings.FrontLeftLowerJoint, HumanoidBone.LeftLowerArm),
				JointOf(character, settings.FrontLeftFootJoint, HumanoidBone.LeftHand),
				settings.FrontLeftToeJoint,
				front: true);

			AddLeg(character,
				JointOf(character, settings.FrontRightUpperJoint, HumanoidBone.RightUpperArm),
				JointOf(character, settings.FrontRightLowerJoint, HumanoidBone.RightLowerArm),
				JointOf(character, settings.FrontRightFootJoint, HumanoidBone.RightHand),
				settings.FrontRightToeJoint,
				front: true, right: true);
		}
	}

	/// <summary>Добавляет ногу, если ВСЕ три кости нашлись. Частично настроенная нога не добавляется
	/// вовсе: two-bone IK с отсутствующим суставом - это не «чуть хуже», а обращение по индексу -1.
	/// Носок - ОПЦИОНАЛЬНЫЙ: без него точкой опоры служит сама стопа (двуногие).</summary>
	private static void AddLeg(Character character, string upper, string lower, string foot,
		string toe = "", bool front = false, bool right = false)
	{
		int upperJoint = character.Skeleton.FindJoint(upper ?? string.Empty);
		int lowerJoint = character.Skeleton.FindJoint(lower ?? string.Empty);
		int footJoint = character.Skeleton.FindJoint(foot ?? string.Empty);

		if (upperJoint < 0 || lowerJoint < 0 || footJoint < 0)
		{
			return;
		}

		character.Legs.Add(new FootIkLeg
		{
			UpperJoint = upperJoint,
			LowerJoint = lowerJoint,
			FootJoint = footJoint,
			ToeJoint = character.Skeleton.FindJoint(toe ?? string.Empty),
			Front = front,
			Right = right,
		});
	}

	private static bool SameLegSource(in FootIkComponent a, in FootIkComponent b) =>
		a.FrontLegs == b.FrontLegs &&
		string.Equals(a.PelvisJoint, b.PelvisJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftUpperJoint, b.LeftUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftLowerJoint, b.LeftLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftFootJoint, b.LeftFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightUpperJoint, b.RightUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightLowerJoint, b.RightLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightFootJoint, b.RightFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftUpperJoint, b.FrontLeftUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftLowerJoint, b.FrontLeftLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftFootJoint, b.FrontLeftFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightUpperJoint, b.FrontRightUpperJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightLowerJoint, b.FrontRightLowerJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightFootJoint, b.FrontRightFootJoint, StringComparison.Ordinal) &&
		string.Equals(a.LeftToeJoint, b.LeftToeJoint, StringComparison.Ordinal) &&
		string.Equals(a.RightToeJoint, b.RightToeJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontLeftToeJoint, b.FrontLeftToeJoint, StringComparison.Ordinal) &&
		string.Equals(a.FrontRightToeJoint, b.FrontRightToeJoint, StringComparison.Ordinal);

	// --- Spring bones ------------------------------------------------------------------------------

	private static void ApplySpringBones(Entity entity, Character character, float deltaSeconds)
	{
		if (!entity.HasComponent<SpringBoneComponent>())
		{
			character.ChainsBuilt = false;
			character.Chains.Clear();
			return;
		}

		var settings = entity.GetComponent<SpringBoneComponent>();
		if (!settings.Enabled || string.IsNullOrEmpty(settings.RootJoint) || settings.Length < 2)
		{
			return;
		}

		if (!character.ChainsBuilt || !SameChainSource(character.ChainSource, settings))
		{
			character.Chains.Clear();

			var joints = BuildChain(character.Skeleton, settings.RootJoint, settings.Length);
			if (joints.Length >= 2)
			{
				character.Chains.Add(new SpringBoneChain { Joints = joints });
			}

			character.ChainSource = settings;
			character.ChainsBuilt = true;
		}

		foreach (var chain in character.Chains)
		{
			// Числовые параметры обновляются каждый кадр (их крутят ползунками прямо во время
			// проигрывания), а вот СОСТАВ цепочки - только при пересборке выше, иначе инерция
			// сбрасывалась бы на каждом кадре.
			chain.Stiffness = settings.Stiffness;
			chain.Drag = settings.Drag;
			chain.TailLength = settings.TailLength;
			chain.Gravity = settings.Gravity;
		}

		SpringBones.Solve(character.Skeleton, character.Chains, character.Locals, character.Models, deltaSeconds);
		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	/// <summary>Совпадает ли СОСТАВ цепочки (корень и длина). Числовые параметры сюда не входят
	/// намеренно - их правка не должна ронять инерцию.</summary>
	private static bool SameChainSource(in SpringBoneComponent a, in SpringBoneComponent b) =>
		string.Equals(a.RootJoint, b.RootJoint, StringComparison.Ordinal) && a.Length == b.Length;

	/// <summary>
	/// Собирает цепочку от корневой кости вниз ПО ПЕРВОМУ РЕБЁНКУ. Первый ребёнок, а не «все дети»:
	/// цепочка вторичного движения по определению линейна, а у кости с развилкой (основание хвоста,
	/// от которого отходят ещё и ноги) взять всех детей значило бы утащить в цепочку пол-скелета.
	/// </summary>
	private static int[] BuildChain(PreparedSkeleton skeleton, string rootName, int length)
	{
		int root = skeleton.FindJoint(rootName);
		if (root < 0)
		{
			return [];
		}

		var chain = new List<int> { root };
		int current = root;

		while (chain.Count < length)
		{
			int child = FirstChild(skeleton, current);
			if (child < 0)
			{
				break;
			}

			chain.Add(child);
			current = child;
		}

		return chain.ToArray();
	}

	/// <summary>Первый ребёнок джойнта, -1 если лист. Джойнты топологически упорядочены, поэтому
	/// дети лежат ПОСЛЕ родителя, и первый найденный - он и есть первый ребёнок.</summary>
	private static int FirstChild(PreparedSkeleton skeleton, int joint)
	{
		for (int i = joint + 1; i < skeleton.JointCount; i++)
		{
			if (skeleton.Parents[i] == joint)
			{
				return i;
			}
		}

		return -1;
	}

	// --- Рэгдолл -----------------------------------------------------------------------------------

	/// <summary>
	/// Ведёт рэгдолл персонажа: собирает и разбирает его по компоненту, гонит тела к позе анимации и
	/// - в физическом режиме - читает позу обратно из тел.
	///
	/// Идёт ПОСЛЕДНЕЙ стадией: рэгдолл либо получает готовую позу как цель, либо целиком её
	/// заменяет, и обе роли требуют, чтобы поза к этому моменту была окончательной.
	/// </summary>
	private void SyncRagdoll(Entity entity, Character character, float deltaSeconds)
	{
		bool wanted = Physics != null && entity.HasComponent<RagdollComponent>();
		var settings = wanted ? entity.GetComponent<RagdollComponent>() : default;

		// Хит-реакция живёт ПОВЕРХ компонента: у идущего персонажа рэгдолл авторски выключен (и
		// FallRecover гасит его каждый кадр), а реагировать на удар он обязан всё равно. Конверт
		// тикает здесь же - реакция без единого кадра рэгдолла обязана истечь, а не висеть вечно.
		bool reacting = wanted && character.ReactionDuration > 0f;
		if (reacting)
		{
			character.ReactionElapsed += deltaSeconds;
			if (character.ReactionElapsed >= character.ReactionDuration)
			{
				character.ReactionDuration = 0f;
				character.ReactionImpulsePending = false;
				reacting = false;
			}
		}

		character.ReactionWeight = 0f;
		character.ReactionDeviation = 0f;

		if (!wanted || (!settings.Enabled && !reacting))
		{
			DestroyRagdoll(character);
			return;
		}

		float worldScale = WorldScaleOf(character.ModelToWorld);

		if (!character.RagdollBuilt || !SameRagdollSource(character.RagdollSource, settings) ||
			!SameScale(character.RagdollBuildScale, worldScale))
		{
			DestroyRagdoll(character);
			BuildRagdoll(character, settings);

			character.RagdollSource = settings;
			character.RagdollBuildScale = worldScale;
			character.RagdollBuilt = true;
		}

		var ragdoll = character.Ragdoll;
		if (ragdoll == null)
		{
			return;
		}

		ActiveRagdollCount++;

		// Цель - поза анимации В МИРЕ. Считается ДО чтения из тел: в физическом режиме чтение
		// затрёт character.Models, а сервоприводам нужна именно анимационная цель.
		for (int i = 0; i < character.Models.Length; i++)
		{
			character.JointWorld[i] = character.Models[i] * character.ModelToWorld;
		}

		// Реакция переводит тела в физику с СИЛЬНЫМИ сервоприводами: они тянут корпус обратно к
		// анимации, и толчок читается как «качнулся и выправился», а не «обмяк». Настоящее падение
		// (Physical по компоненту) сильнее реакции: там поза целиком из тел, и подмешивать нечего.
		bool reactionDrives = reacting && !settings.Physical;

		ragdoll.SetAnimationDriven(!settings.Physical && !reactionDrives);
		ragdoll.DriveToPose(character.JointWorld, deltaSeconds,
			reactionDrives ? ReactionServoStrength : settings.ServoStrength);

		if (reactionDrives && character.ReactionImpulsePending)
		{
			EnsureReactionMask(character);
			ragdoll.AddVelocity(character.ReactionImpulse, character.ReactionMask);
			character.ReactionImpulsePending = false;
		}

		if (settings.Physical)
		{
			ReadRagdollPose(character, ragdoll);
		}
		else if (reactionDrives)
		{
			BlendReactionPose(character, ragdoll);
		}
	}

	/// <summary>Сила сервоприводов реакции. Порядок величины - как у демонстрационного active
	/// ragdoll (60): достаточно, чтобы корпус вернулся к анимации за доли секунды, и мало,
	/// чтобы толчок вообще был виден.</summary>
	private const float ReactionServoStrength = 60f;

	/// <summary>
	/// Подмешивает позу тел к анимации по маске и конверту. Ноги в маске нулевые - они продолжают
	/// идти анимацией (и foot IK уже отработал по ней); смешиваются РАЗЛОЖЕННЫЕ TRS по той же
	/// причине, что и в подъёме: интерполяция матриц поворота напрямую плющит кости на полпути.
	/// </summary>
	private static void BlendReactionPose(Character character, Ragdoll ragdoll)
	{
		EnsureReactionMask(character);

		if (character.ReactionAnimated.Length != character.Models.Length)
		{
			character.ReactionAnimated = new Matrix4x4[character.Models.Length];
		}

		character.Models.CopyTo(character.ReactionAnimated, 0);
		ReadRagdollPose(character, ragdoll);

		// Конверт: быстрая атака (толчок обязан быть виден сразу) и плавный спад до конца реакции.
		float t = Math.Clamp(character.ReactionElapsed / character.ReactionDuration, 0f, 1f);
		float attack = Math.Clamp(character.ReactionElapsed / ReactionAttackSeconds, 0f, 1f);
		float release = 1f - t * t * (3f - 2f * t);
		float envelope = character.ReactionStrength * attack * release;

		character.ReactionWeight = envelope;

		float deviation = 0f;

		for (int i = 0; i < character.Models.Length; i++)
		{
			float weight = envelope * character.ReactionMask[i];
			var animated = character.ReactionAnimated[i];

			if (weight <= 1e-4f)
			{
				character.Models[i] = animated;
				continue;
			}

			if (!Matrix4x4.Decompose(character.Models[i], out var scale, out var rotation, out var translation) ||
				!Matrix4x4.Decompose(animated, out var animScale, out var animRotation, out var animTranslation))
			{
				character.Models[i] = animated;
				continue;
			}

			character.Models[i] = MathUtils.CreateTrs(
				Vector3.Lerp(animTranslation, translation, weight),
				Quaternion.Slerp(animRotation, rotation, weight),
				Vector3.Lerp(animScale, scale, weight));

			deviation = MathF.Max(deviation,
				Vector3.Distance(character.Models[i].Translation, animTranslation));
		}

		character.ReactionDeviation = deviation;
		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	/// <summary>
	/// Маска реакции по humanoid-разметке: конечности (все шесть цепочек слотов и их поддеревья)
	/// нулевые, таз приглушён (его качает и так - через корпус), остальное единица. Без разметки
	/// маска целиком единичная - реакция честно качает всего персонажа, что хуже, но видно.
	/// </summary>
	private static void EnsureReactionMask(Character character)
	{
		if (character.ReactionMaskBuilt && character.ReactionMask.Length == character.Skeleton.JointCount)
		{
			return;
		}

		int count = character.Skeleton.JointCount;
		character.ReactionMask = new float[count];
		Array.Fill(character.ReactionMask, 1f);

		if (character.Avatar != null)
		{
			ReadOnlySpan<HumanoidBone> limbs =
			[
				HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand,
				HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, HumanoidBone.RightHand,
				HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot,
				HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot,
			];

			foreach (var slot in limbs)
			{
				int joint = character.Skeleton.FindJoint(character.Avatar[slot] ?? string.Empty);
				if (joint >= 0)
				{
					character.ReactionMask[joint] = 0f;
				}
			}

			int hips = character.Skeleton.FindJoint(character.Avatar[HumanoidBone.Hips] ?? string.Empty);
			if (hips >= 0)
			{
				character.ReactionMask[hips] = 0.3f;
			}

			// Поддеревья обнулённых костей (пальцы под кистью): джойнты топологически упорядочены,
			// одного прохода хватает. Нулевой РОДИТЕЛЬ обнуляет ребёнка - но только нулевой:
			// приглушённый таз своих детей не глушит, ноги обнулены явно, а корпус растёт из него
			// с полным весом.
			var parents = character.Skeleton.Parents;
			for (int i = 0; i < count; i++)
			{
				if (parents[i] >= 0 && character.ReactionMask[parents[i]] == 0f)
				{
					character.ReactionMask[i] = 0f;
				}
			}
		}

		character.ReactionMaskBuilt = true;
	}

	/// <summary>
	/// Переносит позу из тел рэгдолла обратно в пространство модели.
	///
	/// Джойнты, у которых тела НЕТ (пальцы, кости хвоста, всё, что глубже MaxDepth), пересчитываются
	/// от родителя по локальной TRS. Без этого они остались бы там, где их оставила анимация, -
	/// то есть у лежащего персонажа кисти висели бы в воздухе на месте стоящей позы. Один проход по
	/// массиву достаточен: джойнты топологически упорядочены, родитель к моменту обработки ребёнка
	/// уже посчитан.
	/// </summary>
	private static void ReadRagdollPose(Character character, Ragdoll ragdoll)
	{
		if (!Matrix4x4.Invert(character.ModelToWorld, out var worldToModel))
		{
			return;
		}

		character.JointWorld.CopyTo(character.RagdollWorld, 0);
		ragdoll.ReadPose(character.RagdollWorld);

		Array.Clear(character.RagdollOwned);
		for (int i = 0; i < ragdoll.BoneCount; i++)
		{
			character.RagdollOwned[ragdoll.JointOf(i)] = true;
		}

		var parents = character.Skeleton.Parents;

		// Поза тела Bepu ЖЁСТКАЯ - поворот и позиция, масштаб единичный, - а worldToModel несёт
		// ОБРАТНЫЙ масштаб сущности. Голое произведение RagdollWorld * worldToModel даёт модельную
		// матрицу с масштабом 1/scale в линейной части: позиция кости переводится в модельные
		// единицы правильно, но каждый привязанный к кости офсет вершины раздувается в те же 1/scale
		// раз. При масштабе лисы 0.01 это персонаж, разорванный в СТО раз (замерено headless-прогоном
		// сцены: деформированный габарит 9501 при bind 175, и уже на ПЕРВОМ кадре физики - это не
		// разлёт симуляции, а чистая ошибка пространства). Домножение на масштаб слева гасит его в
		// линейной части, не трогая перевод позиции: строка трансляции у скейл-матрицы единичная.
		var counterScale = Matrix4x4.CreateScale(WorldScaleOf(character.ModelToWorld));

		for (int i = 0; i < character.Models.Length; i++)
		{
			if (character.RagdollOwned[i])
			{
				character.Models[i] = counterScale * character.RagdollWorld[i] * worldToModel;
				continue;
			}

			var local = character.Locals[i];
			// Полным именем: MathUtils есть в нескольких пространствах имён движка, и короткое имя
			// разрешается не в то.
			var localMatrix = MathUtils.CreateTrs(
				local.position, local.rotation, local.scale);

			character.Models[i] = parents[i] >= 0
				? localMatrix * character.Models[parents[i]]
				: localMatrix;
		}

		character.Models.CopyTo(character.Managed.ModelMatrices, 0);
	}

	private static void DestroyRagdoll(Character character)
	{
		character.Ragdoll?.Destroy();
		character.Ragdoll = null;
		character.RagdollBuilt = false;
	}

	/// <summary>Совпадает ли СТРОЕНИЕ рэгдолла. Physical и ServoStrength сюда не входят: это ручки
	/// режима, и пересобирать на них тела значило бы ронять персонажа заново на каждом кадре, пока
	/// ползунок силы сервоприводов под курсором.</summary>
	/// <summary>Средний масштаб трансформа - длина осей его линейной части. Средний, а не покомпонентный:
	/// рэгдолл всё равно строится изотропным (капсула Bepu не умеет неравномерного масштаба), и
	/// сравнивать по осям значило бы обещать точность, которой в сборке нет.</summary>
	private static float WorldScaleOf(in Matrix4x4 transform)
	{
		float x = new Vector3(transform.M11, transform.M12, transform.M13).Length();
		float y = new Vector3(transform.M21, transform.M22, transform.M23).Length();
		float z = new Vector3(transform.M31, transform.M32, transform.M33).Length();

		return (x + y + z) / 3f;
	}

	/// <summary>Сравнение масштабов ОТНОСИТЕЛЬНОЕ и с мёртвой зоной. Точное сравнение здесь недопустимо:
	/// масштаб приезжает из разложения матрицы, его младшие разряды шумят на уровне 1e-7, и рэгдолл
	/// пересобирался бы каждый кадр - то есть персонаж падал бы заново на каждом кадре, ни разу не
	/// успев упасть.</summary>
	private static bool SameScale(float a, float b) =>
		MathF.Abs(a - b) <= 1e-3f * MathF.Max(MathF.Abs(a), MathF.Abs(b));

	private static bool SameRagdollSource(in RagdollComponent a, in RagdollComponent b) =>
		string.Equals(a.RootJoint, b.RootJoint, StringComparison.Ordinal) &&
		a.MaxDepth == b.MaxDepth && a.BoneRadius == b.BoneRadius && a.TotalMass == b.TotalMass;

	private void BuildRagdoll(Character character, in RagdollComponent settings)
	{
		if (Physics == null)
		{
			return;
		}

		var description = BuildRagdollDescription(character, settings, WorldScaleOf(character.ModelToWorld));
		if (description.Count < 2)
		{
			// Рэгдолл из одной кости - это не рэгдолл, а падающая капсула. Молча его не собираем:
			// собранный он выглядел бы как «работает», и разбираться, почему персонаж не гнётся,
			// пришлось бы в физике, а не в имени корневой кости.
			return;
		}

		for (int i = 0; i < character.Models.Length; i++)
		{
			character.JointWorld[i] = character.Models[i] * character.ModelToWorld;
		}

		MarkHingeBones(character, description);

		character.Ragdoll = Ragdoll.Build(Physics.World,
			System.Runtime.InteropServices.CollectionsMarshal.AsSpan(description), character.JointWorld);
	}

	/// <summary>
	/// Колени и локти по humanoid-разметке становятся ШАРНИРАМИ (см.
	/// <see cref="RagdollBoneDesc.HingeAxisWorld"/>): ball-socket с конусом разрешает согнуть их
	/// назад, и упавший персонаж заламывает конечности, не нарушая ни одного предела. Ось и диапазон
	/// считает <see cref="Ragdoll.MarkHinge"/> из позы сборки; без разметки (или с прямой в момент
	/// сборки конечностью) сустав остаётся конусным - хуже, но не сломано.
	/// </summary>
	private static void MarkHingeBones(Character character, List<RagdollBoneDesc> description)
	{
		if (character.Avatar == null)
		{
			return;
		}

		ReadOnlySpan<HumanoidBone> hinges =
		[
			HumanoidBone.LeftLowerLeg, HumanoidBone.RightLowerLeg,
			HumanoidBone.LeftLowerArm, HumanoidBone.RightLowerArm,
		];

		foreach (var slot in hinges)
		{
			int joint = character.Skeleton.FindJoint(character.Avatar[slot] ?? string.Empty);
			if (joint < 0)
			{
				continue;
			}

			for (int i = 0; i < description.Count; i++)
			{
				var bone = description[i];
				if (bone.Joint != joint || bone.Parent < 0 || bone.ChildJoint < 0)
				{
					continue;
				}

				// «Верх» - джойнт РОДИТЕЛЬСКОЙ КОСТИ РЭГДОЛЛА, а не родительский джойнт скелета:
				// шарнир связывает именно эти два тела, и ось из пропущенного звена была бы осью
				// не того сустава.
				Ragdoll.MarkHinge(ref bone,
					character.JointWorld[description[bone.Parent].Joint].Translation,
					character.JointWorld[bone.Joint].Translation,
					character.JointWorld[bone.ChildJoint].Translation);

				description[i] = bone;
				break;
			}
		}
	}

	/// <summary>
	/// Строит описание рэгдолла обходом скелета от корневой кости вглубь до <c>MaxDepth</c>. Костью
	/// рэгдолла становится каждый посещённый джойнт, У КОТОРОГО ЕСТЬ РЕБЁНОК: концевые джойнты
	/// (кончики пальцев, макушка) задают только длину родительской капсулы и своего тела не
	/// получают - иначе у персонажа выросли бы висящие ни на чём обрубки.
	///
	/// Автоматика здесь допустима ровно потому, что глубину задаёт автор: это его способ сказать
	/// «дальше кости служебные». Полный обход рига дал бы двести тел вместо двадцати.
	/// </summary>
	private static List<RagdollBoneDesc> BuildRagdollDescription(Character character,
		in RagdollComponent settings, float worldScale)
	{
		var result = new List<RagdollBoneDesc>();
		var skeleton = character.Skeleton;

		// Корень рэгдолла - заданный автором, иначе таз из humanoid-разметки, иначе просто корень
		// скелета. Последнее - именно фолбэк, а не выбор: у рига со служебным корнем («Armature»)
		// рэгдолл от него получит лишнее звено, но это лучше, чем не собраться вовсе.
		string rootName = JointOf(character, settings.RootJoint, HumanoidBone.Hips);
		int root = string.IsNullOrEmpty(rootName) ? 0 : skeleton.FindJoint(rootName);

		if (root < 0)
		{
			return result;
		}

		// Радиус капсулы каждой кости - ИЗ МЕША: средневзвешенное расстояние привязанных к джойнту
		// вершин до оси кости. Один радиус на весь скелет (прежняя схема) не соответствует телу по
		// построению: туловище лисы втрое толще лапы, и капсулы либо тонут в туловище (персонаж
		// лежит наполовину В полу - замерено: таз на y=0.018 при видимой толщине корпуса ~0.15 м),
		// либо распирают лапы. Авторское BoneRadius > 0 остаётся принудительным override на весь
		// скелет - под риги без скин-стрима и под намеренную стилизацию.
		float authoredRadius = settings.BoneRadius;
		var meshRadii = authoredRadius > 0f ? [] : MeasureBoneRadii(character);

		// Радиусы - в единицах МОДЕЛИ (и мешевые, и авторский: автор видит скелет в них же), в мир
		// переводятся масштабом сущности. Длины костей приезжают из мировых матриц джойнтов, то есть
		// уже отмасштабированными. Фолбэк - доля характерного размера скелета: масштаб моделей
		// произволен, и любая константа осмысленна ровно для одного из них.
		float RadiusOf(int joint)
		{
			if (authoredRadius > 0f)
			{
				return authoredRadius * worldScale;
			}

			float measured = joint < meshRadii.Length ? meshRadii[joint] : 0f;
			return (measured > 1e-4f ? measured : character.Scale * 0.12f) * worldScale;
		}

		// Индекс кости рэгдолла по джойнту - чтобы найти РОДИТЕЛЬСКУЮ КОСТЬ, а не родительский
		// джойнт: между двумя костями рэгдолла обычно есть пропущенные звенья скелета.
		var boneOfJoint = new Dictionary<int, int>();

		var queue = new Queue<(int Joint, int Depth, int ParentBone)>();
		queue.Enqueue((root, 0, -1));

		while (queue.Count > 0)
		{
			var (joint, depth, parentBone) = queue.Dequeue();

			int child = FirstChild(skeleton, joint);
			int bone = parentBone;

			if (child >= 0)
			{
				bone = result.Count;
				boneOfJoint[joint] = bone;

				result.Add(new RagdollBoneDesc
				{
					Joint = joint,
					ChildJoint = child,
					Parent = parentBone,
					Radius = RadiusOf(joint),

					// Запасная длина концевой кости - тоже в мире: её берут капсулы джойнтов без
					// ребёнка (голова, кисть), и в пространстве модели она была бы в разы длиннее.
					Length = character.Scale * worldScale,

					// Предел отклонения в суставе - не жёсткий и не свободный: 120 градусов размаха
					// не мешают конечности лечь естественно, но не дают ей вывернуться назад через
					// сустав, из-за чего рэгдолл выглядит сломанным, а не мёртвым.
					SwingLimitCos = -0.5f,

					// Скручивание - ДРУГАЯ степень свободы, конусом не ограниченная вовсе: без этого
					// предела кость проворачивается вокруг себя на любой угол, формально оставаясь
					// внутри конуса, и лапа выглядит вывернутой. 50° - примерно предел живого сустава
					// на звено; ровно столько и остаётся, если не гнаться за анатомией конкретного
					// рига, которой у произвольной модели всё равно нет.
					TwistLimitAngle = 50f * (MathF.PI / 180f),
				});
			}

			if (depth >= settings.MaxDepth)
			{
				continue;
			}

			for (int i = joint + 1; i < skeleton.JointCount; i++)
			{
				if (skeleton.Parents[i] == joint)
				{
					queue.Enqueue((i, depth + 1, bone));
				}
			}
		}

		DistributeMass(result, settings.TotalMass);
		return result;
	}

	/// <summary>
	/// Толщина каждой кости ПО МЕШУ: средневзвешенное перпендикулярное расстояние от привязанных к
	/// джойнту вершин до оси кости (джойнт → первый ребёнок), в единицах модели, в bind-позе.
	///
	/// Средневзвешенное, а не максимум: вершины лежат НА поверхности части тела, и их средняя
	/// дистанция до оси - это и есть её радиус; максимум цеплял бы вершины смежных частей, слабо
	/// привязанные к кости на стыке. Влияния легче 0.3 не считаются вовсе - вершина стыка, поровну
	/// разделённая между двумя костями, говорит о толщине обеих хуже, чем «своя» вершина о своей.
	/// </summary>
	private static unsafe float[] MeasureBoneRadii(Character character)
	{
		var skeleton = character.Skeleton;
		int count = skeleton.JointCount;

		// Модельные матрицы bind-позы. Managed-поза не годится: к моменту пересборки рэгдолла в ней
		// уже текущий кадр клипа, и радиусы гуляли бы от позы к позе.
		var bind = new Matrix4x4[count];
		for (int i = 0; i < count; i++)
		{
			var local = skeleton.BindLocals[i];
			var matrix = MathUtils.CreateTrs(
				local.position, local.rotation, local.scale);
			bind[i] = skeleton.Parents[i] >= 0 ? matrix * bind[skeleton.Parents[i]] : matrix;
		}

		var sum = new float[count];
		var weight = new float[count];
		var model = character.Model;

		void Accumulate(int joint, ushort rawWeight, Vector3 position)
		{
			float w = rawWeight / SkinVertex.WeightScale;
			if (w < 0.3f || joint >= count)
			{
				return;
			}

			var start = bind[joint].Translation;
			int child = FirstChild(skeleton, joint);
			var end = child >= 0 ? bind[child].Translation : start;

			var axis = end - start;
			float lengthSq = axis.LengthSquared();
			float t = lengthSq > 1e-8f
				? Math.Clamp(Vector3.Dot(position - start, axis) / lengthSq, 0f, 1f)
				: 0f;

			sum[joint] += Vector3.Distance(position, start + axis * t) * w;
			weight[joint] += w;
		}

		for (int meshIndex = 0; meshIndex < model.Meshes.Count; meshIndex++)
		{
			var skinStream = meshIndex < model.MeshSkin.Count ? model.MeshSkin[meshIndex] : null;
			var mesh = model.Meshes[meshIndex];

			if (skinStream == null || mesh.VertexData == null)
			{
				continue;
			}

			int vertexCount = Math.Min(
				UnsafeCollections.Collections.Unsafe.UnsafeArray.GetLength(mesh.VertexData),
				skinStream.Length);
			var vertices = new ReadOnlySpan<Vertex>(
				UnsafeCollections.Collections.Unsafe.UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0),
				vertexCount);

			for (int v = 0; v < vertexCount; v++)
			{
				var s = skinStream[v];
				var position = vertices[v].Position;

				Accumulate(s.J0, s.W0, position);
				Accumulate(s.J1, s.W1, position);
				Accumulate(s.J2, s.W2, position);
				Accumulate(s.J3, s.W3, position);
			}
		}

		var radii = new float[count];
		for (int i = 0; i < count; i++)
		{
			radii[i] = weight[i] > 0f ? sum[i] / weight[i] : 0f;
		}

		return radii;
	}

	/// <summary>Раскладывает общую массу по костям пропорционально ОБЪЁМУ капсулы. Поровну нельзя:
	/// голова весила бы столько же, сколько таз, и персонаж падал бы, кувыркаясь через голову.</summary>
	private static void DistributeMass(List<RagdollBoneDesc> bones, float totalMass)
	{
		if (bones.Count == 0)
		{
			return;
		}

		float mass = totalMass > 0f ? totalMass : 70f;
		float sum = 0f;

		Span<float> volumes = bones.Count <= 64 ? stackalloc float[bones.Count] : new float[bones.Count];

		for (int i = 0; i < bones.Count; i++)
		{
			float radius = MathF.Max(bones[i].Radius, 1e-4f);
			volumes[i] = radius * radius * MathF.Max(bones[i].Length, radius);
			sum += volumes[i];
		}

		for (int i = 0; i < bones.Count; i++)
		{
			var bone = bones[i];
			bone.Mass = sum > 0f ? mass * (volumes[i] / sum) : mass / bones.Count;
			bones[i] = bone;
		}
	}

	// --- Дебаг -------------------------------------------------------------------------------------

	/// <summary>Сводка по персонажу для окна дебага. Плоская структура, а не ссылка на
	/// <see cref="Character"/>: окно не должно уметь трогать состояние драйвера, а список персонажей
	/// оно перечитывает каждый кадр.</summary>
	public readonly record struct CharacterInfo(
		int EntityId,
		string Clip,
		float Time,
		bool Playing,
		int JointCount,
		int LegCount,
		bool IkApplied,
		int ChainCount,
		int RagdollBones,
		bool RagdollPhysical,
		bool Locomotion,
		float LocoSpeed,
		float LocoIdleWeight,
		float LocoWalkWeight,
		float LocoRunWeight,
		float LocoWalkPhaseOffset,
		float LocoRunPhaseOffset,
		float ReactionWeight,
		float ReactionDeviation,
		float LocoWalkStride,
		float LocoRunStride);

	public void DescribeCharacters(List<CharacterInfo> result)
	{
		result.Clear();

		foreach (var pair in _characters)
		{
			var character = pair.Value;

			result.Add(new CharacterInfo(
				pair.Key,
				string.IsNullOrEmpty(character.AppliedClip) ? "(bind)" : character.AppliedClip,
				character.Player.Time,
				character.Player.Clip != null,
				character.Skeleton.JointCount,
				character.Legs.Count,
				character.IkApplied,
				character.Chains.Count,
				character.Ragdoll?.BoneCount ?? 0,
				character.Ragdoll != null && !character.Ragdoll.IsAnimationDriven,
				character.LocoActive,
				character.LocoSpeed,
				character.LocoIdleWeight,
				character.LocoWalkWeight,
				character.LocoRunWeight,
				character.LocoWalkPhaseOffset,
				character.LocoRunPhaseOffset,
				character.ReactionWeight,
				character.ReactionDeviation,
				character.LocoWalkStride,
				character.LocoRunStride));
		}
	}

	/// <summary>
	/// Связи рэгдолла: отрезок между центрами связанных тел и точка крепления сустава. Живёт здесь, а
	/// не в <see cref="PhysicsDebugDraw"/>, потому что по симуляции связь рэгдолла неотличима от
	/// любой другой: решатель Bepu хранит их плоским списком без понятия «это рэгдолл вот этого
	/// персонажа». Знание о структуре есть только у того, кто её строил.
	/// </summary>
	public void DrawRagdollJoints(DebugDraw draw, bool onTop)
	{
		if (draw is not { Enabled: true })
		{
			return;
		}

		foreach (var character in _characters.Values)
		{
			var ragdoll = character.Ragdoll;
			if (ragdoll == null)
			{
				continue;
			}

			for (int bone = 0; bone < ragdoll.BoneCount; bone++)
			{
				var pose = ragdoll.PoseOf(bone);
				int parent = ragdoll.ParentOf(bone);

				// Центр тела - крестом: расхождение центра капсулы с суставом скелета и есть тот
				// самый перевод «джойнт -> тело», в котором чаще всего ошибаются.
				draw.Cross(pose.Position, character.Scale * 0.1f, DebugColor.White, onTop);

				if (parent >= 0)
				{
					draw.Line(ragdoll.PoseOf(parent).Position, pose.Position, DebugColor.Yellow, onTop);
				}
			}
		}
	}

	/// <summary>Средняя длина кости скелета - характерный размер модели. По ней масштабируется весь
	/// дебаг и дальность лучей IK; см. <see cref="Character.Scale"/>.</summary>
	private static float MeasureScale(PreparedSkeleton skeleton)
	{
		var pose = new SkeletonPose(skeleton);
		pose.ComputeModelMatrices();

		float sum = 0f;
		int count = 0;

		for (int i = 0; i < skeleton.JointCount; i++)
		{
			int parent = skeleton.Parents[i];
			if (parent < 0)
			{
				continue;
			}

			sum += Vector3.Distance(pose.ModelMatrices[i].Translation, pose.ModelMatrices[parent].Translation);
			count++;
		}

		return count > 0 && sum > 1e-5f ? sum / count : 1f;
	}

	private void DrawDebug(Character character)
	{
		var draw = Debug;
		if (draw is not { Enabled: true })
		{
			return;
		}

		DrawHighlight(draw, character);

		var options = DebugOptions;
		if (!options.AnyEnabled)
		{
			return;
		}

		bool onTop = options.OnTop;
		var toWorld = character.ModelToWorld;

		if (options.BindPose)
		{
			DrawBindPose(draw, character, onTop);
		}

		if (options.Skeleton || options.JointAxes || options.JointNames)
		{
			DrawSkeleton(draw, character, options, onTop);
		}

		if (options.SpringChains)
		{
			DrawChains(draw, character, onTop);
		}

		if (options.LookAt && character.HasLookAt)
		{
			var target = Vector3.Transform(character.LookAtTarget, toWorld);
			draw.Cross(target, character.Scale * 0.3f, DebugColor.Magenta, onTop);

			if (character.LookAtJoint >= 0)
			{
				var joint = Vector3.Transform(character.Models[character.LookAtJoint].Translation, toWorld);
				draw.Line(joint, target, DebugColor.Dim(DebugColor.Magenta), onTop);
			}
		}

		if (options.FootIk)
		{
			DrawFootIk(draw, character, onTop);
		}
	}

	/// <summary>Подсветка кости, выбранной в окне Humanoid: крест, оси и подпись. Оси - не
	/// украшение: при разметке рига важно не только «эта ли кость», но и «куда она смотрит», потому
	/// что от этого зависит вся дальнейшая работа с ней.</summary>
	private void DrawHighlight(DebugDraw draw, Character character)
	{
		if (string.IsNullOrEmpty(HighlightJoint))
		{
			return;
		}

		int joint = character.Skeleton.FindJoint(HighlightJoint);
		if (joint < 0)
		{
			return;
		}

		var world = character.Models[joint] * character.ModelToWorld;

		draw.Cross(world.Translation, character.Scale * 0.5f, DebugColor.Magenta, onTop: true);
		draw.Axes(world, character.Scale * 0.6f, onTop: true);
		draw.Label(world.Translation, HighlightJoint, DebugColor.Magenta);
	}

	private static void DrawSkeleton(DebugDraw draw, Character character, in AnimationDebugOptions options,
		bool onTop)
	{
		var toWorld = character.ModelToWorld;
		var parents = character.Skeleton.Parents;

		for (int i = 0; i < character.Models.Length; i++)
		{
			var world = character.Models[i] * toWorld;

			if (options.Skeleton)
			{
				int parent = parents[i];
				if (parent >= 0)
				{
					var from = Vector3.Transform(character.Models[parent].Translation, toWorld);
					// Цвет кодирует источник позы: кость, которой управляет физика, - оранжевая, как
					// динамическое тело в дебаге физики. Так на одном экране видно, где кончается
					// анимация и начинается рэгдолл.
					var color = character.RagdollOwned.Length > i && character.RagdollOwned[i]
						? DebugColor.Orange
						: DebugColor.Cyan;

					draw.Bone(from, world.Translation, color, 0.12f, onTop);
				}
				else
				{
					draw.Cross(world.Translation, character.Scale * 0.25f, DebugColor.Yellow, onTop);
				}
			}

			if (options.JointAxes)
			{
				draw.Axes(world, character.Scale * 0.35f, onTop);
			}

			if (options.JointNames)
			{
				draw.Label(world.Translation, character.Skeleton.JointNames[i], DebugColor.White);
			}
		}
	}

	/// <summary>Bind-поза приглушённым серым. Считается на месте, а не хранится: рисуется она под
	/// вопрос «применилась ли поза вообще», то есть редко, а массив матриц на каждого персонажа
	/// жил бы всегда.</summary>
	private static void DrawBindPose(DebugDraw draw, Character character, bool onTop)
	{
		var pose = new SkeletonPose(character.Skeleton);
		pose.ComputeModelMatrices();

		var toWorld = character.ModelToWorld;
		var parents = character.Skeleton.Parents;

		for (int i = 0; i < pose.ModelMatrices.Length; i++)
		{
			int parent = parents[i];
			if (parent < 0)
			{
				continue;
			}

			var from = Vector3.Transform(pose.ModelMatrices[parent].Translation, toWorld);
			var to = Vector3.Transform(pose.ModelMatrices[i].Translation, toWorld);

			draw.Line(from, to, DebugColor.Dim(DebugColor.Grey, 0.7f), onTop);
		}
	}

	private static void DrawChains(DebugDraw draw, Character character, bool onTop)
	{
		var toWorld = character.ModelToWorld;

		foreach (var chain in character.Chains)
		{
			for (int i = 1; i < chain.Joints.Length; i++)
			{
				var from = Vector3.Transform(character.Models[chain.Joints[i - 1]].Translation, toWorld);
				var to = Vector3.Transform(character.Models[chain.Joints[i]].Translation, toWorld);

				draw.Line(from, to, DebugColor.Green, onTop);
				draw.Cross(to, character.Scale * 0.12f, DebugColor.Green, onTop);
			}
		}
	}

	/// <summary>Стопы: сустав и то, чем он стал после IK. Сами ЛУЧИ рисует дебаг физики - они
	/// принадлежат миру, а не скелету, и записываются там же, где пускаются (см. ScenePhysics).</summary>
	private static void DrawFootIk(DebugDraw draw, Character character, bool onTop)
	{
		var toWorld = character.ModelToWorld;
		var color = character.IkApplied ? DebugColor.Yellow : DebugColor.Red;

		foreach (var leg in character.Legs)
		{
			var upper = Vector3.Transform(character.Models[leg.UpperJoint].Translation, toWorld);
			var lower = Vector3.Transform(character.Models[leg.LowerJoint].Translation, toWorld);
			var foot = Vector3.Transform(character.Models[leg.FootJoint].Translation, toWorld);

			draw.Line(upper, lower, color, onTop);
			draw.Line(lower, foot, color, onTop);
			draw.Cross(foot, character.Scale * 0.2f, color, onTop);

			// Точка опоры: у дигитиграда это носок, и плюсна дорисовывается к цепочке.
			var contact = foot;
			if (leg.ToeJoint >= 0)
			{
				contact = Vector3.Transform(character.Models[leg.ToeJoint].Translation, toWorld);
				draw.Line(foot, contact, color, onTop);
			}

			// Подошва: сустав опоры минус его высота. Именно её IK сажает на поверхность, и именно
			// её расхождение с точкой попадания луча объясняет утопленную или висящую стопу.
			var sole = contact - Vector3.TransformNormal(Vector3.UnitY * leg.AnkleHeight, toWorld);
			draw.Line(contact, sole, DebugColor.Dim(color), onTop);
		}
	}
}
