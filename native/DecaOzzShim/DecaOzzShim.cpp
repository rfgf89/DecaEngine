// DecaOzzShim - мост DecaEngine <-> ozz-animation.
//
// Зачем шим вообще. ozz - это C++ с SoA-раскладкой поз (ozz::math::SoaTransform: четыре джойнта в
// одном SIMD-регистре), шаблонными job-ами и собственным аллокатором. Тащить это в C# через
// пословный маршалинг бессмысленно: выигрыш ozz именно в том, что вся поза обсчитывается пачками по
// четыре кости, а поштучный переход границы managed/native съел бы его целиком. Поэтому граница
// проведена по КРУПНЫМ операциям: «просемплируй клип в позу», «сблендь позы», «переведи в модельные
// матрицы». Всё, что между ними, живёт нативно и managed-код не видит вовсе.
//
// Раскладка матриц. ozz::math::Float4x4 - четыре SimdFloat4-столбца, где i-й столбец есть образ i-й
// оси. System.Numerics.Matrix4x4 в строчной конвенции (v * M) хранит ровно то же самое строками.
// Поэтому матрицы копируются ПОБАЙТОВО, без транспонирования - см. DecaOzz_ReadModelMatrices.
//
// Порядок джойнтов. ozz ПЕРЕУПОРЯДОЧИВАЕТ кости при сборке скелета (breadth-first), и его порядок не
// совпадает с нашим. Чтобы связь не была основана на догадках о внутреннем обходе, каждая кость
// уходит в ozz с именем "<исходный индекс>|<имя>", а после сборки префикс читается обратно - так
// шим отдаёт ТОЧНЫЙ remap, не воспроизводя логику ozz у себя (см. DecaOzz_BuildSkeleton).

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "ozz/animation/offline/animation_builder.h"
#include "ozz/animation/offline/raw_animation.h"
#include "ozz/animation/offline/raw_skeleton.h"
#include "ozz/animation/offline/skeleton_builder.h"
#include "ozz/animation/runtime/animation.h"
#include "ozz/animation/runtime/blending_job.h"
#include "ozz/animation/runtime/ik_aim_job.h"
#include "ozz/animation/runtime/ik_two_bone_job.h"
#include "ozz/animation/runtime/local_to_model_job.h"
#include "ozz/animation/runtime/sampling_job.h"
#include "ozz/animation/runtime/skeleton.h"
#include "ozz/base/maths/simd_math.h"
// SimdQuaternion объявлен вперёд в заголовках IK-job-ов, но определён только здесь - без этого
// include компилятор видит неполный тип ровно в тех местах, где нужны коррекции IK.
#include "ozz/base/maths/simd_quaternion.h"
#include "ozz/base/maths/soa_transform.h"
#include "ozz/base/memory/unique_ptr.h"
#include "ozz/base/span.h"

#define DECA_API extern "C" __declspec(dllexport)

namespace {

// Зеркало DecaEngine.Graphics.Transform: TRS одного джойнта в AoS-виде. Граница managed/native
// работает только в AoS - SoA остаётся деталью нативной стороны.
struct DecaOzzTransform {
	float translation[3];
	float rotation[4]; // xyzw
	float scale[3];
};

struct DecaOzzJointDesc {
	const char* name;
	int32_t parent; // -1 у корня; массив ОБЯЗАН быть топологически упорядочен
	DecaOzzTransform bind;
};

// Один ключ дорожки. Каналы приходят раздельными массивами, поэтому ключ несёт только время и
// значение нужной размерности; трансляция и масштаб читают xyz, поворот - xyzw.
struct DecaOzzKey {
	float time;
	float value[4];
};

struct Skeleton {
	ozz::unique_ptr<ozz::animation::Skeleton> skeleton;

