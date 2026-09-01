using System.Numerics;
using System.Runtime.InteropServices;

namespace DecaEngine.Graphics;

[StructLayout(LayoutKind.Explicit, Size = 48)]
public struct DrawData
{
	[FieldOffset(0)]
	public Vector4 positionScale;
	[FieldOffset(16)]
	public Vector4 orientation;

	/// <summary>xyz - ПОКОМПОНЕНТНЫЙ масштаб инстанса. Кулинг-сфера в BatchingInstancingCS обязана
	/// масштабировать центр баундов меша именно им: прежний максимум (positionScale.w) на
	/// неравномерном масштабе уносил центр на |bounds.center|*(max-фактический) от геометрии, и
	/// повёрнутый инстанс со смещённым пивотом вылетал из фрустума теневого слайса punctual-света -
	/// "тень не учитывает поворот". Максимум остаётся в positionScale.w для радиуса и LOD.</summary>
	[FieldOffset(32)]
	public Vector4 scale3;
};