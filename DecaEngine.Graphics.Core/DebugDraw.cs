using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Graphics;

/// <summary>Вершина дебаг-линии. Позиция МИРОВАЯ - дебаг-примитивы приходят из совершенно разных
/// систем (поза скелета в пространстве модели, тела физики в мире), и общий знаменатель у них ровно
/// один: мир. Приводит к нему тот, кто рисует, а не рисователь.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DebugLineVertex
{
	public Vector3 Position;

	/// <summary>Цвет линии. АЛЬФА - НЕ прозрачность: блендинга в дебаг-PSO нет. Ноль в альфе означает
	/// «вершины нет» - шейдер выкидывает её за пределы клип-пространства. Так гасится хвост буфера,
	/// оставшийся от кадра, в котором линий было больше (см. DebugLineOverlay).</summary>
	public Vector4 Color;
}

/// <summary>Экранная подпись к точке мира (имя кости, номер тела). Собирается вместе с линиями, но
/// РИСУЕТСЯ иначе - текст живёт в экранном пространстве, и рисует его тот, у кого есть шрифт и
/// проекция кадра (в редакторе - ImGui поверх картинки вьюпорта, см. PrefabSceneViewport). Здесь
/// подписи только НАКАПЛИВАЮТСЯ, чтобы у систем была одна точка сдачи дебага, а не две.</summary>
public struct DebugLabel
{
	public Vector3 Position;
	public string Text;
	public Vector4 Color;
}

/// <summary>Готовая палитра дебага. Отдельные имена, а не литералы на месте вызова: цвет здесь -
/// это КОДИРОВКА (спящее тело серое, кинематическое голубое), и один и тот же смысл обязан выглядеть
/// одинаково во всех системах, иначе легенда в окне дебага перестаёт быть правдой.</summary>
public static class DebugColor
{
	public static readonly Vector4 White = new(1f, 1f, 1f, 1f);
	public static readonly Vector4 Black = new(0f, 0f, 0f, 1f);
	public static readonly Vector4 Grey = new(0.5f, 0.5f, 0.5f, 1f);
	public static readonly Vector4 Red = new(1f, 0.15f, 0.15f, 1f);
	public static readonly Vector4 Green = new(0.2f, 1f, 0.25f, 1f);
	public static readonly Vector4 Blue = new(0.3f, 0.5f, 1f, 1f);
	public static readonly Vector4 Yellow = new(1f, 0.9f, 0.2f, 1f);
	public static readonly Vector4 Orange = new(1f, 0.55f, 0.1f, 1f);
	public static readonly Vector4 Cyan = new(0.2f, 0.95f, 1f, 1f);
	public static readonly Vector4 Magenta = new(1f, 0.3f, 0.9f, 1f);

	/// <summary>Тот же цвет, но приглушённый - для «второстепенной» половины пары (цель против
	/// текущего положения, спящее тело против бодрствующего).</summary>
	public static Vector4 Dim(Vector4 color, float factor = 0.45f) =>
		new(color.X * factor, color.Y * factor, color.Z * factor, color.W);
}

/// <summary>
/// Приёмник дебаг-геометрии на кадр: immediate-mode список цветных линий, который системы наполняют
/// в произвольном порядке, а рисователь один раз за кадр заливает в GPU (см. DebugLineOverlay в
/// редакторе).
///
/// ЛИНИИ, а не заполненные фигуры, и это осознанно: каркас читается насквозь, не прячет ни сцену,
/// ни сам себя, не требует ни сортировки, ни блендинга, и любой примитив - от скелета до капсулы
/// коллайдера - выражается через него без отдельного PSO.
///
/// Два независимых буфера: <see cref="Line"/> с депт-тестом (примитив честно прячется за геометрией
/// сцены - так видно, что кость ВНУТРИ меша) и «поверх всего» (кость видно и сквозь персонажа - без
/// этого скелет невозможно разглядеть вообще, потому что он весь внутри модели). Оба нужны
/// одновременно, поэтому это не флаг режима, а два списка.
///
/// Класс НЕ потокобезопасен: наполняется из кадрового шага, который в редакторе однопоточен.
/// Единственное исключение - контакты физики, которые собираются в узкой фазе на воркерах; они
/// накапливаются на своей стороне и переливаются сюда уже одним потоком (см. PhysicsContactRecorder).
/// </summary>
public sealed class DebugDraw
{
	private readonly List<DebugLineVertex> _depthTested = new();
	private readonly List<DebugLineVertex> _onTop = new();
	private readonly List<DebugLabel> _labels = new();

