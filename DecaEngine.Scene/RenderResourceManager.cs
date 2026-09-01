using System.Numerics;
using DecaEngine.Graphics;
using Friflo.Engine.ECS;
using UnsafeCollections.Collections.Native;
using UnsafeCollections.Collections.Unsafe;

namespace DecaEngine.Scene;

public class RenderResourceManager
{
	private readonly IBatchRenderer _batchRenderer;
	private readonly NativeStack<int> _freeSlots;
	private readonly EntityStore _store;
	private int[] _batchCounts;

	public int totalInstances;
	public int totalFreeSlot;
	public bool cullFrustum;

	private BatchSubset _renderSubset;

	// Верхняя граница ЗАНЯТЫХ слотов (максимальный выданный индекс + 1), а не их количество.
	// Слоты выдаются из стека свободных, поэтому занятые индексы РАЗРЕЖЕНЫ: после освобождения
	// пачки слотов стек отдаёт их в обратном порядке, и следующая, более мелкая пачка инстансов
	// садится в ХВОСТ диапазона (например слоты 8..9 при 10 освобождённых). Culling-шейдер
	// (BatchingInstancingCS) обходит Instances[0..drawCount) и пропускает дырки по batchId/objectId
	// < 0, так что drawCount - это граница ИНДЕКСА. Если подставить туда количество занятых слотов
	// (totalInstances - totalFreeSlot), шейдер обойдёт только начало массива, где после churn-а
	// лежат одни дырки, и не нарисует ничего - именно так пропадали превью сабмешей в
	// AssetBrowser/Inspector после бейка целой модели (см. ModelIconBaker.CreateStageEntities).
	private int _slotHighWaterMark;

	/// <summary>
	/// Сколько первых слотов массива инстансов должен обойти culling-шейдер - см.
	/// <see cref="_slotHighWaterMark"/>. Подставляется в CullData.drawCount
	/// (<see cref="CullingAndRenderSystem"/> и упрощённый SimpleCullingAndRenderSystem,
	/// оставшийся на стороне редактора вместе с настройками теней превью).
	/// </summary>
	public int DrawInstanceCount => _slotHighWaterMark;

	public RenderResourceManager(int totalInstances, int totalBatch, EntityStore store,
		IBatchRenderer batchRenderer)
	{
		_store = store;
		_batchRenderer = batchRenderer;
		this.totalInstances = totalInstances;
		_batchCounts = new int[totalBatch];

		_renderSubset = new BatchSubset
		{
			instances = new NativeArray<IndirectInstance>(totalInstances),
			gpuData = new NativeArray<GPURenderInstance>(totalInstances),
			drawData = new NativeArray<DrawData>(totalInstances),

			countData = new NativeArray<int>(totalBatch)
		};

		for (var i = 0; i < totalInstances; i++)
		{
			_renderSubset.instances[i] = new IndirectInstance { batchId = new BatchId(-1), objectId = -1 };
		}

		_batchRenderer.PinInstances(_renderSubset);

		_freeSlots = new NativeStack<int>(totalInstances);

		for (var i = 0; i < totalInstances; i++)
		{
			_freeSlots.Push(totalInstances - 1 - i);
		}

		// Подстраховка от утечки GPU-слотов: если сущность с BatchRenderInfo удаляют напрямую
		// (Entity.DeleteEntity), минуя UnregisterRenderable (например InspectorWindow при удалении
		// объекта из иерархии), её слот раньше никогда не возвращался в _freeSlots - разрежение
		// росло безвозвратно, а верхняя граница диспатча (_slotHighWaterMark) не опускалась.
		// OnEntityDelete стреляет ДО фактического удаления (сущность ещё жива и с компонентами -
		// см. документацию Friflo), так что можно прочитать GpuSlotIndex и освободить слот тем же
		// путём, что и UnregisterRenderable. Для сущностей, уже отцепленных через
		// UnregisterRenderable (обычный протокол вьюпортов/стримера), TryGetComponent тут вернёт
		// false - двойного освобождения нет.
		_store.OnEntityDelete += OnEntityDeleting;
	}

