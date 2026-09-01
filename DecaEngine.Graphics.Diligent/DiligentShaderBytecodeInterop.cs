using System;
using System.Runtime.InteropServices;
using Diligent;
using SharpGen.Runtime;

namespace DecaEngine.Graphics.Diligent;

/// <summary>
/// Создание шейдера из ГОТОВОГО байткода в обход управляемого биндинга: у
/// DiligentGraphics.DiligentEngine.Core 2.5.6 (включая 2.5.6-api10) сгенерированный
/// ShaderCreateInfo.__MarshalTo копирует массив ByteCode, но НЕ заполняет ByteCodeSize - нативный
/// Diligent видит указатель с нулевой длиной и отвергает создание. Поэтому нативная структура
/// собирается здесь вручную (поле в поле повторяет декомпилированный __Native, размер 152 байта
/// сверяется с внутренним типом биндинга в рантайме - разъехались после апдейта пакета, значит
/// путь байткода молча выключается, а вызывающий компилирует из исходника как раньше) и вызывается
/// тот же слот vtable [5] (IRenderDevice::CreateShader), что использует сам биндинг.
/// </summary>
internal static class DiligentShaderBytecodeInterop
{
	// Зеркало Diligent.ShaderCreateInfo.__Native (x64). Version-поля заменены на ulong: та же
	// ширина и те же смещения, а значения всё равно нулевые (для байткода версии языка не нужны).
	[StructLayout(LayoutKind.Sequential)]
	private unsafe struct NativeCreateInfo
	{
		public IntPtr FilePath;
		public IntPtr ShaderSourceStreamFactory;
		public IntPtr Source;
		public byte* ByteCode;
		public nuint ByteCodeSize;
		public IntPtr EntryPoint;
		public IntPtr MacroElements;
		public uint MacroCount;
		private readonly uint _macrosPad;
		public IntPtr DescName;
		public ShaderType ShaderType;
		public byte UseCombinedTextureSamplers;
		private readonly byte _pad0;
		private readonly ushort _pad1;
		public IntPtr CombinedSamplerSuffix;
		public ShaderSourceLanguage SourceLanguage;
		public ShaderCompiler ShaderCompiler;
		public ulong HLSLVersion;
		public ulong GLSLVersion;
		public ulong GLESSLVersion;
		public ulong MSLVersion;
		public ShaderCompileFlags CompileFlags;
		public byte LoadConstantBufferReflection;
		private readonly byte _pad2;
		private readonly ushort _pad3;
		public IntPtr GLSLExtensions;
		public IntPtr WebGPUEmulatedArrayIndexSuffix;
	}

	/// <summary>null - лейаут биндинга не совпал с ожидаемым (см. класс-док). Считается один раз.</summary>
	private static readonly bool LayoutValidated = ValidateLayout();

	private static bool ValidateLayout()
	{
		try
		{
			var native = typeof(ShaderCreateInfo).GetNestedType("__Native",
				System.Reflection.BindingFlags.NonPublic);
			return native != null && Marshal.SizeOf(native) == Marshal.SizeOf<NativeCreateInfo>();
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Создаёт шейдер из байткода. null - лейаут не совпал или нативный Diligent вернул
	/// null (битый/несовместимый байткод) - вызывающий обязан откатиться на компиляцию исходника.</summary>
	public static unsafe IShader? CreateShader(IRenderDevice device, string name, ShaderType type,
		string entryPoint, byte[] bytecode)
	{
		if (!LayoutValidated || bytecode.Length == 0)
		{
			return null;
		}

		var namePtr = Marshal.StringToHGlobalAnsi(name);
		var entryPtr = Marshal.StringToHGlobalAnsi(entryPoint);
		// Тот же суффикс, что ставит ShaderDesc() биндинга - путь компиляции из исходника идёт с
		// UseCombinedTextureSamplers=true (см. DiligentShader.CompileCore), рефлексия байткода
		// обязана совпадать.
		var suffixPtr = Marshal.StringToHGlobalAnsi("_sampler");

		try
		{
			fixed (byte* bytecodePtr = bytecode)
			{
				var ci = new NativeCreateInfo
				{
					ByteCode = bytecodePtr,
					ByteCodeSize = (nuint)bytecode.Length,
					EntryPoint = entryPtr,
					DescName = namePtr,
					ShaderType = type,
					UseCombinedTextureSamplers = 1,
					CombinedSamplerSuffix = suffixPtr,
				};

				var devicePtr = ((CppObject)device).NativePointer;
				var createShader = (delegate* unmanaged[Cdecl]<IntPtr, void*, void*, void*, void>)
					(*(void***)devicePtr)[5];

				var shaderPtr = IntPtr.Zero;
				var blobPtr = IntPtr.Zero;
				createShader(devicePtr, &ci, &shaderPtr, &blobPtr);

				if (blobPtr != IntPtr.Zero)
				{
					new IDataBlob(blobPtr).Dispose();
				}

				return shaderPtr != IntPtr.Zero ? new IShader(shaderPtr) : null;
			}
		}
		finally
		{
			Marshal.FreeHGlobal(namePtr);
			Marshal.FreeHGlobal(entryPtr);
			Marshal.FreeHGlobal(suffixPtr);
		}
	}
}
