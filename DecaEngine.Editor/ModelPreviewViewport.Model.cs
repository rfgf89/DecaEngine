using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DecaEngine.Core;
using DecaEngine.Core.Diagnostics;
using DecaEngine.Editor.ECS;
using DecaEngine.Graphics;
using DecaEngine.Graphics.Diligent;
using DecaEngine.Graphics.ProbeGi;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using Hexa.NET.ImGui;
using DecaEngine.Scene;
using DecaEngine.Animation;

namespace DecaEngine.Editor
{
	/// <summary>Загрузка модели: заявка в столе, стриминг, инстансы, RT-сцена теней. Часть <see cref="ModelPreviewViewport"/> - файл на тему;
	/// состояние, конструктор и кадровые Update/Render живут в основном файле.</summary>
	public partial class ModelPreviewViewport
	{
		/// <summary>
		/// ????????? .gltf/.glb ?????? ?? ?????????? ???? ? ??????????? EntityStore ????? ??????.
		/// ?? ?????? ??????, ???? ???? ????????? ? ??? ???????????. ?????? ???????? (????? ????,
		/// ?? ?????? ? ?.?.) ?? ????????? ?????? - ??. <see cref="LoadError"/>.
		/// </summary>
		public void LoadModel(string modelPath, int subMeshIndex = -1)
		{
			// Ключ загрузки - пара (путь, сабмеш): та же модель с другим выбранным сабмешем
			// должна перезагрузиться (точнее, перенаселить сцену только этим сабмешем).
			if ((string.Equals(_loadedPath, modelPath, StringComparison.OrdinalIgnoreCase) && _loadedSubMesh == subMeshIndex) ||
			    (string.Equals(_loadingPath, modelPath, StringComparison.OrdinalIgnoreCase) && _loadingSubMesh == subMeshIndex))
			{
				return;
			}

			// Та же модель, что уже резидентна с предыдущего вызова (просто другой сабмеш выбран) -
			// файл уже распарсен и его меши/материалы уже зарегистрированы в _env.BatchRenderer, так
			// что достаточно перенаселить сцену, без диска, фоновой задачи и лоадер-хендла.
			if (_residentModel != null && string.Equals(_residentPath, modelPath, StringComparison.OrdinalIgnoreCase))
			{
				CancelPendingLoad();
				ClearInstances();

				try
				{
					ResetPreviewModeForNewSelection();
					PopulateFromScene(_residentModel, subMeshIndex);

					// See the matching comment in PollPendingLoad below - new batches were just
					// registered for this sub-mesh selection and the render graph must be recompiled
					// to pick them up.
					_env.DilApi.ImmediateContext.Flush();
					_env.DilApi.ImmediateContext.WaitForIdle();
					_env.Pipeline.InvalidateGraph();

					// AO/GI world-range (см. FrameAll) - только теперь, после барьера выше, той же
					// причине, что и в PollPendingLoad.
					_env.SetAoWorldRange(AoWorldRange());
					_env.SetAoStrength(Math.Clamp(_editorSettings.AoStrength, 0.1f, 4f),
						Math.Clamp(_editorSettings.AoFloor, 0f, 1f));
					_env.SetAoDebugView(_editorSettings.AoDebugView);
					ApplyGiSettings(pushRange: true);

					_loadedPath = modelPath;
					_loadedSubMesh = subMeshIndex;
					_loadError = null;
					ApplyPreviewSettingsToMaterials();
				}
				catch (Exception ex)
				{
					_loadedPath = null;
					_loadError = ex.Message;
					EngineLog.Add(LogLevel.Error, $"Model preview: failed to switch sub-mesh for '{modelPath}': {ex.Message}");
				}

				return;
			}

			CancelPendingLoad();

			// Возврат из паузы резидента заведомо не имеет (мы сами его отдали при уходе) - это
			// штатный путь, а не потерянный кеш, о котором предупреждает диагностика ниже.
			if (!_restoringAfterResume)
			{
				EngineLog.Add(LogLevel.Warning,
					$"Model preview: FULL reload for '{modelPath}' subMesh={subMeshIndex} " +
					$"(resident was '{_residentPath}', model={(_residentModel is null ? "null" : "loaded")}) - " +
					"resident path did not match, re-parsing from disk instead of reusing it.");
			}

			UnloadResidentModel();

			// Сама загрузка стартует из ModelStreamingSystem (в SystemRoot окружения) ближайшим
			// кадром; готовность опрашивает PollPendingLoad. Ошибки (файл пропал, битый glTF)
			// приходят через Resident.Failed тем же путём.
			_streamingModel = _streamer.Acquire(modelPath, _orbitTarget);
			_loadingPath = modelPath;
			_loadingSubMesh = subMeshIndex;
			_loadError = null;
		}

