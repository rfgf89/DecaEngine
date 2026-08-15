// Главный пасс GTAO - см. GtaoCommon.hlsl. Пары обёрток под одиночный/мультисемпловый депт у
// него больше нет: глубину он читает из префильтрованной цепочки, а MSAA заканчивается на её
// первом звене (GtaoDepthPrefilterMsaaPS.hlsl).
#include "GtaoCommon.hlsl"