	// ozz-индекс -> исходный индекс и обратно. Держим оба: первый нужен на выгрузке результата,
	// второй - на загрузке входных данных (палитра, обратные bind-матрицы, IK-цели).
	std::vector<int32_t> to_source;
	std::vector<int32_t> from_source;
};

struct Animation {
	ozz::unique_ptr<ozz::animation::Animation> animation;
};

// Поза одного персонажа: локальные TRS в SoA + модельные матрицы + контекст семплирования.
// Контекст живёт ЗДЕСЬ, а не создаётся на вызов: в нём лежат курсоры распакованных ключей, ради
// которых ozz и быстр на последовательном воспроизведении - пересоздание убивало бы весь смысл.
struct Pose {
	const Skeleton* owner = nullptr;
	ozz::vector<ozz::math::SoaTransform> locals;
	ozz::vector<ozz::math::Float4x4> models;
	ozz::animation::SamplingJob::Context context;
};

ozz::math::Transform ToOzz(const DecaOzzTransform& source) {
	ozz::math::Transform result;
	result.translation = ozz::math::Float3(source.translation[0], source.translation[1], source.translation[2]);
	result.rotation = ozz::math::Quaternion(source.rotation[0], source.rotation[1], source.rotation[2],
											source.rotation[3]);
	result.scale = ozz::math::Float3(source.scale[0], source.scale[1], source.scale[2]);
	return result;
}

// Рекурсивно материализует поддерево сырого скелета. Рекурсия, а не итерация: RawSkeleton::Joint
// хранит детей вектором ПО ЗНАЧЕНИЮ, и любой push_back в него инвалидирует указатели на уже
// заполненных потомков - итеративная сборка по указателям здесь просто не работает.
void FillRawJoint(ozz::animation::offline::RawSkeleton::Joint& destination, int32_t index,
				  const std::vector<std::vector<int32_t>>& children, const DecaOzzJointDesc* joints) {
	// Префикс с исходным индексом - способ получить точный remap после переупорядочивания ozz
	// (см. шапку файла). Имя остаётся читаемым, что важно при отладке ozz-дампов.
	char prefix[16];
	std::snprintf(prefix, sizeof(prefix), "%d|", index);
	destination.name = ozz::string(prefix) + ozz::string(joints[index].name ? joints[index].name : "");
	destination.transform = ToOzz(joints[index].bind);

	const std::vector<int32_t>& kids = children[index];
	destination.children.resize(kids.size());
	for (size_t i = 0; i < kids.size(); ++i) {
		FillRawJoint(destination.children[i], kids[i], children, joints);
	}
}

// Домножает локальный поворот одной кости на довороты IK. Возни с транспонированием не избежать:
// поза лежит в SoA (по четыре кости в регистре), а коррекция приходит одиночным кватернионом, и
// добраться до его дорожки можно только развернув блок в AoS и свернув обратно. Это ровно тот же
// приём, что в семплах ozz (MultiplySoATransformQuaternion).
void MultiplySoaRotation(ozz::math::SoaTransform& block, int lane, const ozz::math::SimdQuaternion& correction) {
	ozz::math::SimdQuaternion aos[4];
	ozz::math::Transpose4x4(&block.rotation.x, &aos->xyzw);

	aos[lane] = aos[lane] * correction;

	ozz::math::Transpose4x4(&aos->xyzw, &block.rotation.x);
}

// Достаёт rest-позу одного джойнта (ozz-индекс) в AoS-виде. Нужна при сборке клипа: см.
// SeedEmptyChannels.
ozz::math::Transform RestPoseOf(const ozz::animation::Skeleton& skeleton, int joint) {
	const auto rest = skeleton.joint_rest_poses();
	const ozz::math::SoaTransform& block = rest[joint / 4];
	const int lane = joint % 4;

	float translation[3][4], rotation[4][4], scale[3][4];
	ozz::math::StorePtrU(block.translation.x, translation[0]);
	ozz::math::StorePtrU(block.translation.y, translation[1]);
	ozz::math::StorePtrU(block.translation.z, translation[2]);
	ozz::math::StorePtrU(block.rotation.x, rotation[0]);
	ozz::math::StorePtrU(block.rotation.y, rotation[1]);
	ozz::math::StorePtrU(block.rotation.z, rotation[2]);
	ozz::math::StorePtrU(block.rotation.w, rotation[3]);
	ozz::math::StorePtrU(block.scale.x, scale[0]);
	ozz::math::StorePtrU(block.scale.y, scale[1]);
	ozz::math::StorePtrU(block.scale.z, scale[2]);

	ozz::math::Transform result;
	result.translation = ozz::math::Float3(translation[0][lane], translation[1][lane], translation[2][lane]);
	result.rotation =
		ozz::math::Quaternion(rotation[0][lane], rotation[1][lane], rotation[2][lane], rotation[3][lane]);
	result.scale = ozz::math::Float3(scale[0][lane], scale[1][lane], scale[2][lane]);
	return result;
}

// Досевает пустые каналы дорожек ОДНИМ ключом из bind-позы.
//
// Это не мелочь, а обязательный шаг: у ozz канал без ключей означает ЕДИНИЧНОЕ значение, а в glTF
// (и у нашего C#-семплера) неанимированный канал означает «значение из позы узла». Разница ровно в
// том, что кость с одним лишь анимированным поворотом - самый частый случай в персонажных ригах -
// получала бы нулевую трансляцию и уезжала в начало координат родителя. На Fox это давало
// расхождение с C#-семплером в сотню единиц при габаритах модели ~160.
//
// Касается и джойнтов, которых клип не трогает вовсе: без досева они тоже схлопнулись бы в
// единичную трансформацию вместо bind-позы.
void SeedEmptyChannels(ozz::animation::offline::RawAnimation& raw, const ozz::animation::Skeleton& skeleton) {
	for (size_t i = 0; i < raw.tracks.size(); ++i) {
		auto& track = raw.tracks[i];
		if (!track.translations.empty() && !track.rotations.empty() && !track.scales.empty()) {
			continue;
		}

		const ozz::math::Transform rest = RestPoseOf(skeleton, static_cast<int>(i));

		if (track.translations.empty()) {
			track.translations.push_back({0.0f, rest.translation});
		}
		if (track.rotations.empty()) {
			track.rotations.push_back({0.0f, rest.rotation});
		}
		if (track.scales.empty()) {
			track.scales.push_back({0.0f, rest.scale});
		}
	}
}

int32_t ParseSourceIndex(const char* name) {
	if (name == nullptr) {
		return -1;
	}

	const char* separator = std::strchr(name, '|');
	if (separator == nullptr) {
		return -1;
	}

	return static_cast<int32_t>(std::strtol(name, nullptr, 10));
}

} // namespace