		/// <summary>
		/// Полностью снимает текущую модель превью с GPU: инстансы, резидентную модель, регистрации в
		/// батч-рендерере и пробы. «Сначала очистить предыдущее, потом грузить новое»: контент
		/// прошлого выбора не висит на GPU всё время фоновой загрузки следующего. ClearAll стримера
		/// выполняет обязательный протокол освобождения (барьер GPU -> сброс регистраций
		/// батч-рендерера -> Release модели -> пересборка графа - см. комментарии в PopulateFromScene
		/// про мега-буферы и замороженные команды). Зовётся из <see cref="LoadModel"/> перед новой
		/// загрузкой и из <see cref="ApplyPendingActiveChange"/>, когда превью уходит в паузу (модель
		/// в этот момент обязана остаться ровно одна на редактор - см. <see cref="SetActive"/>).
		/// Вызывать ТОЛЬКО под GPU-локом редактора: внутри есть Flush/WaitForIdle.
		/// </summary>
		private void UnloadResidentModel()
		{
			ClearInstances();

			// Фоновая сборка BVH под пробы могла ещё читать геометрию старой модели, а ClearAll ниже
			// её освободит - ждём ДО освобождения (ResetProbeGi зовётся уже после и на этом месте
			// был бы поздно).
			WaitProbeBakerTask();

			// Ссылка на резидентную модель прошлого выбора - отпустить до ClearAll.
			if (_streamingModel != null)
			{
				_streamer.Release(_streamingModel);
				_streamingModel = null;
			}

			_streamer.ClearAll();
			_rtShadowScene?.Release();
			_rtShadowScene = null;
			_residentModel = null;
			_residentPath = null;
			_meshIdMap.Clear();
			_materialIdMap.Clear();
			_batchCache.Clear();
			// Wireframe-материал регистрировался в батч-рендерере - его регистрация умерла со
			// сбросом в ClearAll; сам объект освобождаем (GPU уже дождались там же) и пересоздадим
			// лениво в EnsureWireframeMaterial.
			_wireframeMaterial?.Release();
			_wireframeMaterial = null;
			_wireframeMaterialId = null;
			_wireframeBatchCache.Clear();

			// Куб отладочного BVH был зарегистрирован в ТОМ ЖЕ батч-рендерере, чьи регистрации
			// только что сброшены: его MeshId/BatchId стали недействительны, а сам меш надо
			// освободить (его GPU-буферы больше никому не принадлежат).
			ReleaseBvhDebugResources();

			_loadedPath = null;
			_loadedSubMesh = -1;

			// Пробы ссылались на материалы/BVH только что освобождённой модели - сброс за барьером,
			// который ClearAll уже сделал.
			ResetProbeGi();
		}

		/// <summary>Опции загрузки для стримера - прежние опции прямого BeginLoadAsync. Фабрика, а не
		/// снимок: MaxTextureSize/анизотропию пользователь меняет между загрузками. Кламп
		/// MaxTextureSize - это ПИКОВАЯ память загрузки: все текстуры модели декодируются разом и
		/// лежат несжатыми до самой заливки (см. EditorSettings.PreviewMaxTextureSize).</summary>
		private ModelLoadOptions BuildLoadOptions() =>
			ViewportSettingsPush.BuildLoadOptions(_editorSettings, RtShadowsEnabled());

