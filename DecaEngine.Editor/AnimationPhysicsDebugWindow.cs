using Engine.ImGui.Core;
using Hexa.NET.ImGui;

namespace DecaEngine.Editor;

/// <summary>
/// Окно отладки анимации и физики: галочки слоёв дебаг-вида, ручки симуляции и счётчики состояния.
///
/// Всё в одном окне намеренно. Анимация и физика в движке связаны насквозь - foot IK стоит на
/// райкасте, рэгдолл живёт телами, поза персонажа приезжает то из клипа, то из симуляции, - и
/// вопрос «почему персонаж стоит не так» почти никогда не относится целиком к одной из двух сторон.
/// Разведя их по двум окнам, пришлось бы держать оба открытыми всегда.
///
/// Окно НЕ владеет ничем: галочки пишутся в <see cref="EditorSettings"/>, а вьюпорт перечитывает их
/// каждый кадр (см. PrefabSceneViewport.BeginDebugFrame). Поэтому закрытое окно ничего не выключает
/// - включённый слой продолжает рисоваться, и это правильно: дебаг-вид включают, чтобы смотреть на
/// сцену, а не на окно.
/// </summary>
public class AnimationPhysicsDebugWindow : ImGuiDockingWindow
{
	private readonly EditorSettings _settings;
	private readonly PrefabSceneViewport _viewport;

	private bool _changed;

	public AnimationPhysicsDebugWindow(string title, EditorSettings settings, PrefabSceneViewport viewport,
		ImGuiRender imGuiRender) : base(title, imGuiRender)
	{
		_settings = settings;
		_viewport = viewport;
	}