// --- Скелет -------------------------------------------------------------------------------------

/// Собирает рантайм-скелет ozz. joints ОБЯЗАН быть топологически упорядочен (родитель раньше
/// ребёнка) - это же требование у PreparedSkeleton на стороне C#.
/// Возвращает handle или nullptr.
DECA_API void* DecaOzz_BuildSkeleton(const DecaOzzJointDesc* joints, int32_t jointCount) {
	if (joints == nullptr || jointCount <= 0) {
		return nullptr;
	}

	std::vector<std::vector<int32_t>> children(static_cast<size_t>(jointCount));
	std::vector<int32_t> roots;

	for (int32_t i = 0; i < jointCount; ++i) {
		const int32_t parent = joints[i].parent;
		if (parent < 0) {
			roots.push_back(i);
		} else if (parent < i) {
			children[static_cast<size_t>(parent)].push_back(i);
		} else {
			// Родитель позже ребёнка - вход не топологичен. Собирать из такого скелет нельзя:
			// получилась бы тихо оборванная иерархия, а не ошибка.
			return nullptr;
		}
	}

	ozz::animation::offline::RawSkeleton raw;
	raw.roots.resize(roots.size());
	for (size_t i = 0; i < roots.size(); ++i) {
		FillRawJoint(raw.roots[i], roots[i], children, joints);
	}

	if (!raw.Validate()) {
		return nullptr;
	}

	ozz::animation::offline::SkeletonBuilder builder;
	auto built = builder(raw);
	if (!built) {
		return nullptr;
	}

	auto* result = new Skeleton();
	result->skeleton = std::move(built);

	const int num = result->skeleton->num_joints();
	result->to_source.assign(static_cast<size_t>(num), -1);
	result->from_source.assign(static_cast<size_t>(jointCount), -1);

	auto names = result->skeleton->joint_names();
	for (int i = 0; i < num; ++i) {
		const int32_t source = ParseSourceIndex(names[i]);
		if (source < 0 || source >= jointCount) {
			delete result;
			return nullptr;
		}

		result->to_source[static_cast<size_t>(i)] = source;
		result->from_source[static_cast<size_t>(source)] = i;
	}

	return result;
}

DECA_API void DecaOzz_ReleaseSkeleton(void* handle) { delete static_cast<Skeleton*>(handle); }

DECA_API int32_t DecaOzz_SkeletonJointCount(void* handle) {
	auto* skeleton = static_cast<Skeleton*>(handle);
	return skeleton != nullptr ? skeleton->skeleton->num_joints() : 0;
}

/// Таблица «ozz-индекс -> исходный индекс», по которой C#-сторона переупорядочивает обратные
/// bind-матрицы и индексы костей в скин-стриме. Без неё палитра уехала бы костями (см. шапку).
DECA_API int32_t DecaOzz_SkeletonRemap(void* handle, int32_t* out, int32_t capacity) {
	auto* skeleton = static_cast<Skeleton*>(handle);
	if (skeleton == nullptr || out == nullptr) {
		return 0;
	}

	const int32_t count = static_cast<int32_t>(skeleton->to_source.size());
	if (capacity < count) {
		return 0;
	}

	std::memcpy(out, skeleton->to_source.data(), sizeof(int32_t) * static_cast<size_t>(count));
	return count;
}

// --- Клип ---------------------------------------------------------------------------------------

