using System.Numerics;

namespace DecaEngine.Core;

/// <summary>
/// Позиция, поворот, масштаб - общий словарь движка.
///
/// Лежал внутри ModelLoader.cs, посреди четырёх тысяч строк разбора glTF. Из-за этого весь рантайм
/// анимации - поза скелета, ozz, IK стоп, пружинные кости, гуманоидный аватар - формально зависел
/// от загрузчика моделей, хотя не использовал из него ничего, кроме этих трёх полей.
///
/// Матрица из него собирается через <see cref="MathUtils.CreateTrs"/>.
/// </summary>
public struct Transform
{
	public Vector3 position;
	public Quaternion rotation;
	public Vector3 scale;
}