	/// <summary>Общий тумблер. Выключенный рисователь не только не рисует, но и НЕ КОПИТ: вызовы
	/// примитивов выходят сразу, поэтому системы могут звать их безусловно, не обвешивая каждый
	/// вызов проверкой настройки.</summary>
	public bool Enabled { get; set; }

	/// <summary>Потолок числа вершин за кадр. Дебаг рисуется по данным, размер которых вызывающий не
	/// контролирует (скелет на двести костей, сцена на тысячи тел), и без потолка одна забытая
	/// галочка означает не «неудобный вид», а гигабайт на заливку и повисший кадр. Лишнее молча
	/// отбрасывается, а <see cref="Overflowed"/> позволяет честно сказать об этом в окне дебага.</summary>
	public int MaxVertices { get; set; } = 1 << 18;

	/// <summary>Уперлись ли в <see cref="MaxVertices"/> в этом кадре.</summary>
	public bool Overflowed { get; private set; }

	public int DepthTestedCount => _depthTested.Count;
	public int OnTopCount => _onTop.Count;
	public int TotalCount => _depthTested.Count + _onTop.Count;

	public ReadOnlySpan<DebugLineVertex> DepthTestedVertices() => CollectionsMarshal.AsSpan(_depthTested);

	public ReadOnlySpan<DebugLineVertex> OnTopVertices() => CollectionsMarshal.AsSpan(_onTop);

	/// <summary>Подписи кадра (см. <see cref="DebugLabel"/>). Список, а не span: потребитель их
	/// проецирует и сортирует, а не заливает в буфер.</summary>
	public IReadOnlyList<DebugLabel> Labels => _labels;

	/// <summary>Потолок числа подписей за кадр. Отдельный от <see cref="MaxVertices"/> и НАМНОГО
	/// меньше: текст рисуется по одному вызову ImGui на подпись, и «имена всех костей всех
	/// персонажей сцены» кладут редактор задолго до того, как линии упрутся в свой потолок.</summary>
	public int MaxLabels { get; set; } = 512;

	/// <summary>Сбрасывает кадр. Звать РОВНО ОДИН РАЗ за кадр и до наполнения: список - это кадр
	/// целиком, а не накопитель.</summary>
	public void Clear()
	{
		_depthTested.Clear();
		_onTop.Clear();
		_labels.Clear();
		Overflowed = false;
	}

	/// <summary>Подпись к точке мира. Пустой текст не добавляется: у кости без имени подпись
	/// выглядела бы как артефакт отрисовки, а не как отсутствие имени.</summary>
	public void Label(Vector3 position, string text, Vector4 color)
	{
		if (!Enabled || string.IsNullOrEmpty(text) || !IsFinite(position))
		{
			return;
		}

		if (_labels.Count >= MaxLabels)
		{
			Overflowed = true;
			return;
		}

		_labels.Add(new DebugLabel { Position = position, Text = text, Color = color });
	}

	// --- Базовый примитив --------------------------------------------------------------------------