/// Собирает рантайм-клип. Дорожки приходят В ИСХОДНОМ порядке джойнтов; шим сам раскладывает их по
/// ozz-порядку через remap скелета - так вызывающему не нужно знать о переупорядочивании вовсе.
/// Каждый канал задан парой (указатель на ключи, число ключей); нулевое число = канал не анимирован,
/// джойнт остаётся в bind-позе.
DECA_API void* DecaOzz_BuildAnimation(void* skeletonHandle, const char* name, float duration,
									  int32_t trackCount, const DecaOzzKey* const* translationKeys,
									  const int32_t* translationCounts, const DecaOzzKey* const* rotationKeys,
									  const int32_t* rotationCounts, const DecaOzzKey* const* scaleKeys,
									  const int32_t* scaleCounts) {
	auto* skeleton = static_cast<Skeleton*>(skeletonHandle);
	if (skeleton == nullptr || duration <= 0.0f || trackCount <= 0) {
		return nullptr;
	}

	const int num = skeleton->skeleton->num_joints();

	ozz::animation::offline::RawAnimation raw;
	raw.name = name != nullptr ? name : "";
	raw.duration = duration;
	raw.tracks.resize(static_cast<size_t>(num));

	for (int32_t source = 0; source < trackCount; ++source) {
		if (source >= static_cast<int32_t>(skeleton->from_source.size())) {
			break;
		}

		const int32_t target = skeleton->from_source[static_cast<size_t>(source)];
		if (target < 0) {
			continue;
		}

		auto& track = raw.tracks[static_cast<size_t>(target)];

		const int32_t translationCount = translationCounts != nullptr ? translationCounts[source] : 0;
		for (int32_t k = 0; k < translationCount; ++k) {
			const DecaOzzKey& key = translationKeys[source][k];
			track.translations.push_back(
				{key.time, ozz::math::Float3(key.value[0], key.value[1], key.value[2])});
		}

		const int32_t rotationCount = rotationCounts != nullptr ? rotationCounts[source] : 0;
		for (int32_t k = 0; k < rotationCount; ++k) {
			const DecaOzzKey& key = rotationKeys[source][k];
			track.rotations.push_back(
				{key.time, ozz::math::Quaternion(key.value[0], key.value[1], key.value[2], key.value[3])});
		}

		const int32_t scaleCount = scaleCounts != nullptr ? scaleCounts[source] : 0;
		for (int32_t k = 0; k < scaleCount; ++k) {
			const DecaOzzKey& key = scaleKeys[source][k];
			track.scales.push_back({key.time, ozz::math::Float3(key.value[0], key.value[1], key.value[2])});
		}
	}

	SeedEmptyChannels(raw, *skeleton->skeleton);

	if (!raw.Validate()) {
		return nullptr;
	}

	ozz::animation::offline::AnimationBuilder builder;
	auto built = builder(raw);
	if (!built) {
		return nullptr;
	}

	auto* result = new Animation();
	result->animation = std::move(built);
	return result;
}

DECA_API void DecaOzz_ReleaseAnimation(void* handle) { delete static_cast<Animation*>(handle); }

DECA_API float DecaOzz_AnimationDuration(void* handle) {
	auto* animation = static_cast<Animation*>(handle);
	return animation != nullptr ? animation->animation->duration() : 0.0f;
}

// --- Поза ---------------------------------------------------------------------------------------

DECA_API void* DecaOzz_CreatePose(void* skeletonHandle) {
	auto* skeleton = static_cast<Skeleton*>(skeletonHandle);
	if (skeleton == nullptr) {
		return nullptr;
	}

	auto* pose = new Pose();
	pose->owner = skeleton;
	pose->locals.resize(static_cast<size_t>(skeleton->skeleton->num_soa_joints()));
	pose->models.resize(static_cast<size_t>(skeleton->skeleton->num_joints()));
	pose->context.Resize(skeleton->skeleton->num_joints());

	// Стартовая поза - bind: до первого семплирования потребитель имеет право читать позу, и
	// нулевые (то есть вырожденные) трансформации дали бы схлопнутого в точку персонажа.
	const auto rest = skeleton->skeleton->joint_rest_poses();
	std::memcpy(pose->locals.data(), rest.data(), rest.size_bytes());

	return pose;
}

DECA_API void DecaOzz_ReleasePose(void* handle) { delete static_cast<Pose*>(handle); }