	private void OnEntityDeleting(EntityDelete args)
	{
		if (args.Entity.TryGetComponent<BatchRenderInfo>(out var batchInfo))
		{
			FreeSlot(batchInfo.GpuSlotIndex, batchInfo.BatchId);
		}
	}

	/// <summary>Поджимает верхнюю границу диспатча (<see cref="_slotHighWaterMark"/>) под фактически
	/// занятые слоты, схлопывая освободившийся хвост. Вызывается сама из <see cref="FreeSlot"/> при
	/// каждом освобождении слота, так что отдельный вызов обычно не нужен - метод публичный и
	/// идемпотентный на случай, если слоты когда-нибудь освободятся в обход FreeSlot.</summary>
	public void RecycleDeadHandles()
	{
		while (_slotHighWaterMark > 0 && _renderSubset.instances[_slotHighWaterMark - 1].batchId.batchId < 0)
		{
			_slotHighWaterMark--;
		}
	}

	/// <summary>Общий путь освобождения одного GPU-слота инстанса: возвращает индекс в
	/// <see cref="_freeSlots"/>, гасит запись в <see cref="_renderSubset"/> (иначе куллинг-шейдер
	/// продолжил бы читать чужой/устаревший инстанс по этому индексу), откатывает
	/// префикс-суммы <see cref="_batchCounts"/>/countData и поджимает хвост диспатча. Используется
	/// и из <see cref="UnregisterRenderable"/> (явное снятие), и из <see cref="OnEntityDeleting"/>
	/// (сущность удалена напрямую, в обход UnregisterRenderable).</summary>
	private unsafe void FreeSlot(int slotIndex, BatchId batchId)
	{
		_freeSlots.Push(slotIndex);
		totalFreeSlot = _freeSlots.Count;

		_renderSubset.instances[slotIndex] = new IndirectInstance { batchId = new BatchId(-1), objectId = -1 };

		if (batchId.batchId >= 0 && batchId.batchId < _batchCounts.Length)
		{
			_batchCounts[batchId.batchId]--;
			for (var i = batchId.batchId + 1; i < _batchCounts.Length; i++)
			{
				_renderSubset.countData.GetRef(i)--;
			}
		}

		RecycleDeadHandles();

		_batchRenderer.MarkInstancesContentDirty();
	}

