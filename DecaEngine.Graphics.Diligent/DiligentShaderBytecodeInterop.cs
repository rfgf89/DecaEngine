using System;
using System.Runtime.InteropServices;
using Diligent;
using SharpGen.Runtime;

namespace DecaEngine.Graphics.Diligent;

// Creates a shader from precompiled bytecode, bypassing the managed binding: in
// DiligentGraphics.DiligentEngine.Core 2.5.6 the generated ShaderCreateInfo.__MarshalTo copies the
// ByteCode array but never fills ByteCodeSize, so native Diligent rejects the call. The native
// struct is therefore built by hand here and dispatched through vtable slot [5]
// (IRenderDevice::CreateShader). A size mismatch disables the path instead of corrupting memory.
internal static class DiligentShaderBytecodeInterop
{
	// Mirror of Diligent.ShaderCreateInfo.__Native (x64). Version fields widened to ulong: same
	// width and offsets, and bytecode needs no language version anyway.
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

	// False when the binding's layout no longer matches NativeCreateInfo. Computed once.
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

	// null means the caller must fall back to compiling from source.
	public static unsafe IShader? CreateShader(IRenderDevice device, string name, ShaderType type,
		string entryPoint, byte[] bytecode)
	{
		if (!LayoutValidated || bytecode.Length == 0)
		{
			return null;
		}

		var namePtr = Marshal.StringToHGlobalAnsi(name);
		var entryPtr = Marshal.StringToHGlobalAnsi(entryPoint);
		// Must match the source-compile path, which uses UseCombinedTextureSamplers=true.
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