	public void Line(Vector3 a, Vector3 b, Vector4 color, bool onTop = false)
	{
		if (!Enabled)
		{
			return;
		}

		// NaN приезжает сюда реально: разлетевшийся рэгдолл, вырожденная матрица кости, деление на
		// нулевую длину в цепочке. Одна такая вершина роняет ВЕСЬ дроу линий (примитив с NaN
		// отбраковывается вместе с соседями по-разному на разных драйверах), и вид дебага пропадает
		// целиком - ровно в тот момент, когда он нужнее всего.
		if (!IsFinite(a) || !IsFinite(b))
		{
			return;
		}

		var list = onTop ? _onTop : _depthTested;

		if (_depthTested.Count + _onTop.Count + 2 > MaxVertices)
		{
			Overflowed = true;
			return;
		}

		list.Add(new DebugLineVertex { Position = a, Color = color });
		list.Add(new DebugLineVertex { Position = b, Color = color });
	}

	/// <summary>Ломаная по точкам - замкнутая по требованию. Отдельным методом, потому что кольца
	/// (сфера, капсула, окружность) строятся именно так, и раскрывать их в пары вручную на каждом
	/// вызове значило бы повторить один и тот же цикл в пяти местах.</summary>
	public void Polyline(ReadOnlySpan<Vector3> points, Vector4 color, bool closed = false, bool onTop = false)
	{
		for (int i = 1; i < points.Length; i++)
		{
			Line(points[i - 1], points[i], color, onTop);
		}

		if (closed && points.Length > 2)
		{
			Line(points[^1], points[0], color, onTop);
		}
	}

	// --- Точки и направления -----------------------------------------------------------------------

	/// <summary>Крест по трём осям - «здесь точка». Размер задаётся ВЫЗЫВАЮЩИМ и не имеет разумного
	/// значения по умолчанию: масштаб моделей в движке произволен (у лисы габарит ~160 единиц), и
	/// константа в единицах мира была бы либо невидимой, либо во весь экран.</summary>
	public void Cross(Vector3 point, float size, Vector4 color, bool onTop = false)
	{
		Line(point - Vector3.UnitX * size, point + Vector3.UnitX * size, color, onTop);
		Line(point - Vector3.UnitY * size, point + Vector3.UnitY * size, color, onTop);
		Line(point - Vector3.UnitZ * size, point + Vector3.UnitZ * size, color, onTop);
	}

	public void Ray(Vector3 origin, Vector3 direction, float length, Vector4 color, bool onTop = false) =>
		Line(origin, origin + direction * length, color, onTop);

	/// <summary>Стрелка: отрезок плюс четыре зубца на конце. Зубцы - доля ДЛИНЫ самой стрелки, а не
	/// абсолютный размер: стрелка скорости бывает и в сантиметр, и в десятки единиц.</summary>
	public void Arrow(Vector3 from, Vector3 to, Vector4 color, bool onTop = false)
	{
		Line(from, to, color, onTop);

		var delta = to - from;
		float length = delta.Length();
		if (length < 1e-6f)
		{
			return;
		}

		var direction = delta / length;
		Basis(direction, out var u, out var v);

		float head = length * 0.18f;
		var baseCenter = to - direction * head;

		Line(to, baseCenter + u * head * 0.5f, color, onTop);
		Line(to, baseCenter - u * head * 0.5f, color, onTop);
		Line(to, baseCenter + v * head * 0.5f, color, onTop);
		Line(to, baseCenter - v * head * 0.5f, color, onTop);
	}

	/// <summary>Тройка осей трансформа: X красная, Y зелёная, Z синяя - конвенция, одинаковая во всех
	/// редакторах, и менять её нельзя, даже если в движке своя система координат.</summary>
	public void Axes(in Matrix4x4 transform, float scale, bool onTop = false)
	{
		var origin = transform.Translation;

		Line(origin, origin + new Vector3(transform.M11, transform.M12, transform.M13) * scale, DebugColor.Red, onTop);
		Line(origin, origin + new Vector3(transform.M21, transform.M22, transform.M23) * scale, DebugColor.Green, onTop);
		Line(origin, origin + new Vector3(transform.M31, transform.M32, transform.M33) * scale, DebugColor.Blue, onTop);
	}

	// --- Каркасы форм ------------------------------------------------------------------------------