		/// <summary>Строит TLAS RT-теней по резидентной модели и привязывает его её материалам.
		/// No-op вне режима «Ray-traced» и без модели. Прежняя структура освобождается: смена
		/// модели/сабмеша меняет набор мешей, а BLAS-кэш DiligentRayTracingScene ключуется мешами
		/// умершей модели. Вызывать только после барьера (Flush + WaitForIdle).</summary>
		private void UpdateRtShadowScene()
		{
			_rtShadowScene?.Release();
			_rtShadowScene = null;

			if (!RtShadowsEnabled() || _residentModel == null)
			{
				return;
			}

			var instances = new List<DiligentRayTracingScene.Instance>();
			foreach (var instance in _residentModel.instances)
			{
				if (instance.meshId < 0 || instance.meshId >= _residentModel.Meshes.Count ||
					_residentModel.Meshes[instance.meshId] is not DiligentMesh mesh ||
					mesh.IndexCount < 3 || mesh.VertexBuffer == null || mesh.IndexBuffer == null)
				{
					continue;
				}

				var t = instance.transform;
				instances.Add(new DiligentRayTracingScene.Instance(mesh,
					Matrix4x4.CreateScale(t.scale.X, t.scale.Y, t.scale.Z) *
					Matrix4x4.CreateFromQuaternion(t.rotation) *
					Matrix4x4.CreateTranslation(t.position),
					(uint)instances.Count));
			}

			if (instances.Count == 0)
			{
				return;
			}

			_rtShadowScene = new DiligentRayTracingScene(_env.DilApi);
			_rtShadowScene.Rebuild(instances);
			if (_rtShadowScene.Tlas == null)
			{
				return;
			}

			for (int i = 0; i < _residentModel.materialObjects.Count; i++)
			{
				if (_residentModel.materialObjects.GetAt(i).Value is DiligentMaterial material)
				{
					material.SetAccelStructure("_SceneTlas", _rtShadowScene.Tlas);
				}
			}
		}

		/// <summary>Потолок текстуры в том виде, в каком он уходит в загрузчик. Отдельным методом,
		/// потому что то же значение сравнивает диф перезагрузки: сравнивать сырую настройку с
		/// заклампленной - значит вечно видеть расхождение на значениях вне [128, 8192].</summary>
		private int ClampedMaxTextureSize() => ViewportSettingsPush.ClampedMaxTextureSize(_editorSettings);

		/// <summary>
		/// Cancels and releases the in-flight background load, if any - the background Task.Run in
		/// ModelImporter.PrepareModel checks the token between phases, so this actually stops it from
		/// continuing to burn CPU decoding textures for a model/sub-mesh selection the user has already
		/// moved on from, instead of just forgetting the reference and letting it run to completion
		/// unobserved.
		/// </summary>
		private void CancelPendingLoad()
		{
			// Отпускаем ссылку ТОЛЬКО если загрузка ещё шла: после готовности _streamingModel - это
			// ссылка, удерживающая РЕЗИДЕНТНУЮ модель от выселения стримером (переключение сабмеша
			// зовёт CancelPendingLoad и не должно её терять). Стример сам отменит фоновую задачу
			// (CancellationToken проверяется между фазами PrepareModel - декод реально остановится)
			// ближайшим Tick-ом, увидев ноль ссылок; хендл статуса закрывается там же.
			if (_loadingPath != null && _streamingModel != null)
			{
				_streamer.Release(_streamingModel);
				_streamingModel = null;
			}

			_loadingPath = null;
			_loadingSubMesh = -1;
		}

