using System;
using System.Collections.Generic;
using System.Numerics;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Core.Assets;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Engine.ImGui.Core;
using System.Threading.Tasks;
using Friflo.Engine.ECS;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using UnsafeCollections.Collections.Unsafe;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>Физика сцены: ленивый мир Bepu, статики, дебаг-оверлей линий. Часть <see cref="PrefabSceneViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class PrefabSceneViewport
	{
		/// <summary>
		/// Кадровый шаг физики: заводит или убирает мир по надобности, догоняет статику и двигает
		/// симуляцию. Идёт ДО анимации - и это не вкусовщина: луч foot IK обязан щупать мир в том
		/// состоянии, в котором он будет нарисован, а рэгдолл в этом же кадре читает позу из тел,
		/// проинтегрированных ЗДЕСЬ, и тут же задаёт им новую цель.
		/// </summary>
		private void PollScenePhysics(float deltaSeconds)
		{
			if (!ScenePhysicsWanted())
			{
				if (_physics != null)
				{
					// Драйвер держит рэгдоллы ЭТОГО мира - без их сноса они остались бы хендлами в
					// уничтоженной симуляции. Отвязка, а не Clear: персонажи со своими палитрами
					// скиннинга обязаны пережить выключение физики (см. AnimationDriver.DetachPhysics).
					_animation?.DetachPhysics();
					_motion.Clear(_physics);
					_physics.Dispose();
					_physics = null;
				}

				return;
			}

			if (_physics == null)
			{
				_physics = new ScenePhysics(new Vector3(0f, _editorSettings.SceneGravity, 0f));
				_physicsStaticsDirty = true;
			}

			// Симуляция идёт ТОЛЬКО в Play. Мир при этом заводится и статику держит всегда - построение
		// BVH по всей геометрии сцены в момент нажатия кнопки отдало бы первый кадр игры под
		// подвисание, - но шагов не делает: в режиме редактирования сцена обязана стоять там, где её
		// поставил автор. Рэгдолл, разъезжающийся, пока автор двигает объекты, - это не «живая
		// сцена», а невозможность её собрать.
		//
		// Ручная пауза остаётся сверху: она останавливает и то, что идёт по Play.
		_physics.Paused = _editorSettings.ScenePhysicsPaused || !IsPlaying;
			_physics.TimeScale = _editorSettings.ScenePhysicsTimeScale;
			_physics.RecordRays = _editorSettings.PhysicsDebug.Rays;

			bool recordContacts = _editorSettings.PhysicsDebug.NeedsContactRecording;
			if (_physics.World.Contacts.Enabled != recordContacts)
			{
				_physics.World.Contacts.Enabled = recordContacts;

				// Выключение обязано и ОЧИСТИТЬ: иначе на экране навсегда остался бы снимок шага,
				// на котором галочка ещё была включена, и он выглядел бы как живые контакты.
				if (!recordContacts)
				{
					_physics.World.Contacts.Clear();
				}
			}

			if (_physicsStaticsDirty)
			{
				_physicsStaticsDirty = false;
				RebuildPhysicsStatics();
			}

			// Скорость задаётся ДО шага, поза читается ПОСЛЕ. Слить их в один вызов нельзя: скорость,
			// заданная после шага, применится только к следующему (персонаж отстаёт от собственной
			// команды на кадр), а поза, прочитанная до шага, - это поза прошлого кадра.
			_motion.Input = _playerInput;
			_playerInput = default;
			_motion.Steer(_lastStore, _physics, IsPlaying, deltaSeconds, _animation);

			_physics.Update(deltaSeconds);

			// Тело сдвинулось - трансформ сущности за ним. SyncScene, который перекладывает трансформы
			// в инстансы, идёт РАНЬШЕ по кадру, поэтому картинка отстаёт от физики ровно на кадр -
			// столько же, сколько и Play-Mode-системы, которые EditorManager тикает после вьюпорта.
			_motion.Apply(_lastStore, _physics);
		}

		/// <summary>Нужна ли физика этой сцене вообще. Мир заводится только под конкретного
		/// потребителя - персонажа с foot IK или рэгдоллом либо явно включённый дебаг физики:
		/// построение статики - это BVH по всей геометрии сцены.</summary>
		private bool ScenePhysicsWanted()
		{
			if (!_editorSettings.ScenePhysicsEnabled)
			{
				return false;
			}

			if (_editorSettings.PhysicsDebug.AnyEnabled)
			{
				return true;
			}

			var store = _lastStore;
			if (store == null)
			{
				return false;
			}

			foreach (var record in _rendered.Values)
			{
				if (store.TryGetEntityById(record.EntityId, out var entity) &&
					(entity.HasComponent<FootIkComponent>() || entity.HasComponent<RagdollComponent>() ||
						IsPhysicalCharacter(entity)))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Ведёт ли этого персонажа физика геймплейного скрипта. Мир заводится по нему СРАЗУ,
		/// не дожидаясь Play: статика - это BVH по всей геометрии сцены, и строить его в момент
		/// нажатия кнопки значило бы отдать первый кадр игры под подвисание.</summary>
		private static bool IsPhysicalCharacter(Entity entity) => entity.HasComponent<CharacterBodyComponent>();

		/// <summary>
		/// Состояние привода персонажей - для окна дебага. Молчащий персонаж в редакторе выглядит
		/// одинаково независимо от того, ЧТО именно его не пускает: физика выключена галочкой, игра не
		/// запущена, компонент тела не доехал из префаба или тело есть, но упёрлось. Разбирать это
		/// глазами по коду - самое дорогое, что можно тут сделать, поэтому все четыре числа выведены
		/// наружу разом.
		/// </summary>
		public (bool Playing, bool HasPhysics, bool Paused, int Scripts, int WithBody, int Bodies) ScriptCharacterStatus
		{
			get
			{
				int scripts = 0;
				int withBody = 0;
				var store = _lastStore;

				if (store != null)
				{
					// Скрипты и тела считаются ОТДЕЛЬНО: сцена, сгенерированная до появления
					// Character Body, приезжает со скриптом и без тела, и «1 и 0» отвечает на вопрос
					// сразу, а «0 из 0» отправило бы искать поломку в физике.
					foreach (var entity in store.Query<CircleMoveComponent>().Entities)
					{
						scripts++;
						withBody += entity.HasComponent<CharacterBodyComponent>() ? 1 : 0;
					}

					// Игрок - тоже скрипт движения, только рулит им клавиатура (см. PlayerMoveComponent).
					foreach (var entity in store.Query<PlayerMoveComponent>().Entities)
					{
						scripts++;
						withBody += entity.HasComponent<CharacterBodyComponent>() ? 1 : 0;
					}
				}

				return (IsPlaying, _physics != null, _physics?.Paused ?? false, scripts, withBody,
					_motion.CharacterCount);
			}
		}

		/// <summary>
		/// Пересобирает статику физики по геометрии сцены. Скиннед-модели в неё НЕ идут: персонаж не
		/// должен быть полом сам себе - его собственная стопа немедленно нашла бы лучом его же ногу,
		/// и foot IK поднял бы его на высоту собственного бедра.
		/// </summary>
		private void RebuildPhysicsStatics()
		{
			if (_physics == null)
			{
				return;
			}

			_physics.BeginStatics();

			foreach (var record in _rendered.Values)
			{
				if (!record.Instantiated || string.IsNullOrEmpty(record.ResolvedPath) ||
					!_models.TryGetValue(record.ResolvedPath, out var state) || state.Model == null ||
					state.Model.Skeleton != null)
				{
					continue;
				}

				_physicsPositions.Clear();
				_physicsIndices.Clear();
				AppendRecordGeometry(record, state.Model, _physicsPositions, _physicsIndices);

				_physics.AddStaticMesh(
					System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_physicsPositions),
					System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_physicsIndices));
			}

			_physics.EndStatics();

			// Скретч держит мировую копию всей сцены - на большом уровне это десятки мегабайт,
			// которые до следующей пересборки не нужны никому.
			_physicsPositions.Clear();
			_physicsIndices.Clear();
			_physicsPositions.TrimExcess();
			_physicsIndices.TrimExcess();
		}

		// --- Дебаг-линии (см. DebugDraw / DebugLineOverlay) ---------------------------------------

		/// <summary>Открывает кадр дебага. Звать ДО любой стадии, которая рисует: список линий - это
		/// кадр целиком, а не накопитель.</summary>
		private void BeginDebugFrame()
		{
			// Подсветка кости из окна Humanoid включает дебаг сама по себе: её просят как раз тогда,
			// когда ни один слой ещё не включён - «покажи, какая это кость».
			_debugDraw.Enabled = _editorSettings.AnimationDebug.AnyEnabled ||
				_editorSettings.PhysicsDebug.AnyEnabled ||
				!string.IsNullOrEmpty(HighlightJoint);

			_debugDraw.Clear();
		}

		/// <summary>Закрывает кадр дебага: дорисовывает то, что принадлежит миру (физика), и заливает
		/// всё на GPU. Звать ПОСЛЕ анимации и до исполнения графа.</summary>
		private void EndDebugFrame()
		{
			if (_debugDraw.Enabled && _physics != null)
			{
				var options = _editorSettings.PhysicsDebug;
				PhysicsDebugDraw.Draw(_debugDraw, _physics, options);

				if (options.RagdollJoints)
				{
					_animation?.DrawRagdollJoints(_debugDraw, options.OnTop);
				}
			}

			_animation?.DescribeCharacters(_debugCharacters);
			PollDebugLineOverlay();
		}

		/// <summary>Ведёт GPU-оверлей дебаг-линий за содержимым кадра. Создание/снятие пересобирает
		/// граф (команды заморожены, см. GraphicsPipelineSimple.DebugOverlay), поэтому проверка
		/// «есть что рисовать» обязана быть дешёвой - она и есть пара сравнений.</summary>
		private void PollDebugLineOverlay()
		{
			if (!_debugDraw.Enabled || _debugDraw.TotalCount == 0)
			{
				if (_env.Pipeline.DebugOverlay != null)
				{
					_env.Pipeline.DebugOverlay = null;
					_env.Pipeline.InvalidateGraph();
				}

				return;
			}

			if (_debugLineOverlay == null)
			{
				if (_debugOverlayFailed)
				{
					return;
				}

				try
				{
					_debugLineOverlay = new DebugLineOverlay(_env.DilApi, _graphicsApi, _env.BatchRenderer,
						_env.Pipeline.Targets?.RenderColorFormat ?? TextureObjectFormat.R8G8B8A8UNorm);
				}
				catch (Exception ex)
				{
					// Один раз и больше не пробовать: не собравшийся шейдер не соберётся и на
					// следующем кадре, а поток одинаковых ошибок в консоли скрыл бы настоящие.
					_debugOverlayFailed = true;
					EngineLog.Add(LogLevel.Error, $"Debug draw: overlay unavailable: {ex.Message}");
					return;
				}
			}

			_debugLineOverlay.Intensity = _editorSettings.DebugLineIntensity;

			bool commandsDirty = _debugLineOverlay.Upload(_debugDraw);

			if (_env.Pipeline.DebugOverlay == null)
			{
				_env.Pipeline.DebugOverlay = _debugLineOverlay.Draw;
				commandsDirty = true;
			}

			if (commandsDirty)
			{
				_env.Pipeline.InvalidateGraph();
			}
		}

		/// <summary>Снимает дебаг-оверлей с конвейера и освобождает его. Звать перед пересозданием
		/// окружения и на выходе: оверлей держит буферы и PSO этого конкретного конвейера.</summary>
		private void ReleaseDebugOverlay()
		{
			if (_debugLineOverlay == null)
			{
				return;
			}

			_env.Pipeline.DebugOverlay = null;
			_env.Pipeline.InvalidateGraph();
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			_debugLineOverlay.Dispose();
			_debugLineOverlay = null;
		}

		private readonly HashSet<int> _visitedThisSync = new();
		private readonly List<int> _removeScratch = new();

		// Зеркала punctual-светов (point/spot) префаба в РЕНДЕР-сторе окружения: SimpleCullingAndRender
		// System собирает света из _env.Store, а сущности префаба живут в своём сторе с ЛОКАЛЬНЫМИ
		// трансформами - зеркало несёт мировые (ComputeWorldMatrix, как у геометрии). Ключ - id
		// сущности префаба. Синк покомпонентно каждый кадр: светов единицы, а ручки инспектора
		// (цвет/интенсивность/углы) обязаны быть живыми - пул светов и так перезаливается за кадр.
		private readonly Dictionary<int, Entity> _lightMirrors = new();

		/// <summary>Скретч сборки punctual-светов для бейка проб (см. PollSceneProbeBake) - чтобы не
		/// аллоцировать список каждый кадр.</summary>
		private readonly List<PunctualLight> _probeBakeLightsScratch = new();

		/// <summary>TLAS для RT-теней (режим «Ray-traced» комбо Shadow filtering, см.
		/// ModelPreviewViewport._rtShadowScene - та же роль). Отдельный от _sceneAccel проб: живёт
		/// независимо от probe-GI, BLAS-ы кешируются по мешам и переживают движение - на позы
		/// отвечает пересборка TLAS.</summary>
		private DiligentRayTracingScene? _rtShadowScene;
		private readonly List<DiligentRayTracingScene.Instance> _rtShadowInstances = new();
		private bool _appliedRtShadows;

	}
}