	public void Circle(Vector3 center, Vector3 axisU, Vector3 axisV, float radius, Vector4 color,
		int segments = 24, bool onTop = false)
	{
		if (segments < 3)
		{
			segments = 3;
		}

		var previous = center + axisU * radius;

		for (int i = 1; i <= segments; i++)
		{
			float angle = i / (float)segments * MathF.Tau;
			var next = center + (axisU * MathF.Cos(angle) + axisV * MathF.Sin(angle)) * radius;

			Line(previous, next, color, onTop);
			previous = next;
		}
	}

	/// <summary>Сфера тремя ортогональными кольцами. Не «икосферой»: три кольца читаются как объём
	/// не хуже, а стоят три десятка линий вместо трёх сотен - у сцены с сотней тел это разница между
	/// дебагом и слайд-шоу.</summary>
	public void WireSphere(Vector3 center, float radius, Vector4 color, int segments = 24, bool onTop = false)
	{
		Circle(center, Vector3.UnitX, Vector3.UnitY, radius, color, segments, onTop);
		Circle(center, Vector3.UnitY, Vector3.UnitZ, radius, color, segments, onTop);
		Circle(center, Vector3.UnitZ, Vector3.UnitX, radius, color, segments, onTop);
	}

	public void WireBox(Vector3 center, Quaternion orientation, Vector3 halfExtents, Vector4 color,
		bool onTop = false)
	{
		Span<Vector3> corners = stackalloc Vector3[8];

		for (int i = 0; i < 8; i++)
		{
			var local = new Vector3(
				(i & 1) == 0 ? -halfExtents.X : halfExtents.X,
				(i & 2) == 0 ? -halfExtents.Y : halfExtents.Y,
				(i & 4) == 0 ? -halfExtents.Z : halfExtents.Z);

			corners[i] = center + Vector3.Transform(local, orientation);
		}

		// Рёбра по битам индекса: две вершины соединены, если их номера различаются РОВНО одним битом.
		for (int i = 0; i < 8; i++)
		{
			for (int bit = 1; bit <= 4; bit <<= 1)
			{
				int j = i | bit;
				if (j != i)
				{
					Line(corners[i], corners[j], color, onTop);
				}
			}
		}
	}

	public void WireBox(Vector3 min, Vector3 max, Vector4 color, bool onTop = false) =>
		WireBox((min + max) * 0.5f, Quaternion.Identity, (max - min) * 0.5f, color, onTop);

	/// <summary>
	/// Капсула в конвенции Bepu: ось - ЛОКАЛЬНАЯ Y, <paramref name="length"/> - длина ЦИЛИНДРИЧЕСКОЙ
	/// части без полусфер. Именно так её измеряет <c>BepuPhysics.Collidables.Capsule</c>, и рисовать
	/// её здесь «полной длиной» значило бы показывать каркас длиннее реального коллайдера - самый
	/// вредный вид дебага, который врёт правдоподобно.
	/// </summary>
	public void WireCapsule(Vector3 center, Quaternion orientation, float radius, float length,
		Vector4 color, int segments = 16, bool onTop = false)
	{
		float half = length * 0.5f;

		var axis = Vector3.Transform(Vector3.UnitY, orientation);
		var u = Vector3.Transform(Vector3.UnitX, orientation);
		var v = Vector3.Transform(Vector3.UnitZ, orientation);

		var top = center + axis * half;
		var bottom = center - axis * half;

		Circle(top, u, v, radius, color, segments, onTop);
		Circle(bottom, u, v, radius, color, segments, onTop);

		// Четыре образующих цилиндра.
		Line(top + u * radius, bottom + u * radius, color, onTop);
		Line(top - u * radius, bottom - u * radius, color, onTop);
		Line(top + v * radius, bottom + v * radius, color, onTop);
		Line(top - v * radius, bottom - v * radius, color, onTop);

		// Полусферы - полукольцами в двух плоскостях: без них капсула неотличима от цилиндра, а
		// разница в полрадиуса на каждом конце и есть то, из-за чего конечности рэгдолла не сходятся.
		HalfCircle(top, u, axis, radius, color, segments, onTop);
		HalfCircle(top, v, axis, radius, color, segments, onTop);
		HalfCircle(bottom, u, -axis, radius, color, segments, onTop);
		HalfCircle(bottom, v, -axis, radius, color, segments, onTop);
	}

