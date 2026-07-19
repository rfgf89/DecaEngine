using System;
using System.Numerics;
using System.Runtime.InteropServices;
using DecaEngine.Core;
using DecaEngine.Graphics.Core;
using DecaEngine.Graphics.Diligent;
using Diligent;
using SharpGLTF.Schema2;
using StbImageSharp;
using Version = Diligent.Version;

namespace DecaEngine
{
	public class DiligentGraphicsPipeline : IGraphicsPipeline
	{
		private IEngineFactory? _engineFactory;
		private IRenderDevice? _renderDevice;
		private IDeviceContext? _immediateContext;
		private IDeviceContext[] _deferredDevices = [];
		private ISwapChain? _swapChain;
		private IFence? _frameFence;
		private ulong _nextFrameValue = 1;

		public IEngineFactory EngineFactory => _engineFactory ?? throw new NullReferenceException();
		public IRenderDevice Device => _renderDevice ?? throw new NullReferenceException();
		public IDeviceContext ImmediateContext => _immediateContext ?? throw new NullReferenceException();
		public IDeviceContext[] DeferredContexts => _deferredDevices ?? [];
		public ISwapChain SwapChain => _swapChain ?? throw new NullReferenceException();

		public event Action<GraphicsPipelineSetupInfo> OnCreateSetupInfo;
		public event Action OnSwapChainInfo;

		public IWindowHandle WindowHandle { get; set; }
		public DiligentPsoManager PsoManager { get; private set; }

		public DiligentGraphicsPipeline(IWindowHandle windowHandle)
		{
			WindowHandle = windowHandle;
		}

		private static void OnMessageCallback(DebugMessageSeverity severity, string message, string function, string file, int line)
		{
			Console.WriteLine($"[{severity}] {message} ({function}): {file}, {line}");
		}

		public void SetBackBufferTarget(Vector4 color)
		{
			var rtv = SwapChain.GetCurrentBackBufferRTV();
			var dsv = SwapChain.GetDepthBufferDSV();

			ImmediateContext.SetRenderTargets(new[] { rtv }, dsv, ResourceStateTransitionMode.Transition);
			ImmediateContext.ClearRenderTarget(rtv, color, ResourceStateTransitionMode.Transition);
			if (dsv != null)
				ImmediateContext.ClearDepthStencil(dsv, ClearDepthStencilFlags.Depth, 1.0f, 0, ResourceStateTransitionMode.Transition);
		}

		public IMeshObject CreateMesh(string name)
		{
			return new DiligentMesh(name, Device);
		}

		public IMaterialObject CreateMaterial(string name)
		{
			return new DiligentMaterial(name, this);
		}

		public IComputeMaterial CreateComputeMaterial(string name)
		{
			return new DiligentComputeMaterial(name, this);
		}

		public IShaderObject CreateShader(string name, string factoryPath, string filePath, ShaderObjectType type)
		{
			return new DiligentShader(this, name, factoryPath, filePath, type);
		}

		private static TextureAddressMode ToDiligent(TextureAddress mode)
		{
			return mode switch
			{
				TextureAddress.Wrap => TextureAddressMode.Wrap,
				TextureAddress.Mirror => TextureAddressMode.Mirror,
				TextureAddress.Clamp => TextureAddressMode.Clamp,
				TextureAddress.Border => TextureAddressMode.Border,
				TextureAddress.MirrorOnce => TextureAddressMode.MirrorOnce,
				_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
			};
		}

		private static FilterType ToDiligent(TextureFilter filter)
		{
			return filter switch
			{
				TextureFilter.Anisotropic => FilterType.Anisotropic,
				_ => FilterType.Linear
			};
		}

		public ISamplerObject CreateSampler(string name,
			TextureFilter filter,
			TextureAddress address,
			CompFunction comparisonFunction,
			Vector4 border)
		{
			var desc = new SamplerDesc
			{
				BorderColor = border,
				ComparisonFunc = (ComparisonFunction)comparisonFunction,
				MinFilter = ToDiligent(filter),
				MagFilter = ToDiligent(filter),
				MipFilter = ToDiligent(filter),
				AddressU = ToDiligent(address),
				AddressV = ToDiligent(address),
				AddressW = ToDiligent(address),
				Name = name
			};

			var sampler = Device.CreateSampler(desc);
			return new DiligentSamplerObject(sampler, desc);
		}