/// Семплирует клип в локальные TRS позы. ratio - НОРМАЛИЗОВАННОЕ время [0..1] (соглашение ozz), а
/// не секунды: вызывающий делит на длительность сам, потому что он же владеет зацикливанием.
DECA_API int32_t DecaOzz_SamplePose(void* poseHandle, void* animationHandle, float ratio) {
	auto* pose = static_cast<Pose*>(poseHandle);
	auto* animation = static_cast<Animation*>(animationHandle);
	if (pose == nullptr || animation == nullptr) {
		return 0;
	}

	ozz::animation::SamplingJob job;
	job.animation = animation->animation.get();
	job.context = &pose->context;
	job.ratio = ratio;
	job.output = ozz::make_span(pose->locals);

	return job.Run() ? 1 : 0;
}

/// Смешивает позы-слои в приёмник. Веса не нормализуются здесь намеренно: ozz сам добирает разницу
/// до единицы rest-позой через threshold, и «нормализация» на нашей стороне ломала бы аддитивные
/// сценарии, где сумма весов заведомо не единица.
DECA_API int32_t DecaOzz_BlendPoses(void* destinationHandle, void* const* layerHandles, const float* weights,
									int32_t layerCount) {
	auto* destination = static_cast<Pose*>(destinationHandle);
	if (destination == nullptr || layerHandles == nullptr || weights == nullptr || layerCount <= 0) {
		return 0;
	}

	std::vector<ozz::animation::BlendingJob::Layer> layers(static_cast<size_t>(layerCount));
	for (int32_t i = 0; i < layerCount; ++i) {
		auto* layer = static_cast<Pose*>(layerHandles[i]);
		if (layer == nullptr || layer->owner != destination->owner) {
			// Слой от другого скелета - не «немного не то», а чтение чужой памяти по чужим индексам.
			return 0;
		}

		layers[static_cast<size_t>(i)].transform = ozz::make_span(layer->locals);
		layers[static_cast<size_t>(i)].weight = weights[i];
	}

	ozz::animation::BlendingJob job;
	job.layers = ozz::make_span(layers);
	job.rest_pose = destination->owner->skeleton->joint_rest_poses();
	job.output = ozz::make_span(destination->locals);

	return job.Run() ? 1 : 0;
}

/// Тот же бленд, но с ПОСУСТАВНЫМИ весами слоёв (частичный бленд ozz: верх тела играет свой клип,
/// ноги - базовый) и АДДИТИВНЫМИ слоями (additiveFlags: ненулевой флаг кладёт слой в
/// additive_layers - его трансформы суммируются ПОВЕРХ результата, а не участвуют в усреднении;
/// слой обязан содержать ДЕЛЬТУ, см. AdditiveAnimationBuilder). additiveFlags может быть nullptr -
/// все слои обычные. jointWeights - на слой либо nullptr (вес всюду 1), либо массив по числу костей
/// В ИСХОДНОМ порядке: переупорядочивание в SoA-четвёрки ozz - деталь шима, как и везде.
/// Приёмник МОЖЕТ совпадать с одним из слоёв: BlendingJob пишет выход посуставно после чтения всех
/// слоёв того же сустава, межсуставных зависимостей у него нет.
DECA_API int32_t DecaOzz_BlendPosesLayered(void* destinationHandle, void* const* layerHandles,
										   const float* weights, const float* const* jointWeights,
										   const int32_t* additiveFlags, int32_t layerCount) {
	auto* destination = static_cast<Pose*>(destinationHandle);
	if (destination == nullptr || layerHandles == nullptr || weights == nullptr || layerCount <= 0) {
		return 0;
	}

	const auto& remap = destination->owner->to_source;
	const int32_t jointCount = static_cast<int32_t>(remap.size());
	const int32_t soaCount = (jointCount + 3) / 4;

	std::vector<ozz::animation::BlendingJob::Layer> layers;
	std::vector<ozz::animation::BlendingJob::Layer> additive;
	std::vector<std::vector<ozz::math::SimdFloat4>> masks(static_cast<size_t>(layerCount));

	for (int32_t i = 0; i < layerCount; ++i) {
		auto* layer = static_cast<Pose*>(layerHandles[i]);
		if (layer == nullptr || layer->owner != destination->owner) {
			return 0;
		}

		ozz::animation::BlendingJob::Layer entry;
		entry.transform = ozz::make_span(layer->locals);
		entry.weight = weights[i];

		if (jointWeights != nullptr && jointWeights[i] != nullptr) {
			auto& mask = masks[static_cast<size_t>(i)];
			mask.resize(static_cast<size_t>(soaCount));

			for (int32_t soa = 0; soa < soaCount; ++soa) {
				float lanes[4] = {0.f, 0.f, 0.f, 0.f};
				for (int32_t lane = 0; lane < 4; ++lane) {
					const int32_t joint = soa * 4 + lane;
					if (joint < jointCount) {
						lanes[lane] = jointWeights[i][remap[static_cast<size_t>(joint)]];
					}
				}

				mask[static_cast<size_t>(soa)] =
					ozz::math::simd_float4::Load(lanes[0], lanes[1], lanes[2], lanes[3]);
			}

			entry.joint_weights = ozz::make_span(mask);
		}

		if (additiveFlags != nullptr && additiveFlags[i] != 0) {
			additive.push_back(entry);
		} else {
			layers.push_back(entry);
		}
	}

	ozz::animation::BlendingJob job;
	job.layers = ozz::make_span(layers);
	job.additive_layers = ozz::make_span(additive);
	job.rest_pose = destination->owner->skeleton->joint_rest_poses();
	job.output = ozz::make_span(destination->locals);

	return job.Run() ? 1 : 0;
}