	public void WireCylinder(Vector3 center, Quaternion orientation, float radius, float length,
		Vector4 color, int segments = 16, bool onTop = false)
	{
		float half = length * 0.5f;

		var axis = Vector3.Transform(Vector3.UnitY, orientation);
		var u = Vector3.Transform(Vector3.UnitX, orientation);
		var v = Vector3.Transform(Vector3.UnitZ, orientation);

		var top = center + axis * half;
		var bottom = center - axis * half;

		Circle(top, u, v, radius, color, segments, onTop);
		Circle(bottom, u, v, radius, color, segments, onTop);

		Line(top + u * radius, bottom + u * radius, color, onTop);
		Line(top - u * radius, bottom - u * radius, color, onTop);
		Line(top + v * radius, bottom + v * radius, color, onTop);
		Line(top - v * radius, bottom - v * radius, color, onTop);
	}

	/// <summary>
	/// Кость октаэдром - тот самый вид, по которому скелет узнаётся в любом DCC-пакете. Не отрезком:
	/// отрезок не показывает ни толщину, ни то, куда кость ПОВЁРНУТА вокруг своей оси, а именно
	/// перекрут вокруг оси - самая частая ошибка в риге и в переводе поз между конвенциями.
	/// </summary>
	public void Bone(Vector3 from, Vector3 to, Vector4 color, float widthFactor = 0.12f, bool onTop = false)
	{
		var delta = to - from;
		float length = delta.Length();

		if (length < 1e-6f)
		{
			return;
		}

		var direction = delta / length;
		Basis(direction, out var u, out var v);

		float width = length * widthFactor;
		var ringCenter = from + direction * (length * 0.2f);

		Span<Vector3> ring =
		[
			ringCenter + u * width,
			ringCenter + v * width,
			ringCenter - u * width,
			ringCenter - v * width,
		];

		for (int i = 0; i < 4; i++)
		{
			Line(from, ring[i], color, onTop);
			Line(ring[i], to, color, onTop);
			Line(ring[i], ring[(i + 1) & 3], color, onTop);
		}
	}

	// --- Служебное ---------------------------------------------------------------------------------

	private void HalfCircle(Vector3 center, Vector3 axisU, Vector3 axisV, float radius, Vector4 color,
		int segments, bool onTop)
	{
		if (segments < 2)
		{
			segments = 2;
		}

		var previous = center + axisU * radius;

		for (int i = 1; i <= segments; i++)
		{
			float angle = i / (float)segments * MathF.PI;
			var next = center + (axisU * MathF.Cos(angle) + axisV * MathF.Sin(angle)) * radius;

			Line(previous, next, color, onTop);
			previous = next;
		}
	}

	/// <summary>Любые две оси, ортогональные заданной. Опорный вектор выбирается по НАИМЕНЬШЕЙ
	/// компоненте направления: взять фиксированный (скажем, всегда Y) значит получить вырожденное
	/// векторное произведение ровно тогда, когда кость смотрит вертикально, - а вертикальных костей
	/// в любом персонаже большинство.</summary>
	private static void Basis(Vector3 direction, out Vector3 u, out Vector3 v)
	{
		var absolute = Vector3.Abs(direction);

		var reference = absolute.X <= absolute.Y && absolute.X <= absolute.Z ? Vector3.UnitX
			: absolute.Y <= absolute.Z ? Vector3.UnitY
			: Vector3.UnitZ;

		u = Vector3.Normalize(Vector3.Cross(direction, reference));
		v = Vector3.Cross(direction, u);
	}

	private static bool IsFinite(Vector3 value) =>
		float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