		public unsafe IGpuTexture CreateTexture(CpuTextureData data)
		{
			ImageResult imageResult =
				ImageResult.FromMemory(data.Image.Content.Content.ToArray(), ColorComponents.RedGreenBlueAlpha);

			var desc = new TextureDesc
			{
				Name = data.Name,
				Type = ResourceDimension.Tex2d,
				Width = (uint)imageResult.Width,
				Height = (uint)imageResult.Height,
				Format = TextureFormat.RGBA8_UNorm,
				BindFlags = BindFlags.ShaderResource,
				Usage = Usage.Immutable,
				MipLevels = 1,
			};

			ITexture nativeTexture;
			fixed (byte* pData = imageResult.Data)
			{
				var subResource = new TextureSubResData
				{
					Data = (IntPtr)pData, Stride = (uint)(imageResult.Width * 4)
				};
				var textureData = new TextureData { SubResources = [subResource], Context = ImmediateContext };
				nativeTexture = Device.CreateTexture(desc, textureData);
			}

			// Add a transition to ShaderResource. Diligent's engine doesn't automatically transition
			// the underlying native resource to ShaderResource correctly if you skip Context update 
			// transition barriers in materials or if it's used inside indirect draw setups.
			ImmediateContext.TransitionResourceStates(
			[
				new StateTransitionDesc()
				{
					Resource = nativeTexture,
					OldState = ResourceState.Unknown,
					NewState = ResourceState.ShaderResource,
					Flags = StateTransitionFlags.UpdateState
				}
			]);

			var textureInfo = new DecaEngine.Graphics.Core.TextureInfo
			{
				name = data.Name,
				width = (uint)imageResult.Width,
				height = (uint)imageResult.Height,
				format = TextureObjectFormat.R8G8B8A8UNorm,
				type = TextureType.Texture2D,
			};

			return new DiligentGpuTexture(data.Name, textureInfo, nativeTexture);
		}

		public IRenderTarget CreateRenderTarget(RenderTargetInfo info)
		{
			return new DiligentRenderTarget(Device, info);
		}

		public IBufferHandle CreateBuffer<T>(BufferInfo info)
		{
			info.sizeInBytes = (uint)Marshal.SizeOf<T>();
			var handle = new DiligentBufferHandle(Device);
			handle.Alloc(info);
			return handle;
		}

		public IBufferHandle CreateBuffer<T>(int count, BufferInfo info)
		{
			info.stride = (uint)Marshal.SizeOf<T>();
			info.sizeInBytes = (uint)count * info.stride;
			var handle = new DiligentBufferHandle(Device);
			handle.Alloc(info);
			return handle;
		}

		public IRenderHandle CreateRenderHandle(RenderTargetInfo info)
		{
			var handle = new DiligentRenderHandle(Device);
			handle.Alloc(info);
			return handle;
		}