		/// <summary>Опрос стриминга текущего выбора. Саму загрузку (фоновый Prepare, покадровую
		/// финализацию порциями - дисциплину upload-хипа, регистрацию в батч-рендерере) ведёт
		/// <see cref="ModelStreamer"/> из ModelStreamingSystem; здесь - только реакция на готовность:
		/// перенос словарей регистраций, население сцены и пост-обвязка (AO/GI/материалы).</summary>
		private void PollPendingLoad()
		{
			if (_streamingModel == null || _loadingPath == null)
			{
				return;
			}

			var state = _streamingModel;

			if (state.Failed)
			{
				var failedPath = _loadingPath;
				var message = state.Error!;
				CancelPendingLoad();
				_loadedPath = null;
				_loadError = message;
				EngineLog.Add(LogLevel.Error, $"Model preview: failed to load '{failedPath}': {message}");
				return;
			}

			if (!state.Ready)
			{
				return;
			}

			var modelPath = _loadingPath;
			var subMeshIndex = _loadingSubMesh;
			_loadingPath = null;
			_loadingSubMesh = -1;

			ClearInstances();

			try
			{
				ResetPreviewModeForNewSelection();

				// Модель уже зарегистрирована стримером - переносим его словари регистраций в поля
				// вьюпорта (ими пользуются PopulateFromScene/wireframe) и объявляем модель резидентной
				// ДО населения: PopulateFromScene по ReferenceEquals поймёт, что регистрировать заново
				// нечего.
				_residentModel = state.Model;
				_residentPath = modelPath;
				_meshIdMap.Clear();
				_materialIdMap.Clear();
				_batchCache.Clear();
				foreach (var kvp in state.MeshIds)
				{
					_meshIdMap[kvp.Key] = kvp.Value;
				}
				foreach (var kvp in state.MaterialIds)
				{
					_materialIdMap[kvp.Key] = kvp.Value;
				}

				PopulateFromScene(state.Model!, subMeshIndex);

				// New batches were just registered in _batchRenderer for this model/sub-mesh, but
				// the render graph's ForwardPass commands are frozen after the first Compile() and
				// merely replayed on every Execute() (see IRenderGraph.Invalidate) - without this,
				// switching model/sub-mesh keeps drawing whatever batch set existed when the graph
				// was first compiled instead of the newly loaded one.
				//
				// Recompiling disposes and recreates every native resource the graph pinned (e.g.
				// ShadowPass's shadow maps) - same hazard as ResizeTargets below: with no
				// frame-in-flight fence in this engine, disposing GPU resources the previous frame's
				// (still in-flight) commands might reference races the GPU and can crash the driver.
				// Flush()+WaitForIdle() must run first, exactly as ResizeTargets does.
				_env.DilApi.ImmediateContext.Flush();
				_env.DilApi.ImmediateContext.WaitForIdle();
				_env.Pipeline.InvalidateGraph();

				// AO/GI world-range (см. FrameAll) - только теперь, после барьера выше: SetConstant
				// трогает ImmediateContext и метит AoMaterial dirty (пересборка PSO на следующий
				// draw), это небезопасно, пока предыдущий кадр ещё может быть в полёте.
				_env.SetAoWorldRange(AoWorldRange());
				_env.SetAoStrength(Math.Clamp(_editorSettings.AoStrength, 0.1f, 4f),
					Math.Clamp(_editorSettings.AoFloor, 0f, 1f));
				_env.SetAoDebugView(_editorSettings.AoDebugView);
				ApplyGiSettings(pushRange: true);

				_loadedPath = modelPath;
				_loadedSubMesh = subMeshIndex;
				_loadError = null;
				ApplyPreviewSettingsToMaterials();

				// TLAS RT-теней - после барьера выше (сборка BLAS/TLAS трогает ImmediateContext)
				// и строго ДО первого дроу: вариант с FEATURE_RT_SHADOWS объявляет _SceneTlas, и
				// коммит ресурсов без привязки упёрся бы в пустой дескриптор.
				UpdateRtShadowScene();

				// RT-фолбэк SSR мог ждать модель (собственный accel строится от неё) - фичи
				// перечитываются здесь же; внутри и сборка accel-а, и привязка TLAS.
				if (_editorSettings.PreviewSsr && _editorSettings.SsrRayTraced)
				{
					ApplyPipelineFeatures();
				}

				// Probe-GI пересчитывается под новую модель: сброс безопасен - барьер выше уже
				// дождался GPU, а раунды пойдут из PollProbeBake.
				ResetProbeGi();
				RequestProbeSession(delaySeconds: 0f);
			}
			catch (Exception ex)
			{
				_loadedPath = null;
				_loadError = ex.Message;
				EngineLog.Add(LogLevel.Error, $"Model preview: failed to load '{modelPath}': {ex.Message}");
			}
		}