DECA_API int32_t DecaOzz_BlendPosesMasked(void* destinationHandle, void* const* layerHandles,
										  const float* weights, const float* const* jointWeights,
										  int32_t layerCount) {
	auto* destination = static_cast<Pose*>(destinationHandle);
	if (destination == nullptr || layerHandles == nullptr || weights == nullptr || layerCount <= 0) {
		return 0;
	}

	const auto& remap = destination->owner->to_source;
	const int32_t jointCount = static_cast<int32_t>(remap.size());
	const int32_t soaCount = (jointCount + 3) / 4;

	std::vector<ozz::animation::BlendingJob::Layer> layers(static_cast<size_t>(layerCount));
	std::vector<std::vector<ozz::math::SimdFloat4>> masks(static_cast<size_t>(layerCount));

	for (int32_t i = 0; i < layerCount; ++i) {
		auto* layer = static_cast<Pose*>(layerHandles[i]);
		if (layer == nullptr || layer->owner != destination->owner) {
			return 0;
		}

		layers[static_cast<size_t>(i)].transform = ozz::make_span(layer->locals);
		layers[static_cast<size_t>(i)].weight = weights[i];

		if (jointWeights != nullptr && jointWeights[i] != nullptr) {
			auto& mask = masks[static_cast<size_t>(i)];
			mask.resize(static_cast<size_t>(soaCount));

			for (int32_t soa = 0; soa < soaCount; ++soa) {
				float lanes[4] = {0.f, 0.f, 0.f, 0.f};
				for (int32_t lane = 0; lane < 4; ++lane) {
					const int32_t joint = soa * 4 + lane;
					if (joint < jointCount) {
						lanes[lane] = jointWeights[i][remap[static_cast<size_t>(joint)]];
					}
				}

				mask[static_cast<size_t>(soa)] =
					ozz::math::simd_float4::Load(lanes[0], lanes[1], lanes[2], lanes[3]);
			}

			layers[static_cast<size_t>(i)].joint_weights = ozz::make_span(mask);
		}
	}

	ozz::animation::BlendingJob job;
	job.layers = ozz::make_span(layers);
	job.rest_pose = destination->owner->skeleton->joint_rest_poses();
	job.output = ozz::make_span(destination->locals);

	return job.Run() ? 1 : 0;
}

/// Локальные TRS -> модельные матрицы.
DECA_API int32_t DecaOzz_LocalToModel(void* poseHandle) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr) {
		return 0;
	}

	ozz::animation::LocalToModelJob job;
	job.skeleton = pose->owner->skeleton.get();
	job.input = ozz::make_span(pose->locals);
	job.output = ozz::make_span(pose->models);

	return job.Run() ? 1 : 0;
}

/// Выгружает модельные матрицы В ИСХОДНОМ порядке джойнтов - вызывающий про ozz-порядок не знает.
/// Копирование побайтовое: раскладка Float4x4 и System.Numerics.Matrix4x4 совпадает (см. шапку).
DECA_API int32_t DecaOzz_ReadModelMatrices(void* poseHandle, float* out, int32_t jointCapacity) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr || out == nullptr) {
		return 0;
	}

	const auto& remap = pose->owner->to_source;
	const int32_t count = static_cast<int32_t>(remap.size());
	if (jointCapacity < count) {
		return 0;
	}

	for (int32_t i = 0; i < count; ++i) {
		std::memcpy(out + remap[static_cast<size_t>(i)] * 16, &pose->models[static_cast<size_t>(i)],
					sizeof(ozz::math::Float4x4));
	}

	return count;
}

