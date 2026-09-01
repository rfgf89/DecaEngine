using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Animation;
using DecaEngine.Core;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Physics;
using DecaEngine.Scene;
using Friflo.Engine.ECS;

// В Friflo есть свой Transform-компонент, а поза скелета оперирует TRS движка - без явного алиаса
// имя разрешается неоднозначно.
using Transform = DecaEngine.Core.Transform;

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
public sealed partial class AnimationDriver : IDisposable
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

}