		/// <summary>
		/// subMeshIndex &gt;= 0 - показываем только инстансы этого сабмеша, иначе всю модель. Сабмеш
		/// без единого инстанса (неиспользуемый меш в glTF) остаётся пустым - HasModel вернёт false
		/// и Render покажет "No model loaded" вместо синтетического инстанса.
		/// </summary>
		private void PopulateFromScene(ModelLoader modelLoader, int subMeshIndex = -1)
		{
			// Жизненный цикл модели теперь у ModelStreamer: регистрацию ресурсов он делает при
			// готовности загрузки, освобождение предыдущей модели - в LoadModel через ClearAll
			// (барьер GPU -> сброс регистраций -> Release -> пересборка графа; прежде этот протокол
			// жил здесь). Сюда модель приходит уже резидентной: PollPendingLoad переносит словари
			// регистраций и выставляет _residentModel ДО вызова, сабмеш-путь LoadModel передаёт
			// _residentModel сам. Чужая модель - нарушение жизненного цикла стриминга.
			if (!ReferenceEquals(modelLoader, _residentModel))
			{
				EngineLog.Add(LogLevel.Error,
					"Model preview: PopulateFromScene called with a non-resident model - streaming lifecycle violated, skipping.");
				return;
			}

			var instances = new List<InstanceData>(modelLoader.instances.Count);
			foreach (var candidate in modelLoader.instances)
			{
				if (subMeshIndex < 0 || candidate.meshId == subMeshIndex)
				{
					instances.Add(candidate);
				}
			}

			for (int i = 0; i < instances.Count; i++)
			{
				var instance = instances[i];
				var entity = ModelViewportGeometry.CreateInstanceEntity(_env.Store, _env.ResourceManager,
					_env.BatchRenderer, _meshIdMap, _materialIdMap, _batchCache,
					instance.meshId, instance.materialId, instance.transform);
				if (entity != null)
				{
					_instanceEntities.Add(entity.Value);
				}
			}

			// Framing must be based on the actual MESH geometry bounds of ALL sub-meshes/instances
			// (Scene.ComputeBounds, using Scene.Meshes[i].Center/Radius, computed by
			// MeshUtility.RecalculateBounds when the model was loaded, see Scene.cs) - a model
			// almost always consists of multiple sub-meshes/nodes, so a single mesh's bound is not
			// enough on its own; a mesh whose geometry is offset from its local origin (very common
			// for glTF nodes) would otherwise make the orbit target sit next to the model instead of
			// at its actual visual center, so the camera would circle some empty point beside it
			// rather than fully around it.

			// Для одиночного сабмеша считаем bounds только по ЕГО инстансам (аналог
			// ModelLoader.ComputeBounds, но с фильтром) - иначе камера кадрировала бы всю модель,
			// а маленький сабмеш где-нибудь с краю был бы едва различим.
			Vector3 boundsMin, boundsMax;
			if (subMeshIndex < 0)
			{
				(boundsMin, boundsMax) = modelLoader.ComputeBounds();
			}
			else
			{
				(boundsMin, boundsMax) = ModelViewportGeometry.ComputeSubMeshBounds(modelLoader, subMeshIndex);
			}

			// ??????????? ??? ??????? ??????? ? ???????
			EngineLog.Add(LogLevel.Info,
				$"Model preview bounds: min={boundsMin}, max={boundsMax}, instances={_instanceEntities.Count}");

			FrameAll(boundsMin, boundsMax);
		}


		private void ClearInstances()
		{
			foreach (var entity in _instanceEntities)
			{
				_env.ResourceManager.UnregisterRenderable(entity);
				entity.DeleteEntity();
			}
			_instanceEntities.Clear();
			ClearWireframeOverlay();

			// И боксы отладочного BVH: они такие же инстансы батч-рендерера, и снимать их надо
			// ЗДЕСЬ - то есть до любого сброса регистраций, пока их BatchId валиден. Иначе после
			// смены модели они продолжают рисоваться, подхватив геометрию новой модели.
			ClearBvhDebugOverlay();
			_bvhDebugState = default;
		}

		/// <summary>Цвет/интенсивность солнца для бейка проб: тот же keyIntensity, что у
		/// аналитического мирового света (ProbeGiParams.z в UnlitInstancedPS.hlsl) - иначе баунс не
		/// сойдётся по яркости с прямым светом: ярче - тень заливается эмбиентом, тусклее - отскок
		/// проваливается.</summary>
		private Vector3 ProbeSunColor() => ViewportSettingsPush.ProbeSunColor(_editorSettings);

	}
}