/// Локальные TRS в AoS-виде и в исходном порядке - вход процедурного слоя (spring bones, ручная
/// правка костей). Распаковка из SoA здесь, а не на стороне C#: раскладка SoA - внутреннее дело ozz.
DECA_API int32_t DecaOzz_ReadLocalTransforms(void* poseHandle, DecaOzzTransform* out, int32_t jointCapacity) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr || out == nullptr) {
		return 0;
	}

	const auto& remap = pose->owner->to_source;
	const int32_t count = static_cast<int32_t>(remap.size());
	if (jointCapacity < count) {
		return 0;
	}

	for (int32_t i = 0; i < count; ++i) {
		const ozz::math::SoaTransform& soa = pose->locals[static_cast<size_t>(i / 4)];
		const int lane = i % 4;

		float translation[4][4], rotation[4][4], scale[4][4];
		ozz::math::StorePtrU(soa.translation.x, translation[0]);
		ozz::math::StorePtrU(soa.translation.y, translation[1]);
		ozz::math::StorePtrU(soa.translation.z, translation[2]);
		ozz::math::StorePtrU(soa.rotation.x, rotation[0]);
		ozz::math::StorePtrU(soa.rotation.y, rotation[1]);
		ozz::math::StorePtrU(soa.rotation.z, rotation[2]);
		ozz::math::StorePtrU(soa.rotation.w, rotation[3]);
		ozz::math::StorePtrU(soa.scale.x, scale[0]);
		ozz::math::StorePtrU(soa.scale.y, scale[1]);
		ozz::math::StorePtrU(soa.scale.z, scale[2]);

		DecaOzzTransform& destination = out[remap[static_cast<size_t>(i)]];
		destination.translation[0] = translation[0][lane];
		destination.translation[1] = translation[1][lane];
		destination.translation[2] = translation[2][lane];
		destination.rotation[0] = rotation[0][lane];
		destination.rotation[1] = rotation[1][lane];
		destination.rotation[2] = rotation[2][lane];
		destination.rotation[3] = rotation[3][lane];
		destination.scale[0] = scale[0][lane];
		destination.scale[1] = scale[1][lane];
		destination.scale[2] = scale[2][lane];
	}

	return count;
}

/// Обратная операция: правленые процедурным слоем локальные TRS обратно в SoA-позу.
DECA_API int32_t DecaOzz_WriteLocalTransforms(void* poseHandle, const DecaOzzTransform* in, int32_t jointCount) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr || in == nullptr) {
		return 0;
	}

	const auto& remap = pose->owner->to_source;
	const int32_t count = static_cast<int32_t>(remap.size());
	if (jointCount < count) {
		return 0;
	}

	// Хвостовые дорожки последнего SoA-блока (число костей редко кратно четырём) заполняются
	// единичной трансформацией: мусор в них не влияет на результат по костям, но легко даёт NaN, а
	// NaN в SIMD-регистре портит и три соседние ЖИВЫЕ кости вместе с собой.
	const size_t soaCount = pose->locals.size();
	for (size_t block = 0; block < soaCount; ++block) {
		float translation[3][4] = {}, rotation[4][4] = {}, scale[3][4] = {};
		for (int lane = 0; lane < 4; ++lane) {
			const int32_t joint = static_cast<int32_t>(block) * 4 + lane;
			if (joint >= count) {
				rotation[3][lane] = 1.0f;
				scale[0][lane] = scale[1][lane] = scale[2][lane] = 1.0f;
				continue;
			}

			const DecaOzzTransform& source = in[remap[static_cast<size_t>(joint)]];
			translation[0][lane] = source.translation[0];
			translation[1][lane] = source.translation[1];
			translation[2][lane] = source.translation[2];
			rotation[0][lane] = source.rotation[0];
			rotation[1][lane] = source.rotation[1];
			rotation[2][lane] = source.rotation[2];
			rotation[3][lane] = source.rotation[3];
			scale[0][lane] = source.scale[0];
			scale[1][lane] = source.scale[1];
			scale[2][lane] = source.scale[2];
		}

		ozz::math::SoaTransform& destination = pose->locals[block];
		destination.translation.x = ozz::math::simd_float4::LoadPtrU(translation[0]);
		destination.translation.y = ozz::math::simd_float4::LoadPtrU(translation[1]);
		destination.translation.z = ozz::math::simd_float4::LoadPtrU(translation[2]);
		destination.rotation.x = ozz::math::simd_float4::LoadPtrU(rotation[0]);
		destination.rotation.y = ozz::math::simd_float4::LoadPtrU(rotation[1]);
		destination.rotation.z = ozz::math::simd_float4::LoadPtrU(rotation[2]);
		destination.rotation.w = ozz::math::simd_float4::LoadPtrU(rotation[3]);
		destination.scale.x = ozz::math::simd_float4::LoadPtrU(scale[0]);
		destination.scale.y = ozz::math::simd_float4::LoadPtrU(scale[1]);
		destination.scale.z = ozz::math::simd_float4::LoadPtrU(scale[2]);
	}

	return count;
}