	protected override void OnRender(uint dockId)
	{
		_changed = false;

		if (ImGui.CollapsingHeader("Анимация", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawAnimationSection();
		}

		if (ImGui.CollapsingHeader("Физика", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawPhysicsSection();
		}

		if (ImGui.CollapsingHeader("Симуляция", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawSimulationSection();
		}

		if (ImGui.CollapsingHeader("Состояние", ImGuiTreeNodeFlags.DefaultOpen))
		{
			DrawStateSection();
		}

		if (ImGui.CollapsingHeader("Легенда"))
		{
			DrawLegendSection();
		}

		// Запись на диск - после отпускания контрола, а не на каждый тик драга: то же правило, что в
		// окне Graphics.
		if (_changed && !ImGui.IsAnyItemActive())
		{
			_settings.Save();
		}
	}

	// --- Секции ----------------------------------------------------------------------------------

	private void DrawAnimationSection()
	{
		var options = _settings.AnimationDebug;

		_changed |= ImGui.Checkbox("Скелет", ref options.Skeleton);
		Hint("Кости октаэдрами. Оранжевая кость - её позу задаёт физика, голубая - анимация.");

		_changed |= ImGui.Checkbox("Оси суставов", ref options.JointAxes);
		Hint("X красная, Y зелёная, Z синяя. Показывают перекрут кости вокруг собственной оси - " +
			"ошибку, невидимую на «палочках» скелета.");

		_changed |= ImGui.Checkbox("Имена костей", ref options.JointNames);
		Hint("Подписи поверх вьюпорта - ими заполняют поля компонентов, где кости задаются строками.");

		_changed |= ImGui.Checkbox("Bind-поза", ref options.BindPose);
		Hint("Серым поверх текущей позы: отличает «поза не применилась» от «применилась, но не та».");

		_changed |= ImGui.Checkbox("Foot IK", ref options.FootIk);
		Hint("Цепочки ног и подошвы. Красный - солвер в этом кадре НЕ применился " +
			"(нет нативного ozz, нет физики или не найдены кости).");

		_changed |= ImGui.Checkbox("Spring bones", ref options.SpringChains);
		_changed |= ImGui.Checkbox("Look-at", ref options.LookAt);

		_changed |= ImGui.Checkbox("Поверх геометрии##anim", ref options.OnTop);
		Hint("Без депт-теста. Почти всегда нужно именно так: скелет целиком внутри меша.");

		_settings.AnimationDebug = options;
	}

	private void DrawPhysicsSection()
	{
		var options = _settings.PhysicsDebug;

		_changed |= ImGui.Checkbox("Коллайдеры тел", ref options.Colliders);
		Hint("Каркасы по ФАКТИЧЕСКИМ формам из реестра симуляции, а не по тому, что заказывали.");

		_changed |= ImGui.Checkbox("Статика сцены", ref options.Statics);
		Hint("Меш статики рисуется габаритной коробкой: каркас по треугольникам уровня закрасил бы экран.");

		_changed |= ImGui.Checkbox("Контакты", ref options.Contacts);
		Hint("Стоит работы в узкой фазе - включается только этой галочкой. Длина стрелки - глубина проникновения.");

		_changed |= ImGui.Checkbox("Райкасты", ref options.Rays);
		Hint("Лучи кадра, в первую очередь лучи foot IK. Серый луч - промах.");

		_changed |= ImGui.Checkbox("Скорости", ref options.Velocities);
		Hint("Зелёная стрелка - линейная скорость, лиловая - угловая. Длина в единицах мира за секунду.");

		_changed |= ImGui.Checkbox("Суставы рэгдолла", ref options.RagdollJoints);

		// Галочка положительная, поле - от обратного (см. PhysicsDebugOptions.CollidersDepthTested:
		// полезное поведение обязано совпадать с нулевым значением, иначе дефолт не переживает
		// чтения старого файла настроек). Разворот делается здесь, в одном месте.
		bool collidersOnTop = !options.CollidersDepthTested;
		if (ImGui.Checkbox("Коллайдеры поверх геометрии##collidersontop", ref collidersOnTop))
		{
			options.CollidersDepthTested = !collidersOnTop;
			_changed = true;
		}

		Hint("Без депт-теста, как у скелета. Коллайдер персонажа целиком ВНУТРИ меша - с депт-тестом " +
			"его не видно вовсе, а вопрос к этой галочке обычно именно про капсулы.");

		_changed |= ImGui.Checkbox("Остальное поверх геометрии##phys", ref options.OnTop);
		Hint("Статика, контакты, лучи, скорости, суставы. Отдельно от коллайдеров: они живут снаружи " +
			"мешей, и «поверх всего» превращает их в сетку по всему экрану.");

		_settings.PhysicsDebug = options;
	}

	private void DrawSimulationSection()
	{
		bool enabled = _settings.ScenePhysicsEnabled;
		if (ImGui.Checkbox("Физика в сцене", ref enabled))
		{
			_settings.ScenePhysicsEnabled = enabled;
			_changed = true;
		}

		Hint("Мир всё равно заводится лениво - под персонажа с foot IK/рэгдоллом/Character Body или " +
			"под включённый дебаг физики. Эта галочка выключает его насовсем.");

		// Главное, что нужно знать про физику в редакторе, и по галочкам этого не видно: мир стоит,
		// пока не нажат Play. Без этой строки «физика не работает» и «игра не запущена» - один и тот
		// же экран.
		ImGui.TextDisabled(_viewport.ScriptCharacterStatus.Playing
			? "Идёт Play: симуляция и анимация работают."
			: "Play не запущен: мир заведён, но НЕ ШАГАЕТ, и анимация стоит на месте.");

		bool paused = _settings.ScenePhysicsPaused;
		if (ImGui.Checkbox("Пауза", ref paused))
		{
			_settings.ScenePhysicsPaused = paused;
			_changed = true;
		}

		float timeScale = _settings.ScenePhysicsTimeScale;
		if (ImGui.SliderFloat("Масштаб времени", ref timeScale, 0.01f, 2f))
		{
			_settings.ScenePhysicsTimeScale = timeScale;
			_changed = true;
		}

		float gravity = _settings.SceneGravity;
		if (ImGui.SliderFloat("Гравитация (Y)", ref gravity, -200f, 0f))
		{
			_settings.SceneGravity = gravity;
			_changed = true;
		}

		Hint("В единицах МИРА, а не в метрах: масштаб моделей произволен, и -9.81 осмысленно ровно " +
			"для метрового персонажа. Применяется при следующем создании мира.");

		float intensity = _settings.DebugLineIntensity;
		if (ImGui.SliderFloat("Яркость линий", ref intensity, 0.5f, 20f))
		{
			_settings.DebugLineIntensity = intensity;
			_changed = true;
		}

		Hint("Линии пишутся в HDR-таргет ДО тонемапа, экспозиция которого заранее неизвестна - " +
			"на очень яркой или очень тёмной сцене яркость правится здесь.");
	}

	private void DrawStateSection()
	{
		var physics = _viewport.DebugPhysics;

		if (physics == null)
		{
			ImGui.TextDisabled("Физики в сцене нет.");
			ImGui.TextWrapped("Мир заводится, когда в сцене появляется персонаж с компонентом " +
				"Foot IK, Ragdoll или Character Body, либо когда включён любой слой дебага физики.");
		}
		else
		{
			ImGui.Text($"Тел: {physics.BodyCount} (спит {physics.SleepingBodyCount})");
			ImGui.Text($"Треугольников статики: {physics.StaticTriangleCount}");
			ImGui.Text($"Шагов за кадр: {physics.LastStepCount} за {physics.LastStepMilliseconds:0.00} мс");
			ImGui.Text($"Райкастов за кадр: {physics.RayCastsThisFrame}");

			var contacts = physics.World.Contacts;
			ImGui.Text(contacts.Enabled
				? $"Контактов: {contacts.Contacts.Count}" + (contacts.Dropped > 0 ? $" (+{contacts.Dropped} отброшено)" : "")
				: "Контакты не собираются");
		}

		DrawScriptCharacters();

		ImGui.Separator();

		var stats = _viewport.DebugLineStats;
		ImGui.Text($"Дебаг-линий (вершин): {stats.Vertices}");
		if (stats.Overflowed)
		{
			// Молчать здесь нельзя: обрезанный вид неотличим от полного, и «дальше ничего нет»
			// выглядит как ответ на вопрос, ради которого дебаг и включали.
			ImGui.TextColored(new System.Numerics.Vector4(1f, 0.5f, 0.2f, 1f),
				"Упёрлись в потолок вершин - показано НЕ ВСЁ.");
		}

		ImGui.Separator();

		var characters = _viewport.DebugCharacters;
		if (characters.Count == 0)
		{
			ImGui.TextDisabled("Скиннед-персонажей в сцене нет.");
			return;
		}

		for (int i = 0; i < characters.Count; i++)
		{
			var character = characters[i];

			if (!ImGui.TreeNodeEx($"Сущность {character.EntityId}##character{i}",
				ImGuiTreeNodeFlags.DefaultOpen))
			{
				continue;
			}

			ImGui.Text($"Клип: {character.Clip}{(character.Playing ? "" : " (не найден)")}");
			ImGui.Text(character.Locomotion
				? $"Локомоушен: {character.LocoSpeed:0.00} м/с - стойка {character.LocoIdleWeight:0.00} / " +
					$"шаг {character.LocoWalkWeight:0.00} / бег {character.LocoRunWeight:0.00}, " +
					$"фазы аллюра {character.LocoWalkPhaseOffset:0.00}/{character.LocoRunPhaseOffset:0.00}"
				: "Локомоушен: нет (позу ведёт Animator)");
			ImGui.Text($"Время: {character.Time:0.000} с");
			ImGui.Text($"Суставов: {character.JointCount}");
			ImGui.Text($"Ног IK: {character.LegCount} - {(character.IkApplied ? "применён" : "не применён")}");
			ImGui.Text($"Цепочек spring bones: {character.ChainCount}");
			ImGui.Text(character.RagdollBones > 0
				? $"Рэгдолл: {character.RagdollBones} костей, {(character.RagdollPhysical ? "физика" : "анимация")}"
				: "Рэгдолл: нет");
			if (character.ReactionWeight > 0f)
			{
				ImGui.Text($"Хит-реакция: вес {character.ReactionWeight:0.00}, " +
					$"отклонение {character.ReactionDeviation:0.##} ед. модели");
			}

			ImGui.TreePop();
		}
	}

	/// <summary>
	/// Персонажи под управлением геймплейных скриптов (Character Body + скрипт движения).
	///
	/// Строка нужна ровно потому, что стоящий персонаж выглядит одинаково при ЧЕТЫРЁХ разных
	/// причинах: игра не запущена, физика выключена галочкой, компонент тела не доехал (например,
	/// сцена сгенерирована старой версией редактора) или тело есть и просто упёрлось. Разбирать это
	/// по коду дороже, чем вывести четыре числа.
	/// </summary>
	private void DrawScriptCharacters()
	{
		ImGui.Separator();

		var status = _viewport.ScriptCharacterStatus;

		ImGui.Text($"Скриптов движения: {status.Scripts}, из них с Character Body: {status.WithBody}");
		ImGui.Text($"Заведено тел: {status.Bodies}, Play: {(status.Playing ? "идёт" : "не запущен")}");

		if (status.Scripts == 0)
		{
			ImGui.TextDisabled("В сцене нет сущностей со скриптом движения.");
			return;
		}

		if (!status.Playing)
		{
			ImGui.TextDisabled("Скрипты идут только в Play Mode - жми Play в инспекторе.");
		}
		else if (status.WithBody == 0)
		{
			ImGui.TextDisabled("Тела нет - персонаж идёт трансформом и проходит сквозь геометрию. " +
				"Добавь Character Body (или пересоздай сцену, если она старая).");
		}
		else if (!status.HasPhysics)
		{
			ImGui.TextDisabled("Физики в сцене нет - персонаж с телом стоять и будет. " +
				"Включи «Физика в сцене» выше.");
		}
		else if (status.Paused)
		{
			// Пауза - самая обманчивая из причин: физика включена, тело заведено, скрипт исправно
			// задаёт ему скорость - и ничего не происходит, потому что мир не шагает.
			ImGui.TextDisabled("Симуляция НА ПАУЗЕ - скорость телу задаётся, но мир не шагает. " +
				"Сними галочку «Пауза» выше.");
		}
		else if (status.Bodies < status.WithBody)
		{
			ImGui.TextDisabled("Тело заведено не на всех: проверь радиус круга (нулевой отключает скрипт).");
		}
	}

	/// <summary>Легенда цветов. Не украшение: цвет здесь - единственная кодировка состояния, и без
	/// расшифровки «серая капсула» читается как «капсула», а не как «тело спит».</summary>
	private static void DrawLegendSection()
	{
		ImGui.BulletText("Оранжевый - динамическое тело (и кость, которой управляет физика)");
		ImGui.BulletText("Голубой - кинематика; кость, которой управляет анимация; нормаль поверхности");
		ImGui.BulletText("Серый - тело спит; bind-поза; луч, ничего не задевший");
		ImGui.BulletText("Синий - статика сцены");
		ImGui.BulletText("Жёлтый - контакт со статикой; связь рэгдолла; корень скелета");
		ImGui.BulletText("Красный - контакт двух тел; foot IK не применился");
		ImGui.BulletText("Зелёный - цепочка spring bones; линейная скорость; попавшая часть луча");
		ImGui.BulletText("Лиловый - цель look-at; угловая скорость; форма, которую дебаг рисовать не умеет");
	}

	/// <summary>Подсказка мелким серым под контролом. Именно текстом, а не всплывающей: половина
	/// галочек здесь стоит денег или имеет неочевидную цену, и узнавать об этом наведением мыши
	/// пришлось бы по одной.</summary>
	private static void Hint(string text)
	{
		ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.6f, 0.6f, 0.62f, 1f));
		ImGui.TextWrapped(text);
		ImGui.PopStyleColor();
		ImGui.Spacing();
	}
}
