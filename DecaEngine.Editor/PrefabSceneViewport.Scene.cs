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
	/// <summary>Содержимое сцены: синк ECS-дерева в инстансы, стриминг моделей, границы, TLAS RT-теней. Часть <see cref="PrefabSceneViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class PrefabSceneViewport
	{
		/// <summary>Обходит дерево префаба и приводит env-сцену в соответствие: новые ModelRenderer-ы
		/// начинают загрузку, готовые модели инстанцируются, сдвинутые сущности переставляют свои
		/// инстансы, удалённые - убирают их.</summary>
		private void SyncScene(Entity root)
		{
			_visitedThisSync.Clear();
			_visitedLightsThisSync.Clear();
			bool structuralChange = false;
			bool boundsDirty = false;

			SyncEntity(root, ref structuralChange, ref boundsDirty);

			// Света, пропавшие из дерева (удалены/лишились компонента/сменили тип на направленный), -
			// убираем зеркала. Пересборки графа не требуется: пул светов и ClusterParams полностью
			// живые, замороженные команды перечитывают их каждый кадр.
			_removeScratch.Clear();
			foreach (var kvp in _lightMirrors)
			{
				if (!_visitedLightsThisSync.Contains(kvp.Key))
				{
					_removeScratch.Add(kvp.Key);
				}
			}
			foreach (var id in _removeScratch)
			{
				if (!_lightMirrors[id].IsNull)
				{
					_lightMirrors[id].DeleteEntity();
				}
				_lightMirrors.Remove(id);
			}

			// Сущности, пропавшие из дерева (удалены/лишились компонента), - убираем их инстансы.
			_removeScratch.Clear();
			foreach (var kvp in _rendered)
			{
				if (!_visitedThisSync.Contains(kvp.Key))
				{
					_removeScratch.Add(kvp.Key);
				}
			}
			foreach (var id in _removeScratch)
			{
				RemoveRecord(_rendered[id]);
				_rendered.Remove(id);
				structuralChange = true;
			}

			if (structuralChange)
			{
				// Команды графа заморожены после первого Compile - новые/удалённые батчи он не
				// увидит без пересборки, а освобождение/создание GPU-ресурсов требует барьера
				// (см. ModelPreviewViewport.PollPendingLoad - тот же порядок).
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();

				// Ёмкости под новые батчи/инстансы наращиваются ЗДЕСЬ, пока GPU уже остановлен, а
				// граф всё равно будет пересобран. Иначе рост обнаруживается позже - в ветке
				// _transformsDirty, которая идёт ПОСЛЕ записи команд и барьера не делает; она
				// построена на допущении «движение ёмкости не меняет», и это допущение кто-то должен
				// обеспечивать. Лишним вызовом это не является: внутри стоит проверка, и при
				// достаточных ёмкостях он ничего не делает.
				_env.BatchRenderer.CheckAndReallocateBuffers();

				// Заливку СОДЕРЖИМОГО инстансов здесь делать нельзя, и это главное про это место:
				// мировые матрицы инстансов производит GpuInstanceBufferSystem внутри
				// _env.Root.Update, а он идёт ПОЗЖЕ по кадру. На кадре появления модели её матрицы
				// ещё не посчитаны, и любая заливка отсюда отправила бы на GPU нули - объект
				// схлопнут в точку и невидим. Дальше его никто не перезаливал, потому что грязных
				// флагов не осталось, и он оживал только от первого сдвига.
				//
				// Поэтому поднимается _transformsDirty: ветка движения идёт ПОСЛЕ Root.Update и
				// перезальёт инстансы уже с настоящими матрицами - тем же путём, которым это и так
				// работает при перетаскивании.
				_transformsDirty = true;

				// Первый шаг анимации СРАЗУ после инстанцирования, до пересборки графа. Персонаж
				// появляется в драйвере именно здесь, а кадровый UpdateAnimation идёт РАНЬШЕ по
				// коду - то есть на кадре появления модели скиннинг не диспетчеризовался ни разу,
				// и приёмник скиннед-инстанса оставался в GPU-буфере незаполненным: модель была
				// невидима (схлопнута), хотя обводка выделения рисовалась. Появлялась она только
				// после первого сдвига, который поднимал _transformsDirty и попутно всё дозаливал.
				// Нулевой шаг времени: позу посчитать нужно, а двигать её - нет.
				UpdateAnimation(0f);

				_env.Pipeline.InvalidateGraph();

				// Ручки AO/SSGI - только после барьера (SetConstant трогает ImmediateContext).
				PushPostProcessRanges();
				ApplyMaterialSettings();

				// TLAS RT-теней - тоже после барьера: свежезагруженные модели принесли новые меши
				// (BLAS) и материалы (привязка _SceneTlas обязана случиться до их первого дроу).
				UpdateRtShadowScene();
				boundsDirty = true;

				// Контур выделения строится по env-инстансам - структурное изменение (модель
				// догрузилась/сущность удалена) обязано его перепечь (см. SyncSelectionHighlight).
				_structuralDirtySelection = true;

				// По той же причине устарела и статика физики (см. RebuildPhysicsStatics).
				_physicsStaticsDirty = true;

				// Геометрия сцены сменилась - пробы пересобираются (мировой BVH бейкера статичен).
				RequestProbeSession(0.3f);
			}

			if (boundsDirty)
			{
				UpdateShadowBounds();
				if (_framePending)
				{
					FrameAll();
				}
			}

			// Движение сущностей: при живом GPU-пути позы уезжают в TLAS без пересоздания сессии -
			// и без прежнего лага (ребейк BVH на главном потоке + потеря поля). CPU-путь остаётся
			// статическим - там по-старому, ребейк за дебаунсом.
			if (_transformsDirty)
			{
				// Условие требует И СТРУКТУР УСКОРЕНИЯ, а не только живого GPU-пути: позы
				// уезжают именно в TLAS, а его нет на программной трассировке (_sceneAccel == null,
				// там шейдер ходит по BVH бейкера, а тот собран под СТАРЫЕ позы). Без этой
				// проверки ветка выбиралась по «жив ли GPU-путь», PollSceneProbePoses тут же выходил
				// по _sceneAccel == null, а ребейк никто не заказывал - движение терялось МОЛЧА, и GI
				// оставался от старой сцены до следующего ребейка по другой причине.
				if (_sceneGpu != null && _sceneAccel != null && !_sceneGpuDisabled)
				{
					_sceneTlasDirty = true;
				}
				else
				{
					RequestProbeSession(0.4f);
				}

				// TLAS RT-теней живёт отдельно от проб и следует за позами сам: пересборка верхнего
				// уровня дешёвая (BLAS-ы кешируются по мешам), гизмо-драг тянет её каждый кадр.
				UpdateRtShadowScene();
			}
		}

		/// <summary>Пересобирает TLAS RT-теней по актуальным позам сцены и привязывает его
		/// материалам всех резидентных моделей (см. ModelPreviewViewport.UpdateRtShadowScene -
		/// та же роль; здесь мировая поза инстанса = локальный TRS × мировая матрица записи).
		/// No-op вне режима «Ray-traced». Вызывается из ветки структурного изменения (после
		/// барьера) и из ветки движения сущностей.</summary>
		private void UpdateRtShadowScene()
		{
			if (!RtShadowsEnabled())
			{
				return;
			}

			_rtShadowInstances.Clear();
			foreach (var record in _rendered.Values)
			{
				var model = record.Resident?.Model;
				if (model == null || !record.Instantiated)
				{
					continue;
				}

				foreach (var instance in model.instances)
				{
					if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count ||
						model.Meshes[instance.meshId] is not DiligentMesh mesh ||
						mesh.IndexCount < 3 || mesh.VertexBuffer == null || mesh.IndexBuffer == null)
					{
						continue;
					}

					var t = instance.transform;
					var local = Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
						Matrix4x4.CreateFromQuaternion(t.rotation) *
						Matrix4x4.CreateTranslation(t.position);
					_rtShadowInstances.Add(new DiligentRayTracingScene.Instance(mesh,
						local * record.LastWorld, (uint)_rtShadowInstances.Count));
				}
			}

			if (_rtShadowInstances.Count == 0)
			{
				return;
			}

			_rtShadowScene ??= new DiligentRayTracingScene(_env.DilApi);
			_rtShadowScene.Rebuild(_rtShadowInstances);
			if (_rtShadowScene.Tlas == null)
			{
				return;
			}

			// Привязка идемпотентна (дескриптор указывает на сам объект TLAS) - после структурного
			// изменения она докрывает материалы свежезагруженных моделей.
			foreach (var kvp in ((DiligentBatchRenderer)_env.BatchRenderer).GetMaterials())
			{
				kvp.Value.SetAccelStructure("_SceneTlas", _rtShadowScene.Tlas);
			}
		}

		private void SyncEntity(Entity entity, ref bool structuralChange, ref bool boundsDirty)
		{
			if (entity.HasComponent<ModelRenderer>())
			{
				var assetPath = entity.GetComponent<ModelRenderer>().modelRef.Path ?? "";
				_visitedThisSync.Add(entity.Id);

				if (!_rendered.TryGetValue(entity.Id, out var record) ||
					!string.Equals(record.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase))
				{
					if (record != null)
					{
						RemoveRecord(record);
						structuralChange = true;
					}

					record = new RenderedModel { AssetPath = assetPath, EntityId = entity.Id };
					_rendered[entity.Id] = record;
				}

				if (record.ResolvedPath == null && assetPath.Length > 0)
				{
					record.ResolvedPath = ResolveAssetPath(assetPath);
					if (record.ResolvedPath == null)
					{
						EngineLog.Add(LogLevel.Warning, $"Prefab scene: asset not found: '{assetPath}'");
						// Путь не зарезолвился - помечаем запись "пустой" моделью, чтобы не искать
						// файл заново каждый кадр; смена Path в компоненте пересоздаст запись.
						record.ResolvedPath = "";
					}
				}

				if (!string.IsNullOrEmpty(record.ResolvedPath))
				{
					var world = ComputeWorldMatrix(entity);
					var anchor = world.Translation;

					// Решение стриминга по камере: в радиусе - держим ссылку (загрузка стартует из
					// ModelStreamingSystem по приоритету расстояния), вышли за радиус (с гистерезисом) -
					// снимаем инстансы и отпускаем; стример выселит модель с GPU после паузы
					// (UnloadAfterSeconds - буфер против дребезга на границе радиуса).
					if (record.Resident == null)
					{
						if (_streamer.ShouldBeResident(anchor, currentlyResident: false))
						{
							record.Resident = _streamer.Acquire(record.ResolvedPath, anchor);
						}
					}
					else
					{
						record.Resident.Anchor = anchor;

						if (!_streamer.ShouldBeResident(anchor, currentlyResident: true))
						{
							if (record.Instantiated)
							{
								RemoveRecord(record, releaseResident: false);
								structuralChange = true;
							}

							_streamer.Release(record.Resident);
							record.Resident = null;
						}
					}

					if (record.Resident is { } state)
					{
						if (!record.Instantiated && state.Ready)
						{
							InstantiateRecord(record, state, world);
							structuralChange = true;
						}
						else if (record.Instantiated && world != record.LastWorld)
						{
							UpdateRecordTransforms(record, state, world);
							boundsDirty = true;
							_transformsDirty = true;

							// Объект поехал - его треугольники в статике физики устарели. Стопа
							// персонажа обязана встать на пол там, где он ТЕПЕРЬ, а не там, где он
							// был при последней пересборке.
							//
							// НО ТОЛЬКО ЕСЛИ ОН В СТАТИКЕ ВООБЩЕ ЕСТЬ. Скиннед-модели в неё не идут
							// (см. RebuildPhysicsStatics: персонаж не должен быть полом сам себе), и
							// пересборка на их движение - это работа впустую, которая ЛОМАЕТ сцену:
							// идущий персонаж двигается каждый кадр, статика пересобирается каждый
							// кадр, а пересборка снимает и заводит заново меш пола. Тело, стоящее на
							// нём, каждый кадр теряет накопленные импульсы контакта, не успевает
							// опереться и проваливается в свободном падении - вместе с рэгдоллами, а
							// лучи foot IK при этом бьют то в пол, то в пустоту.
							_physicsStaticsDirty |= state.Model?.Skeleton == null;
						}
					}
				}
			}

			// Punctual-света (point/spot) - зеркалом в рендер-стор окружения с МИРОВЫМ трансформом
			// (Position сущности префаба локален родителю). Направленные и солнце сюда не идут:
			// направленный свет Scene View - это солнце окружения (см. SyncSunEntity).
			if (entity.HasComponent<LightComponent>() && !entity.HasComponent<SunComponent>())
			{
				var light = entity.GetComponent<LightComponent>();
				if (light.Type is LightType.Point or LightType.Spot)
				{
					_visitedLightsThisSync.Add(entity.Id);

					var world = ComputeWorldMatrix(entity);
					Matrix4x4.Decompose(world, out _, out var worldRot, out var worldPos);

					if (!_lightMirrors.TryGetValue(entity.Id, out var mirror) || mirror.IsNull)
					{
						mirror = _env.Store.CreateEntity(
							new Position(worldPos.X, worldPos.Y, worldPos.Z),
							new Rotation(worldRot.X, worldRot.Y, worldRot.Z, worldRot.W),
							light);
						_lightMirrors[entity.Id] = mirror;
					}
					else
					{
						mirror.Position = new Position(worldPos.X, worldPos.Y, worldPos.Z);
						mirror.Rotation = new Rotation(worldRot.X, worldRot.Y, worldRot.Z, worldRot.W);
						ref var mirrorLight = ref mirror.GetComponent<LightComponent>();
						mirrorLight = light;
					}
				}
			}

			foreach (var child in entity.ChildEntities)
			{
				SyncEntity(child, ref structuralChange, ref boundsDirty);
			}
		}

		/// <summary>Резолвит путь AssetRef (относительный к "Assets" проекта, forward-slash) в
		/// абсолютный. Фолбэк - папка "Assets" вверх по пути от текущего .prefab.json: префаб можно
		/// открыть и без загруженного проекта.</summary>
		private string? ResolveAssetPath(string assetPath)
		{
			if (Path.IsPathRooted(assetPath))
			{
				return File.Exists(assetPath) ? assetPath : null;
			}

			var relative = assetPath.Replace('/', Path.DirectorySeparatorChar);

			var assetsRoot = _projectSession.AssetsPath;
			if (assetsRoot != null)
			{
				var candidate = Path.Combine(assetsRoot, relative);
				if (File.Exists(candidate))
				{
					return candidate;
				}
			}

			if (_currentPrefabPath != null)
			{
				var dir = Path.GetDirectoryName(Path.GetFullPath(_currentPrefabPath));
				while (dir != null)
				{
					if (string.Equals(Path.GetFileName(dir), "Assets", StringComparison.OrdinalIgnoreCase))
					{
						var candidate = Path.Combine(dir, relative);
						return File.Exists(candidate) ? candidate : null;
					}
					dir = Path.GetDirectoryName(dir);
				}
			}

			return null;
		}

		/// <summary>Опции загрузки для стримера - те же, что были у прямого BeginLoadAsync.
		/// Фабрика (а не снимок): анизотропию пользователь меняет на лету, а смена, требующая
		/// перечитывания, обрабатывается в RecreateEnvironment (dropModels).</summary>
		private ModelLoadOptions BuildLoadOptions() =>
			ViewportSettingsPush.BuildLoadOptions(_editorSettings, RtShadowsEnabled());

		/// <summary>Потолок текстуры в том виде, в каком он уходит в загрузчик - тем же методом, что
		/// и в превью: сравнивать сырую настройку с заклампленной значило бы вечно видеть расхождение
		/// на значениях вне [128, 8192] и перечитывать сцену каждым нажатием OK.</summary>
		private int ClampedMaxTextureSize() => ViewportSettingsPush.ClampedMaxTextureSize(_editorSettings);

		/// <summary>Модель догрузилась и зарегистрирована стримером: атласы проб уже живут -
		/// новорождённой модели вместо плейсхолдеров сразу привязываются настоящие (кбуфер с сеткой
		/// она получит из ApplyMaterialSettings после структурной пересборки в SyncScene).</summary>
		private void OnStreamedModelReady(ModelStreamer.Resident resident)
		{
			_probeTextures?.Bind(resident.Model!);
		}

		/// <summary>Стример сейчас снимет регистрации батч-рендерера ОДНОЙ конкретной модели
		/// (партиционное выселение - см. ModelStreamer.ResidencyResetting): снимаем инстанс-сущности
		/// только тех записей, что ссылаются на ИМЕННО этого резидента, пока его BatchId-ы ещё валидны.
		/// Записи других моделей не трогаются - в отличие от прежней версии (полный сброс всей сцены
		/// на любое частичное выселение), см. задачу про сужение этого события.</summary>
		private void OnStreamerResidencyResetting(ModelStreamer.Resident resident)
		{
			// Стример сейчас освободит эту модель - фоновая сборка BVH (читает геометрию ВСЕЙ сцены)
			// обязана перестать её читать ДО этого.
			WaitProbeBakerTask();

			foreach (var record in _rendered.Values)
			{
				if (record.Instantiated && ReferenceEquals(record.Resident, resident))
				{
					RemoveRecord(record, releaseResident: false);
				}
			}

			// Контур выделения ссылался на снятые сущности; пробы - на материалы/BVH выселяемой
			// модели. Сброс проб безопасен: барьер GPU стример делает сразу после этого события.
			_highlightedId = -1;
			_env.Pipeline.PostOverlay = null;
			_structuralDirtySelection = true;
			_physicsStaticsDirty = true;
		}

		private void OnStreamerResidencyReset(ModelStreamer.Resident resident)
		{
			ResetProbeGi();
			_framePending |= _rendered.Count == 0;
		}

		/// <summary>Создаёт env-сущности под все инстансы модели: комбинированный трансформ =
		/// локальный трансформ glTF-инстанса * мировая матрица сущности префаба.</summary>
		private void InstantiateRecord(RenderedModel record, ModelStreamer.Resident state, Matrix4x4 world)
		{
			var model = state.Model!;
			for (int i = 0; i < model.instances.Count; i++)
			{
				var instance = model.instances[i];
				var combined = ComposeInstanceTransform(instance.transform, world);
				var entity = ModelViewportGeometry.CreateInstanceEntity(_env.Store, _env.ResourceManager,
					_env.BatchRenderer, state.MeshIds, state.MaterialIds, state.BatchCache,
					instance.meshId, instance.materialId, combined,
					model, state.SkinBases,
					// Все скиннед-инстансы модели принадлежат ОДНОЙ сущности префаба: это один
					// персонаж из нескольких мешей, и поза у них общая.
					palette => EnsureAnimation().AddInstance(record.EntityId, model, palette));
				if (entity != null)
				{
					record.EnvEntities.Add(entity.Value);
					record.InstanceIndices.Add(i);
				}
			}

			record.LastWorld = world;
			record.Instantiated = true;
		}

		/// <summary>Переставляет env-сущности записи под новую мировую матрицу сущности префаба.
		/// GpuUpdateTag снимается системой после применения (см. GpuInstanceBufferSystem) - каждое
		/// движение обязано перевесить его заново.</summary>
		private void UpdateRecordTransforms(RenderedModel record, ModelStreamer.Resident state, Matrix4x4 world)
		{
			var model = state.Model!;
			for (int i = 0; i < record.EnvEntities.Count; i++)
			{
				var instance = model.instances[record.InstanceIndices[i]];
				var t = ComposeInstanceTransform(instance.transform, world);

				var entity = record.EnvEntities[i];
				entity.Position = new Position(t.position.X, t.position.Y, t.position.Z);
				entity.Rotation = new Rotation(t.rotation.X, t.rotation.Y, t.rotation.Z, t.rotation.W);
				entity.Scale3 = new Scale3(t.scale.X, t.scale.Y, t.scale.Z);
				entity.AddTag<GpuUpdateTag>();
			}

			record.LastWorld = world;
		}

		private static DecaEngine.Core.Transform ComposeInstanceTransform(
			DecaEngine.Core.Transform instanceLocal, Matrix4x4 world)
		{
			var combined = MathUtils.CreateTrs(
				instanceLocal.position, instanceLocal.rotation, instanceLocal.scale) * world;

			if (!Matrix4x4.Decompose(combined, out var scale, out var rotation, out var translation))
			{
				// Скошенный трансформ (неравномерный скейл под поворотом) - восстанавливаем TRS из
				// базиса матрицы. Прежний фолбэк "позиция + Identity" молча ВЫБРАСЫВАЛ поворот:
				// объект рисовался неповёрнутым, а его punctual-тень вдобавок кулилась не там.
				// Ортонормированный базис под скосом приближение, но приближение В ТОМ ЖЕ месте
				// мира, а не другая поза.
				translation = combined.Translation;
				var bx = new Vector3(combined.M11, combined.M12, combined.M13);
				var by = new Vector3(combined.M21, combined.M22, combined.M23);
				var bz = new Vector3(combined.M31, combined.M32, combined.M33);
				scale = new Vector3(bx.Length(), by.Length(), bz.Length());

				var rx = scale.X > 1e-8f ? bx / scale.X : Vector3.UnitX;
				var ry = scale.Y > 1e-8f ? by / scale.Y : Vector3.UnitY;
				ry -= rx * Vector3.Dot(ry, rx);
				ry = ry.LengthSquared() > 1e-12f ? Vector3.Normalize(ry) : Vector3.UnitY;
				var rz = Vector3.Cross(rx, ry);
				rotation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
					rx.X, rx.Y, rx.Z, 0f,
					ry.X, ry.Y, ry.Z, 0f,
					rz.X, rz.Y, rz.Z, 0f,
					0f, 0f, 0f, 1f));
			}

			return new DecaEngine.Core.Transform
			{
				position = translation,
				rotation = rotation,
				scale = scale,
			};
		}

		/// <summary>Снимает env-сущности записи. <paramref name="releaseResident"/> - отпустить ли и
		/// ссылку стримера на модель (запись умирает совсем); false - при сбросе регистраций
		/// стримера (модель остаётся нужна, запись переинстанцируется) и при стрим-ауте по радиусу
		/// (ссылка отпускается вызывающим отдельно).</summary>
		private void RemoveRecord(RenderedModel record, bool releaseResident = true)
		{
			foreach (var entity in record.EnvEntities)
			{
				_env.ResourceManager.UnregisterRenderable(entity);
				entity.DeleteEntity();
			}
			record.EnvEntities.Clear();
			record.InstanceIndices.Clear();
			record.Instantiated = false;

			// Поза персонажа держит нативные объекты ozz и участки палитры, привязанные к уже
			// снятым инстансам: пережить их она не должна.
			_animation?.Remove(record.EntityId);

			if (releaseResident && record.Resident != null)
			{
				_streamer.Release(record.Resident);
				record.Resident = null;
			}
		}

		private void ClearScene()
		{
			if (_rendered.Count == 0 && _models.Count == 0 && _lightMirrors.Count == 0)
			{
				return;
			}

			// Физика сцены умирает вместе со сценой: её статика - это геометрия исчезающих моделей,
			// а рэгдоллы держат хендлы тел этого мира. Рэгдоллы сносятся ПЕРВЫМИ - иначе они
			// остались бы хендлами в уничтоженной симуляции. Самих персонажей снимают ниже
			// RemoveRecord-ы, по одному вместе с их записями.
			_animation?.DetachPhysics();
			_physics?.Dispose();
			_physics = null;
			_physicsStaticsDirty = true;

			foreach (var record in _rendered.Values)
			{
				RemoveRecord(record);
			}
			_rendered.Clear();

			// Зеркала светов живут в рендер-сторе окружения - оно переживает смену префаба,
			// сущности надо снять руками (в отличие от RecreateEnvironment, где стор умирает целиком).
			foreach (var mirror in _lightMirrors.Values)
			{
				if (!mirror.IsNull)
				{
					mirror.DeleteEntity();
				}
			}
			_lightMirrors.Clear();

			// «Сначала очистить предыдущее, потом грузить новое»: смена/закрытие префаба освобождает
			// резидентные модели прошлой сцены с GPU (барьер + сброс регистраций + Release +
			// пересборка графа внутри ClearAll) и отменяет её фоновые загрузки - модели нового
			// префаба стартуют в пустом батч-рендерере. Прежний кеш жил вечно и копил geometry
			// footprint всех когда-либо открытых префабов.
			WaitProbeBakerTask();
			_streamer.ClearAll();

			// Выделение ссылалось на сущности умершего стора - контур снимается вместе со сценой
			// (пересборка графа ниже подхватит и снятый PostOverlay-хук).
			_highlightedId = -1;
			_env.Pipeline.PostOverlay = null;

			// Замороженные команды графа продолжали бы рисовать снятые инстансы (см. комментарий в
			// ModelPreviewViewport.ClearWireframeOverlay) - барьер + пересборка обязательны.
			_env.DilApi.ImmediateContext.Flush();
			_env.DilApi.ImmediateContext.WaitForIdle();

			// Пробы пересчитаются под новую сцену; сброс за барьером выше.
			ResetProbeGi();

			_env.Pipeline.InvalidateGraph();
		}

		// --- Выделение: контур отдельным пассом и пикинг --------------------------------------------

		/// <summary>Держит контур выделения (см. <see cref="SelectionOutlineOverlay"/>) в согласии со
		/// сценой: перепекает мировую геометрию силуэта при смене выделения, движении сущностей или
		/// структурных изменениях (модель догрузилась/удалена). Пустое выделение снимает
		/// PostOverlay-хук с конвейера.</summary>
		private void SyncSelectionHighlight(Entity? selected)
		{
			int id = selected.HasValue && !selected.Value.IsNull ? selected.Value.Id : -1;
			bool selectionChanged = id != _highlightedId;

			if (!selectionChanged && !_transformsDirty && !_structuralDirtySelection)
			{
				return;
			}

			_highlightedId = id;
			_structuralDirtySelection = false;

			_selectionPositions.Clear();
			_selectionIndices.Clear();
			if (selected.HasValue && id != -1)
			{
				CollectSelectionGeometry(selected.Value);
			}

			if (_selectionPositions.Count == 0)
			{
				if (_env.Pipeline.PostOverlay != null)
				{
					_env.Pipeline.PostOverlay = null;
					_env.Pipeline.InvalidateGraph();
				}
				return;
			}

			_selectionOverlay ??= new SelectionOutlineOverlay(_env.DilApi, _graphicsApi, _env.BatchRenderer,
				(IRenderTarget)_env.ColorTarget);

			// Пересборка команд пасса нужна только когда изменилось ЧИСЛО индексов/пересозданы
			// буферы либо хук ещё не висел; обновление содержимого буферов на месте (гизмо-драг)
			// замороженные команды переживают.
			bool commandsDirty = _selectionOverlay.UpdateGeometry(_selectionPositions, _selectionIndices);
			if (_env.Pipeline.PostOverlay == null)
			{
				_env.Pipeline.PostOverlay = _selectionOverlay.Draw;
				commandsDirty = true;
			}

			if (commandsDirty)
			{
				_env.Pipeline.InvalidateGraph();
			}
		}

		/// <summary>Собирает мировую геометрию силуэта выделенной сущности И её потомков (выделение
		/// родителя подсвечивает всё поддерево, как в обычных редакторах).</summary>
		private void CollectSelectionGeometry(Entity entity)
		{
			if (_rendered.TryGetValue(entity.Id, out var record) && record.Instantiated &&
				!string.IsNullOrEmpty(record.ResolvedPath) &&
				_models.TryGetValue(record.ResolvedPath, out var state) && state.Model != null)
			{
				AppendRecordGeometry(record, state.Model, _selectionPositions, _selectionIndices);
			}

			foreach (var child in entity.ChildEntities)
			{
				CollectSelectionGeometry(child);
			}
		}

		/// <summary>Запекает вершины инстансов записи в мировое пространство (CPU-копии вершин живут
		/// в IMeshObject, пока жива модель - тот же источник, что у probe-GI BVH, см. ProbeGiBaker).
		///
		/// Приёмник приходит параметром: та же самая мировая геометрия нужна и обводке выделения, и
		/// статике физики (см. RebuildPhysicsStatics), и переписывать её вторым таким же методом
		/// значило бы завести второе место, где можно ошибиться с фильтром топологии или с
		/// трансформом инстанса.</summary>
		private void AppendRecordGeometry(RenderedModel record, ModelLoader model,
			List<Vector3> targetPositions, List<uint> targetIndices)
			=> AppendModelGeometry(model,
				System.Runtime.InteropServices.CollectionsMarshal.AsSpan(record.InstanceIndices),
				record.LastWorld, targetPositions, targetIndices);

		/// <summary>Та же запечка, но по ЯВНОМУ списку инстансов и явной мировой матрице - вход для
		/// headless-прогона сцены (см. <see cref="ScenePhysicsProbe"/>). Вынесено, чтобы у пробника не
		/// завелась вторая копия фильтра топологии и склейки трансформа инстанса: разойдясь с этой,
		/// она проверяла бы не ту статику, которую строит редактор.</summary>
		public static unsafe void AppendModelGeometry(ModelLoader model, ReadOnlySpan<int> instanceIndices,
			Matrix4x4 world, List<Vector3> targetPositions, List<uint> targetIndices)
		{
			for (int i = 0; i < instanceIndices.Length; i++)
			{
				var instance = model.instances[instanceIndices[i]];
				if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count)
				{
					continue;
				}

				// Только треугольные меши: PSO маски - TriangleList, индексы линий/точек дали бы
				// мусорные треугольники в силуэте (тот же фильтр, что у ProbeGiBaker).
				if (model.MaterialPbr.TryGetValue(instance.materialId, out var pbr) &&
					pbr.Topology != ModelLoader.MeshTopologyTriangles)
				{
					continue;
				}

				var mesh = model.Meshes[instance.meshId];
				if (mesh.IndexCount < 3 || mesh.VertexData == null || mesh.IndexData == null)
				{
					continue;
				}

				var t = ComposeInstanceTransform(instance.transform, world);
				var matrix = MathUtils.CreateTrs(t.position, t.rotation, t.scale);

				int vertexCount = UnsafeArray.GetLength(mesh.VertexData);
				var vertices = new ReadOnlySpan<Vertex>(UnsafeArray.GetPtr<Vertex>(mesh.VertexData, 0), vertexCount);

				// ModelLoader всегда строит 32-битные индексы (см. PreparedMesh.Indices: uint[]).
				var indices = new ReadOnlySpan<uint>(UnsafeArray.GetPtr<uint>(mesh.IndexData, 0), mesh.IndexCount);

				int baseVertex = targetPositions.Count;
				for (int v = 0; v < vertexCount; v++)
				{
					targetPositions.Add(Vector3.Transform(vertices[v].Position, matrix));
				}
				for (int j = 0; j < indices.Length; j++)
				{
					targetIndices.Add((uint)baseVertex + indices[j]);
				}
			}
		}

		/// <summary>AABB всей env-сцены по сферам мешей инстансов - питает кадрирование камеры и
		/// ортокамеру мирового света.</summary>
		private bool TryComputeSceneBounds(out Vector3 min, out Vector3 max)
		{
			min = new Vector3(float.PositiveInfinity);
			max = new Vector3(float.NegativeInfinity);
			bool any = false;

			foreach (var record in _rendered.Values)
			{
				any |= AccumulateRecordBounds(record, ref min, ref max);
			}

			return FinalizeBounds(any, ref min, ref max);
		}

		/// <summary>AABB одной сущности префаба И её поддерева - фокус по F на выделении (см.
		/// SceneCamera.Frame / FrameSelection). Сущности без своей записи в _rendered (света, группы,
		/// пустышки) в баунды геометрии не попадают - если во всём поддереве не нашлось НИ ОДНОЙ
		/// модели, фолбэком идёт мировая позиция сущности с условным радиусом (иначе F на пустышке
		/// был бы неотличим от щелчка мимо и просто ничего не делал бы).</summary>
		private bool TryComputeEntityBounds(Entity entity, out Vector3 min, out Vector3 max)
		{
			min = new Vector3(float.PositiveInfinity);
			max = new Vector3(float.NegativeInfinity);
			bool any = AccumulateEntityBounds(entity, ref min, ref max);

			if (!any)
			{
				const float fallbackRadius = 0.5f;
				var center = ComputeWorldMatrix(entity).Translation;
				min = center - new Vector3(fallbackRadius);
				max = center + new Vector3(fallbackRadius);
				any = true;
			}

			return FinalizeBounds(any, ref min, ref max);
		}

		private bool AccumulateEntityBounds(Entity entity, ref Vector3 min, ref Vector3 max)
		{
			bool any = _rendered.TryGetValue(entity.Id, out var record) &&
				AccumulateRecordBounds(record, ref min, ref max);

			foreach (var child in entity.ChildEntities)
			{
				any |= AccumulateEntityBounds(child, ref min, ref max);
			}

			return any;
		}

		/// <summary>Общий накопитель AABB одной записи _rendered - сферы мешей её инстансов в мировом
		/// пространстве; общий код TryComputeSceneBounds и TryComputeEntityBounds.</summary>
		private bool AccumulateRecordBounds(RenderedModel record, ref Vector3 min, ref Vector3 max)
		{
			if (!record.Instantiated || string.IsNullOrEmpty(record.ResolvedPath) ||
				!_models.TryGetValue(record.ResolvedPath, out var state) || state.Model == null)
			{
				return false;
			}

			var model = state.Model;
			bool any = false;
			for (int i = 0; i < record.EnvEntities.Count; i++)
			{
				var instance = model.instances[record.InstanceIndices[i]];
				if (instance.meshId < 0 || instance.meshId >= model.Meshes.Count)
				{
					continue;
				}

				var mesh = model.Meshes[instance.meshId];
				var t = ComposeInstanceTransform(instance.transform, record.LastWorld);
				var worldCenter = Vector3.Transform(mesh.Center * t.scale, t.rotation) + t.position;
				var maxScale = MathF.Max(MathF.Abs(t.scale.X), MathF.Max(MathF.Abs(t.scale.Y), MathF.Abs(t.scale.Z)));
				var radius = mesh.Radius * maxScale;

				min = Vector3.Min(min, worldCenter - new Vector3(radius));
				max = Vector3.Max(max, worldCenter + new Vector3(radius));
				any = true;
			}

			return any;
		}

		/// <summary>NaN/Infinity в трансформах не должны ронять кадрирование - общий хвост
		/// TryComputeSceneBounds/TryComputeEntityBounds.</summary>
		private static bool FinalizeBounds(bool any, ref Vector3 min, ref Vector3 max)
		{
			if (!any)
			{
				return false;
			}

			if (float.IsNaN(min.X) || float.IsInfinity(min.X) || float.IsNaN(max.X) || float.IsInfinity(max.X) ||
				float.IsNaN(min.Y) || float.IsInfinity(min.Y) || float.IsNaN(max.Y) || float.IsInfinity(max.Y) ||
				float.IsNaN(min.Z) || float.IsInfinity(min.Z) || float.IsNaN(max.Z) || float.IsInfinity(max.Z))
			{
				return false;
			}

			return true;
		}

		/// <summary>Баунды каскада теней мирового света - пересчитываются при любом изменении сцены
		/// (см. SimpleCullingAndRenderSystem.BuildLightData).</summary>
		private void UpdateShadowBounds()
		{
			if (_env.ShadowSettings == null || !TryComputeSceneBounds(out var min, out var max))
			{
				return;
			}

			_env.ShadowSettings.BoundsCenter = (min + max) * 0.5f;
			_env.ShadowSettings.BoundsRadius = MathF.Max(0.05f, (max - min).Length() * 0.5f);
		}

	}
}