// --- IK -----------------------------------------------------------------------------------------

/// Two-bone IK (нога, рука): доворачивает start и mid так, чтобы end попал в target. Индексы
/// джойнтов - ИСХОДНЫЕ, шим переводит их в ozz-порядок сам. Результат применяется к локальной позе,
/// поэтому вызывающий обязан после этого заново позвать DecaOzz_LocalToModel.
///
/// Требует АКТУАЛЬНЫХ модельных матриц: job читает мировые положения костей. Порядок вызова -
/// Sample -> LocalToModel -> TwoBoneIk -> LocalToModel.
DECA_API int32_t DecaOzz_TwoBoneIk(void* poseHandle, int32_t startJoint, int32_t midJoint, int32_t endJoint,
								   const float* target, const float* poleVector, const float* midAxis,
								   float weight, float soften, float twistAngle) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr || target == nullptr || poleVector == nullptr || midAxis == nullptr) {
		return 0;
	}

	const auto& from = pose->owner->from_source;
	if (startJoint < 0 || midJoint < 0 || endJoint < 0 || startJoint >= static_cast<int32_t>(from.size()) ||
		midJoint >= static_cast<int32_t>(from.size()) || endJoint >= static_cast<int32_t>(from.size())) {
		return 0;
	}

	const int32_t start = from[static_cast<size_t>(startJoint)];
	const int32_t mid = from[static_cast<size_t>(midJoint)];
	const int32_t end = from[static_cast<size_t>(endJoint)];
	if (start < 0 || mid < 0 || end < 0) {
		return 0;
	}

	ozz::math::SimdQuaternion startCorrection, midCorrection;

	ozz::animation::IKTwoBoneJob job;
	job.target = ozz::math::simd_float4::Load3PtrU(target);
	job.pole_vector = ozz::math::simd_float4::Load3PtrU(poleVector);
	job.mid_axis = ozz::math::simd_float4::Load3PtrU(midAxis);
	job.weight = weight;
	job.soften = soften;
	job.twist_angle = twistAngle;
	job.start_joint = &pose->models[static_cast<size_t>(start)];
	job.mid_joint = &pose->models[static_cast<size_t>(mid)];
	job.end_joint = &pose->models[static_cast<size_t>(end)];
	job.start_joint_correction = &startCorrection;
	job.mid_joint_correction = &midCorrection;

	if (!job.Run()) {
		return 0;
	}

	// Коррекции - ДОвороты в модельном пространстве, применяются к локальным поворотам костей.
	// Именно локальная поза, а не модельные матрицы, есть источник истины для дальнейших стадий
	// (блендинг, spring bones), поэтому правится она.
	MultiplySoaRotation(pose->locals[static_cast<size_t>(start / 4)], start & 3, startCorrection);
	MultiplySoaRotation(pose->locals[static_cast<size_t>(mid / 4)], mid & 3, midCorrection);
	return 1;
}

/// Aim IK: доворачивает одну кость (голова, торс, ствол оружия) так, чтобы её forward смотрел в цель.
DECA_API int32_t DecaOzz_AimIk(void* poseHandle, int32_t joint, const float* target, const float* forward,
							   const float* up, const float* poleVector, float weight) {
	auto* pose = static_cast<Pose*>(poseHandle);
	if (pose == nullptr || target == nullptr || forward == nullptr || up == nullptr || poleVector == nullptr) {
		return 0;
	}

	const auto& from = pose->owner->from_source;
	if (joint < 0 || joint >= static_cast<int32_t>(from.size())) {
		return 0;
	}

	const int32_t target_joint = from[static_cast<size_t>(joint)];
	if (target_joint < 0) {
		return 0;
	}

	ozz::math::SimdQuaternion correction;

	ozz::animation::IKAimJob job;
	job.target = ozz::math::simd_float4::Load3PtrU(target);
	job.forward = ozz::math::simd_float4::Load3PtrU(forward);
	job.up = ozz::math::simd_float4::Load3PtrU(up);
	job.pole_vector = ozz::math::simd_float4::Load3PtrU(poleVector);
	job.weight = weight;
	job.joint = &pose->models[static_cast<size_t>(target_joint)];
	job.joint_correction = &correction;

	if (!job.Run()) {
		return 0;
	}

	MultiplySoaRotation(pose->locals[static_cast<size_t>(target_joint / 4)], target_joint & 3, correction);
	return 1;
}
