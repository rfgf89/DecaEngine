// Compute-скиннинг: bind-поза из мега-буфера вершин + палитра матриц -> ДЕФОРМИРОВАННЫЕ вершины в
// другой участок того же мега-буфера.
//
// Почему в буфер, а не в вершиннике. В движке живут RT-тени, SSR и probe GI, которые трассируют
// РЕАЛЬНУЮ геометрию сцены (SceneTrace.hlsl, ProbeGiBvhCache) - скиннинг, сделанный в VS, для них
// не существует, и персонаж отражался бы и затенял в bind-позе. Плюс так не приходится трогать ни
// UnlitInstancedVS.hlsl, ни теневой путь: для них скиннед-инстанс - обычный меш со своим
// baseVertex.
//
// Читаем и пишем ОДИН И ТОТ ЖЕ UAV, но в непересекающиеся участки (исходный меш и приёмник инстанса
// зарегистрированы как разные диапазоны мега-буфера), поэтому синхронизация между потоками не нужна.

#include "Instancing.hlsl"

// Зеркало ModelLoader.Vertex (72 байта). Поля объявлены СКАЛЯРАМИ намеренно: в structured-буфере
// HLSL не даёт вектору пересекать 16-байтную границу и вставил бы padding - float2 uv после float3
// pos (смещение 12) уехал бы на 16, и вся раскладка разъехалась бы с C#-структурой, которая
// упакована плотно. Скаляр выравнивается по 4 и padding не порождает никогда.
struct MegaVertex
{
	float px, py, pz;
	float u, v;
	float nx, ny, nz;
	float tx, ty, tz, tw;
	float cr, cg, cb, ca;
	float u1, v1;
};

// Зеркало SkinVertex (16 байт): по два ushort в uint, little-endian (см. SkinningData.cs).
struct SkinInfluence
{
	uint joints01;
	uint joints23;
	uint weights01;
	uint weights23;
};

// Зеркало SkinRegion (SkinningPass.cs). Один регион - один скиннед-инстанс.
struct SkinRegion
{
	uint sourceBaseVertex; // начало bind-позы меша в мега-буфере
	uint destBaseVertex;   // начало приёмника этого инстанса
	uint vertexCount;
	uint skinBase;         // начало скин-стрима меша в SkinStream

	uint paletteOffset;    // начало палитры инстанса, В МАТРИЦАХ
	uint firstThread;      // глобальный индекс первого потока региона (префиксная сумма vertexCount)
	uint pad0, pad1;
};

// ВСЕ буферы прохода - RW, даже те, из которых шейдер только читает: DiligentComputeMaterial
// биндит компьютным переменным ТОЛЬКО UAV-виды (см. GetViewFlags), а на D3D12 SRV (t#) и UAV (u#) -
// разные типы дескрипторов, и UAV-вид, положенный в SRV-слот, читается как мусорный адрес -> отказ
// устройства прямо на диспетчеризации. На Vulkan оба вида ложатся в один STORAGE_BUFFER, поэтому
// там расхождение не проявлялось вовсе.
RWStructuredBuffer<MegaVertex> MegaVertices;
RWStructuredBuffer<SkinInfluence> SkinStream;

// Палитра лежит ЧЕТВЁРКАМИ float4, а не матрицами: majorness матрицы внутри элемента структурного
// буфера не подчиняется PackMatrixRowMajor и отличается у D3D12 и Vulkan - ровно на этом когда-то
// молча ломались тени punctual-светов на D3D12 (см. DiligentBatchRenderer, PunctualShadowMatrices).
RWStructuredBuffer<float4> SkinPalette;
RWStructuredBuffer<SkinRegion> SkinRegions;

cbuffer SkinConstants
{
	// x - число регионов, y - суммарное число скиннимых вершин (= число полезных потоков).
	uint4 skinParams;
}

float4x4 LoadSkinMatrix(uint matrixIndex)
{
	uint base = matrixIndex * 4;
	return float4x4(SkinPalette[base + 0], SkinPalette[base + 1], SkinPalette[base + 2], SkinPalette[base + 3]);
}

// Регион, которому принадлежит глобальный поток. Бинарный поиск по префиксным суммам: регионов
// единицы-десятки, но одна диспетчеризация на все регионы вместо диспетчеризации на регион нужна,
// чтобы весь скиннинг кадра лежал в ЗАМОРОЖЕННОМ командном буфере (см. DiligentBatchRenderer) -
// иначе на каждого персонажа приходилось бы обновлять константы и перезаписывать команды.
uint FindRegion(uint thread)
{
	uint low = 0;
	uint high = skinParams.x - 1;

	while (low < high)
	{
		uint mid = (low + high + 1) / 2;
		if (SkinRegions[mid].firstThread <= thread)
		{
			low = mid;
		}
		else
		{
			high = mid - 1;
		}
	}

	return low;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
	uint thread = dispatchThreadId.x;
	if (skinParams.x == 0 || thread >= skinParams.y)
	{
		return;
	}

	SkinRegion region = SkinRegions[FindRegion(thread)];
	uint local = thread - region.firstThread;
	if (local >= region.vertexCount)
	{
		return;
	}

	MegaVertex source = MegaVertices[region.sourceBaseVertex + local];
	SkinInfluence influence = SkinStream[region.skinBase + local];

	uint4 joints = uint4(
		influence.joints01 & 0xFFFF, influence.joints01 >> 16,
		influence.joints23 & 0xFFFF, influence.joints23 >> 16);

	// 1/65535, а не 1/65536: вес 65535 обязан давать РОВНО 1 - импортёр нормализует сумму именно к
	// 65535 (см. SkinningData.SkinVertex.WeightScale), и деление на 65536 усаживало бы персонажа на
	// стотысячную долю каждый кадр.
	float4 weights = float4(
		influence.weights01 & 0xFFFF, influence.weights01 >> 16,
		influence.weights23 & 0xFFFF, influence.weights23 >> 16) / 65535.0;

	float4x4 skin = LoadSkinMatrix(region.paletteOffset + joints.x) * weights.x;
	skin += LoadSkinMatrix(region.paletteOffset + joints.y) * weights.y;
	skin += LoadSkinMatrix(region.paletteOffset + joints.z) * weights.z;
	skin += LoadSkinMatrix(region.paletteOffset + joints.w) * weights.w;

	float3 position = mul(float4(source.px, source.py, source.pz, 1.0), skin).xyz;

	// Нормаль и тангент - 3x3 частью, без переноса. Обратно-транспонированная матрица честнее при
	// неравномерном масштабе костей, но считать её на вершину дорого, а риги с неравномерным
	// масштабом костей - редкость; расхождение проявляется только на них.
	float3x3 skin3 = (float3x3)skin;
	float3 normal = normalize(mul(float3(source.nx, source.ny, source.nz), skin3));
	float3 tangent = mul(float3(source.tx, source.ty, source.tz), skin3);

	MegaVertex result = source;
	result.px = position.x;
	result.py = position.y;
	result.pz = position.z;
	result.nx = normal.x;
	result.ny = normal.y;
	result.nz = normal.z;
	result.tx = tangent.x;
	result.ty = tangent.y;
	result.tz = tangent.z;
	// tw (знак битангента) НЕ трогаем: это ориентация базиса, а не направление, и трансформации не
	// подлежит (см. ModelLoader.Vertex.Tangent).

	MegaVertices[region.destBaseVertex + local] = result;
}