		public void Initialize(GraphicsBackend backend)
		{
			switch (backend)
			{
				case GraphicsBackend.D3D11:
					SetupD3D11();
					break;
				case GraphicsBackend.D3D12:
					SetupD3D12();
					break;
				case GraphicsBackend.Vulkan:
					SetupVulkan();
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
			}

			PsoManager = new DiligentPsoManager(Device, Path.Combine(Environment.CurrentDirectory, "cache.pso"));
			WindowHandle.OnWindowResize += OnWindowHandleResize;

			void SetupD3D11()
			{
				var engineFactory = Native.GetEngineFactoryD3D11();
				if (engineFactory is null)
				{
					throw new NullReferenceException($"Failed to get {nameof(IEngineFactoryD3D11)}");
				}
				engineFactory.SetMessageCallback(OnMessageCallback);

				var adapter = FindBestAdapter(engineFactory);
				var createInfo = new EngineD3D11CreateInfo
				{
					EnableValidation = true,
					GraphicsAPIVersion = new Version(11, 0),
					AdapterId = (uint)adapter
				};

				var setupInfo = new GraphicsPipelineSetupInfo
				{
					backend = backend
				};
				OnCreateSetupInfo?.Invoke(setupInfo);
				if (setupInfo.contextCount > 0)
				{
					createInfo.NumDeferredContexts = setupInfo.contextCount;
				}

				engineFactory.CreateDeviceAndContextsD3D11(createInfo, out var renderDevice, out var deviceContexts);

				_engineFactory = engineFactory;
				_immediateContext = deviceContexts[0];
				_deferredDevices = deviceContexts.ToArray();
				_renderDevice = renderDevice;

				var wndSize = WindowHandle.Size;
				var swapChainDesc = new SwapChainDesc
				{
					Width = (uint)wndSize.X,
					Height = (uint)wndSize.Y,
					BufferCount = 2,
					Usage = SwapChainUsageFlags.RenderTarget,
					IsPrimary = true,
					ColorBufferFormat = Diligent.TextureFormat.RGBA8_UNorm,
					DepthBufferFormat = Diligent.TextureFormat.D32_Float
				};

				OnSwapChainInfo?.Invoke();

				var swapChain = engineFactory.CreateSwapChainD3D11(renderDevice, deviceContexts[0], swapChainDesc,
					new FullScreenModeDesc(), new Win32NativeWindow { Wnd = WindowHandle.Handle });

				_swapChain = swapChain;
			}

			void SetupD3D12()
			{
				var engineFactory = Native.GetEngineFactoryD3D12();
				if (engineFactory is null)
				{
					throw new NullReferenceException($"Failed to get {nameof(IEngineFactoryD3D12)}");
				}
				engineFactory.SetMessageCallback(OnMessageCallback);
				engineFactory.LoadD3D12("d3d12.dll");

				var adapter = FindBestAdapter(engineFactory);

				var createInfo = new EngineD3D12CreateInfo
				{
					EnableValidation = true,
					AdapterId = (uint)adapter,
				};

				var setupInfo = new GraphicsPipelineSetupInfo
				{
					backend = backend
				};
				OnCreateSetupInfo?.Invoke(setupInfo);
				if (setupInfo.contextCount > 0)
				{
					createInfo.NumDeferredContexts = setupInfo.contextCount;
				}

				if (setupInfo.dynamicHeapPageSize > 0)
				{
					createInfo.DynamicHeapPageSize = setupInfo.dynamicHeapPageSize;
				}

				createInfo.Features = new DeviceFeatures()
				{
					MultiViewport = DeviceFeatureState.Enabled,
				};

				engineFactory.CreateDeviceAndContextsD3D12(createInfo, out var renderDevice, out var deviceContexts);
				_engineFactory = engineFactory;
				_immediateContext = deviceContexts[0];
				_deferredDevices = deviceContexts.ToArray();
				_renderDevice = renderDevice;

				var wndSize = WindowHandle.Size;
				var swapChainDesc = new SwapChainDesc
				{
					Width = (uint)wndSize.X,
					Height = (uint)wndSize.Y,
					BufferCount = 2,
					Usage = SwapChainUsageFlags.RenderTarget,
					IsPrimary = true,
					ColorBufferFormat = Diligent.TextureFormat.RGBA8_UNorm,
					DepthBufferFormat = Diligent.TextureFormat.D32_Float
				};

				OnSwapChainInfo?.Invoke();
				var swapChain = engineFactory.CreateSwapChainD3D12(renderDevice, deviceContexts[0], swapChainDesc,
					new FullScreenModeDesc(), new Win32NativeWindow { Wnd = WindowHandle.Handle });

				_swapChain = swapChain;
			}

			void SetupVulkan()
			{
				var engineFactory = Native.GetEngineFactoryVk();
				if (engineFactory is null)
				{
					throw new NullReferenceException($"Failed to get {nameof(IEngineFactoryVk)}");
				}
				engineFactory.SetMessageCallback(OnMessageCallback);

				var adapter = FindBestAdapter(engineFactory);
				var createInfo = new EngineVkCreateInfo
				{
					EnableValidation = true,
					AdapterId = (uint)adapter
				};

				var setupInfo = new GraphicsPipelineSetupInfo
				{
					backend = backend
				};
				OnCreateSetupInfo?.Invoke(setupInfo);
				if (setupInfo.contextCount > 0)
				{
					createInfo.NumDeferredContexts = setupInfo.contextCount;
				}
				if (setupInfo.dynamicHeapSize > 0)
				{
					createInfo.DynamicHeapSize = setupInfo.dynamicHeapSize;
				}
				if (setupInfo.dynamicHeapPageSize > 0)
				{
					createInfo.DynamicHeapPageSize = setupInfo.dynamicHeapPageSize;
				}

				createInfo.Features = new DeviceFeatures()
				{
					MultiViewport = DeviceFeatureState.Enabled,
				};

				engineFactory.CreateDeviceAndContextsVk(createInfo, out var renderDevice, out var deviceContexts);
				_engineFactory = engineFactory;
				_immediateContext = deviceContexts[0];
				_deferredDevices = deviceContexts.ToArray();
				_renderDevice = renderDevice;

				var wndSize = WindowHandle.Size;
				var swapChainDesc = new SwapChainDesc
				{
					Width = (uint)wndSize.X,
					Height = (uint)wndSize.Y,
					BufferCount = 3, // Use triple buffering for Vulkan
					Usage = SwapChainUsageFlags.RenderTarget,
					IsPrimary = true,
					ColorBufferFormat = Diligent.TextureFormat.RGBA8_UNorm,
					DepthBufferFormat = Diligent.TextureFormat.D32_Float
				};

				OnSwapChainInfo?.Invoke();

				var swapChain = engineFactory.CreateSwapChainVk(renderDevice, deviceContexts[0], swapChainDesc, new Win32NativeWindow { Wnd = WindowHandle.Handle });
				_swapChain = swapChain;
			}

			_frameFence = Device.CreateFence(new FenceDesc { Name = "Frame Fence" });
		}

