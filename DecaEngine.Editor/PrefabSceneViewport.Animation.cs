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
	/// <summary>Привод анимации сцены: кадровый шаг персонажей и разметка гуманоидных аватаров. Часть <see cref="PrefabSceneViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class PrefabSceneViewport
	{
		private void UpdateAnimation(float deltaSeconds)
		{
			// Диагностика идёт ДО раннего выхода: если счётчики растут и без персонажей, значит
			// источник роста вообще не в анимации, и это тоже ответ.
			// System.Environment полным именем: у вьюпорта есть своё свойство Environment (окружение
			// сцены), и короткое имя разрешается в него.
			if (System.Environment.GetEnvironmentVariable("DECA_ANIM_DIAG") == "1" && (_animDiagFrame++ % 10) == 0)
			{
				Console.WriteLine($"[animdiag] кадр {_animDiagFrame}: " +
					$"{((DiligentBatchRenderer)_env.BatchRenderer).DiagCounters}, " +
					$"записей={_rendered.Count}, персонажей={_animation?.CharacterCount ?? 0}");
			}

			if (_animation == null || _animation.CharacterCount == 0)
			{
				return;
			}

			// Хранилище префаба, а не окружения: компоненты анимации автор ставит на сущность
			// префаба, рядом с ModelRenderer. Store пересоздаётся при перезагрузке префаба, поэтому
			// он берётся из _lastStore, а не кешируется драйвером.
			var store = _lastStore;
			if (store == null)
			{
				return;
			}

			// Проводка драйвера к физике и дебагу идёт КАЖДЫЙ кадр: и то, и другое включается
			// галочками на живой сцене, а драйвер мог быть создан задолго до этого.
			_animation.Physics = _physics;
			_animation.Debug = _debugDraw;
			_animation.DebugOptions = _editorSettings.AnimationDebug;
			_animation.HighlightJoint = HighlightJoint;
			_animation.BeginFrame();

			foreach (var record in _rendered.Values)
			{
				if (!store.TryGetEntityById(record.EntityId, out var entity))
				{
					continue;
				}

				// Humanoid-разметка модели - каждый кадр, потому что её правят в окне Humanoid на
				// живой сцене. Сравнение по ссылке внутри SetAvatar делает повторный вызов
				// бесплатным, а кеш по пути модели - и загрузку тоже.
				if (!string.IsNullOrEmpty(record.ResolvedPath) &&
					_models.TryGetValue(record.ResolvedPath, out var avatarState) &&
					avatarState.Model?.Skeleton != null)
				{
					_animation.SetAvatar(record.EntityId,
						AvatarFor(record.ResolvedPath, avatarState.Model.Skeleton));
				}

				// Мировой трансформ сущности: поза считается в пространстве МОДЕЛИ, а физика живёт в
				// мире, и перевод между ними нужен и лучу foot IK, и рэгдоллу.
				_animation.Update(entity, record.LastWorld, deltaSeconds);
			}

			_env.BatchRenderer.ExecuteSkinning();
		}

		// --- Humanoid-разметка (см. HumanoidAvatar) -----------------------------------------------

		/// <summary>Аватары по пути модели. Кеш по ПУТИ, а не по сущности: одна модель встречается в
		/// сцене много раз, а разметка у неё одна - она свойство рига, а не персонажа.</summary>
		private readonly Dictionary<string, HumanoidAvatar> _avatars = new();

		/// <summary>Пути моделей, у которых разметка получена автоматом, а не прочитана из файла.
		/// Различать обязательно: сохранённая разметка - решение человека, автоматическая - догадка,
		/// и окно Humanoid показывает это прямым текстом.</summary>
		private readonly HashSet<string> _autoAvatars = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Аватар модели: из файла рядом с ней, а если файла нет - автоматический.
		///
		/// Автоматический строится молча намеренно: без него foot IK и рэгдолл требовали бы ручной
		/// разметки ДО первого запуска, то есть не работали бы «из коробки» ни на одной модели. Цена
		/// известна и ограничена - авторские поля компонентов всё равно старше разметки, а сама
		/// разметка видна и правится в окне Humanoid.
		/// </summary>
		private HumanoidAvatar AvatarFor(string modelPath, PreparedSkeleton skeleton)
		{
			if (_avatars.TryGetValue(modelPath, out var cached))
			{
				return cached;
			}

			var avatar = HumanoidAvatarAsset.Load(modelPath);

			if (avatar == null)
			{
				avatar = HumanoidAutoMap.Build(skeleton);
				_autoAvatars.Add(modelPath);
			}
			else
			{
				_autoAvatars.Remove(modelPath);
			}

			_avatars[modelPath] = avatar;
			return avatar;
		}

		/// <summary>Автоматическая ли разметка у этой модели - для окна Humanoid.</summary>
		public bool IsAvatarAuto(string modelPath) => _autoAvatars.Contains(modelPath);

		/// <summary>Забывает разметку модели: следующий кадр перечитает её с диска. Звать после
		/// сохранения аватара из окна Humanoid - иначе персонажи сцены продолжат жить по разметке,
		/// которую только что заменили.</summary>
		public void InvalidateAvatar(string modelPath)
		{
			if (!string.IsNullOrEmpty(modelPath))
			{
				_avatars.Remove(modelPath);
				_autoAvatars.Remove(modelPath);
			}
		}

		/// <summary>Скиннед-модель выделенной сущности - вход для окна Humanoid. Скелет и путь
		/// приходят вместе: путь нужен, чтобы положить аватар рядом с моделью, а скелет - чтобы было
		/// что размечать.</summary>
		public (PreparedSkeleton? Skeleton, string? ModelPath, string Name) SelectedSkinnedModel
		{
			get
			{
				if (_highlightedId < 0 || !_rendered.TryGetValue(_highlightedId, out var record) ||
					string.IsNullOrEmpty(record.ResolvedPath) ||
					!_models.TryGetValue(record.ResolvedPath, out var state) ||
					state.Model?.Skeleton == null)
				{
					return (null, null, string.Empty);
				}

				return (state.Model.Skeleton, record.ResolvedPath,
					System.IO.Path.GetFileName(record.ResolvedPath));
			}
		}

		/// <summary>Кость, подсвеченная окном Humanoid. Пусто - подсветки нет.</summary>
		public string HighlightJoint { get; set; } = string.Empty;

		// --- Физика сцены (см. ScenePhysics) ------------------------------------------------------

	}
}