	public unsafe bool RegisterRenderable(Entity entity, BatchId batchId)
	{
		if (_freeSlots.Count == 0)
		{
			int newInstances = totalInstances == 0 ? 128 : totalInstances * 2;
			var newInstancesArray = new NativeArray<IndirectInstance>(newInstances);
			var newGpuData = new NativeArray<GPURenderInstance>(newInstances);
			var newDrawData = new NativeArray<DrawData>(newInstances);

			if (totalInstances > 0)
			{
				NativeArray<IndirectInstance>.Copy(_renderSubset.instances, newInstancesArray);
				NativeArray<GPURenderInstance>.Copy(_renderSubset.gpuData, newGpuData);
				NativeArray<DrawData>.Copy(_renderSubset.drawData, newDrawData);

				_renderSubset.instances.Dispose();
				_renderSubset.gpuData.Dispose();
				_renderSubset.drawData.Dispose();
			}

			for (var i = totalInstances; i < newInstances; i++)
			{
				newInstancesArray[i] = new IndirectInstance { batchId = new BatchId(-1), objectId = -1 };
				_freeSlots.Push(newInstances - 1 - (i - totalInstances));
			}

			_renderSubset.instances = newInstancesArray;
			_renderSubset.gpuData = newGpuData;
			_renderSubset.drawData = newDrawData;
			totalInstances = newInstances;

			if (totalInstances > 0)
			{
				_store.Query<BatchRenderInfo, LinkDrawInfo>().ForEachEntity((ref BatchRenderInfo batchInfo, ref LinkDrawInfo linkInfo, Entity ent) =>
				{
					linkInfo.renderInstance = UnsafeArray.GetPtr<GPURenderInstance>(_renderSubset.gpuData.GetNative(), batchInfo.GpuSlotIndex);
					linkInfo.drawData = UnsafeArray.GetPtr<DrawData>(_renderSubset.drawData.GetNative(), batchInfo.GpuSlotIndex);
				});
			}

			_batchRenderer.PinInstances(_renderSubset);
		}

		var slotIndex = _freeSlots.Pop();
		totalFreeSlot = _freeSlots.Count;

		if (slotIndex + 1 > _slotHighWaterMark)
		{
			_slotHighWaterMark = slotIndex + 1;
		}

		_renderSubset.instances[slotIndex] = new IndirectInstance
		{
			batchId = batchId,
			objectId = slotIndex
		};

		if (batchId.batchId >= 0 && batchId.batchId >= _batchCounts.Length)
		{
			int oldBatchCountsLength = _batchCounts.Length;
			int newBatchCountsLength = _batchCounts.Length == 0 ? 128 : _batchCounts.Length;
			while (batchId.batchId >= newBatchCountsLength)
			{
				newBatchCountsLength *= 2;
			}

			Array.Resize(ref _batchCounts, newBatchCountsLength);
			var newCountData = new NativeArray<int>(newBatchCountsLength);

			if (_renderSubset.countData.Length > 0)
			{
				NativeArray<int>.Copy(_renderSubset.countData, newCountData);
				_renderSubset.countData.Dispose();
			}

			// countData - префикс-сумма (стартовый слот инстансов батча i = число инстансов у
			// батчей с id < i). НОВЫЕ хвостовые записи обязаны стартовать с текущей суммарной
			// заселённости, а не с нуля: нулевой хвост означал бы, что батчи с id >= старой
			// ёмкости получают офсеты, НАЛОЖЕННЫЕ на диапазоны первых батчей - куллинг-шейдер
			// писал бы их инстансы поверх чужих, а индирект-дроу читал бы чужие слоты (виден был
			// бы случайный поднабор сцены; вылезло на PrimitiveModeNormalsTest - 25 батчей при
			// стартовой ёмкости 16, см. ModelViewportEnvironment).
			int registeredTotal = 0;
			for (int i = 0; i < oldBatchCountsLength; i++)
			{
				registeredTotal += _batchCounts[i];
			}
			for (int i = oldBatchCountsLength; i < newBatchCountsLength; i++)
			{
				newCountData.GetRef(i) = registeredTotal;
			}

			_renderSubset.countData = newCountData;
			_batchRenderer.PinInstances(_renderSubset);
		}

		if (batchId.batchId >= 0)
		{
			_batchCounts[batchId.batchId]++;

			for (var i = batchId.batchId + 1; i < _batchCounts.Length; i++)
			{
				_renderSubset.countData.GetRef(i)++;
			}
		}

		entity.Add(new BatchRenderInfo
			{
				BatchId = batchId,
				GpuSlotIndex = slotIndex
			},
			new LinkDrawInfo
			{
				renderInstance = UnsafeArray.GetPtr<GPURenderInstance>(_renderSubset.gpuData.GetNative(), slotIndex),
				drawData = UnsafeArray.GetPtr<DrawData>(_renderSubset.drawData.GetNative(), slotIndex)
			});

		_batchRenderer.MarkInstancesContentDirty();

		return true;
	}

	public unsafe void UnregisterRenderable(Entity entity)
	{
		if (entity.TryGetComponent<BatchRenderInfo>(out var batchInfo))
		{
			FreeSlot(batchInfo.GpuSlotIndex, batchInfo.BatchId);

			entity.RemoveComponent<BatchRenderInfo>();
			entity.RemoveComponent<LinkDrawInfo>();
		}
	}
}