		private void OnWindowHandleResize()
		{
			SwapChain.Resize((uint)WindowHandle.Size.X, (uint)WindowHandle.Size.Y, SurfaceTransform.Identity);
		}

		private int FindBestAdapter(IEngineFactory factory)
		{
			var adapters = factory.EnumerateAdapters(new Version(11, 1)).ToList();

			GraphicsAdapterInfo suitableAdapter = default;
			int adapterIdx = 0;

			for (int i = 0; i < adapters.Count; i++)
			{
				if (adapters[i].Type == AdapterType.Discrete)
				{
					adapterIdx = i;
					suitableAdapter = adapters[i];
					break;
				}
			}

			if (suitableAdapter.DeviceId != 0)
			{
				return adapterIdx;
			}

			for (int i = 0; i < adapters.Count; i++)
			{
				if (adapters[i].Type == AdapterType.Integrated)
				{
					adapterIdx = i;
					suitableAdapter = adapters[i];
					break;
				}
			}

			if (suitableAdapter.DeviceId != 0)
			{
				return adapterIdx;
			}

			suitableAdapter = adapters.FirstOrDefault();

			return suitableAdapter.DeviceId != 0 ? 0 : throw new NullReferenceException("There's no graphics adapter available.");
		}

		public void Present()
		{
			SwapChain.Present(0);
			
			// Frame synchronization: limit to 2 frames in flight
			/*var fenceValue = _nextFrameValue++;
			ImmediateContext.EnqueueSignal(_frameFence, fenceValue);
			
			if (fenceValue > 2)
			{
				_frameFence.Wait(fenceValue - 2);
			}*/
		}

		public void Release()
		{
			_frameFence?.Dispose();
			PsoManager?.Dispose();
			_swapChain?.Dispose();
			foreach (var deviceContext in _deferredDevices)
			{
				deviceContext.Dispose();
			}

			_immediateContext?.Dispose();
			_renderDevice?.Dispose();

			WindowHandle.OnWindowResize -= OnWindowHandleResize;
		}
	}
}