using System;
using System.Collections.Generic;
using DecaEngine.Graphics.Diligent;
using Diligent;

namespace DecaEngine.Graphics.ProbeGi;

public class HiZGenerator : IDisposable
{
    private readonly DiligentGraphicsApi _api;
    
    private readonly IPipelineState _copyPso;
    private readonly IShaderResourceBinding _copySrb;
    
    private readonly IPipelineState _reducePso;
    private readonly List<IShaderResourceBinding> _reduceSrbs1 = new();
    private readonly List<IShaderResourceBinding> _reduceSrbs2 = new();

    private readonly ITexture _hizTexture1;
    private readonly ITexture _hizTexture2;
    private readonly List<ITextureView> _hizSrvs1 = new();
    private readonly List<ITextureView> _hizUavs1 = new();
    private readonly List<ITextureView> _hizSrvs2 = new();
    private readonly List<ITextureView> _hizUavs2 = new();
    
    private readonly uint _mipLevels;
    private readonly uint _width;
    private readonly uint _height;

    public HiZGenerator(DiligentGraphicsApi api, ITexture hizTexture1, ITexture hizTexture2)
    {
        _api = api;
        _hizTexture1 = hizTexture1;
        _hizTexture2 = hizTexture2;

        var copyShader = CreateShader("CopyDepth.hlsl", "main");
        _copyPso = CreatePso(copyShader, "Hi-Z Copy PSO", false);
        _copySrb = _copyPso.CreateShaderResourceBinding(true);

        var reduceShader = CreateShader("HiZReduce.hlsl", "main");
        _reducePso = CreatePso(reduceShader, "Hi-Z Reduce PSO", true);
        
        var desc = _hizTexture1.GetDesc();
        _mipLevels = desc.MipLevels;
        _width = desc.Width;
        _height = desc.Height;

        for (uint mip = 0; mip < _mipLevels; mip++)
        {
            _hizSrvs1.Add(_hizTexture1.CreateView(new TextureViewDesc { ViewType = TextureViewType.ShaderResource, MostDetailedMip = mip, NumMipLevels = 1 }));
            _hizUavs1.Add(_hizTexture1.CreateView(new TextureViewDesc { ViewType = TextureViewType.UnorderedAccess, MostDetailedMip = mip, NumMipLevels = 1 }));
            _hizSrvs2.Add(_hizTexture2.CreateView(new TextureViewDesc { ViewType = TextureViewType.ShaderResource, MostDetailedMip = mip, NumMipLevels = 1 }));
            _hizUavs2.Add(_hizTexture2.CreateView(new TextureViewDesc { ViewType = TextureViewType.UnorderedAccess, MostDetailedMip = mip, NumMipLevels = 1 }));
        }

        for (uint mip = 1; mip < _mipLevels; mip++)
        {
            var srb1 = _reducePso.CreateShaderResourceBinding(true);
            srb1.GetVariableByName(ShaderType.Compute, "inImage").Set(_hizSrvs1[(int)mip - 1], SetShaderResourceFlags.AllowOverwrite);
            srb1.GetVariableByName(ShaderType.Compute, "outImage").Set(_hizUavs1[(int)mip], SetShaderResourceFlags.AllowOverwrite);
            _reduceSrbs1.Add(srb1);

            var srb2 = _reducePso.CreateShaderResourceBinding(true);
            srb2.GetVariableByName(ShaderType.Compute, "inImage").Set(_hizSrvs2[(int)mip - 1], SetShaderResourceFlags.AllowOverwrite);
            srb2.GetVariableByName(ShaderType.Compute, "outImage").Set(_hizUavs2[(int)mip], SetShaderResourceFlags.AllowOverwrite);
            _reduceSrbs2.Add(srb2);
        }
    }

    private IPipelineState CreatePso(IShader shader, string name, bool useImmutableSampler)
    {
        var layoutDesc = new PipelineResourceLayoutDesc { DefaultVariableType = ShaderResourceVariableType.Mutable };
        if (useImmutableSampler)
        {
            layoutDesc.ImmutableSamplers = [ new ImmutableSamplerDesc { 
                ShaderStages = ShaderType.Compute,
                SamplerOrTextureName = "inImage", 
                Desc = new SamplerDesc 
                { 
                    MinFilter = FilterType.Point,
                    MagFilter = FilterType.Point,
                    MipFilter = FilterType.Point,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp
                } 
            } ];
        }
        var psoDesc = new ComputePipelineStateCreateInfo { PSODesc = new PipelineStateDesc { Name = name, PipelineType = PipelineType.Compute, ResourceLayout = layoutDesc }, Cs = shader };
        return _api.Device.CreateComputePipelineState(psoDesc);
    }

    private IShader CreateShader(string fileName, string entryPoint)
    {
        using var factory = _api.EngineFactory.CreateDefaultShaderSourceStreamFactory("EditorAssets/shader");
        var ci = new ShaderCreateInfo { SourceLanguage = ShaderSourceLanguage.Hlsl, Desc = new ShaderDesc { Name = fileName, ShaderType = ShaderType.Compute, UseCombinedTextureSamplers = false }, EntryPoint = entryPoint, FilePath = fileName, ShaderSourceStreamFactory = factory };
        return _api.Device.CreateShader(ci, out var blob);
    }

    public void Generate(ITexture sourceDepth, bool useFirstTexture)
    {
        var context = _api.ImmediateContext;
        var reduceSrbs = useFirstTexture ? _reduceSrbs1 : _reduceSrbs2;
        var hizTexture = useFirstTexture ? _hizTexture1 : _hizTexture2;
        var uavs = useFirstTexture ? _hizUavs1 : _hizUavs2;

        context.TransitionResourceStates([new StateTransitionDesc {
            Resource = hizTexture,
            OldState = ResourceState.Unknown,
            NewState = ResourceState.UnorderedAccess,
            FirstMipLevel = 0,
            MipLevelsCount = 1
        }]);

        var sourceSrv = sourceDepth.GetDefaultView(TextureViewType.ShaderResource);
        _copySrb.GetVariableByName(ShaderType.Compute, "inImage").Set(sourceSrv, SetShaderResourceFlags.AllowOverwrite);
        _copySrb.GetVariableByName(ShaderType.Compute, "outImage").Set(uavs[0], SetShaderResourceFlags.AllowOverwrite);

        context.SetPipelineState(_copyPso);
        context.CommitShaderResources(_copySrb, ResourceStateTransitionMode.Transition);
        context.DispatchCompute(new DispatchComputeAttribs { ThreadGroupCountX = (_width + 7) / 8, ThreadGroupCountY = (_height + 7) / 8, ThreadGroupCountZ = 1 });

        context.TransitionResourceStates([new StateTransitionDesc {
            Resource = hizTexture,
            OldState = ResourceState.UnorderedAccess,
            NewState = ResourceState.UnorderedAccess,
            FirstMipLevel = 0,
            MipLevelsCount = 1,
            TransitionType = StateTransitionType.Immediate
        }]);

        context.SetPipelineState(_reducePso);
        
        uint currentWidth = _width;
        uint currentHeight = _height;

        for (uint mip = 1; mip < _mipLevels; mip++)
        {
            context.TransitionResourceStates([
                new StateTransitionDesc {
                    Resource = hizTexture,
                    OldState = ResourceState.UnorderedAccess,
                    NewState = ResourceState.ShaderResource,
                    FirstMipLevel = mip - 1,
                    MipLevelsCount = 1
                },
                new StateTransitionDesc {
                    Resource = hizTexture,
                    OldState = ResourceState.Unknown, // CORRECTED: Use Unknown to discard previous mip content
                    NewState = ResourceState.UnorderedAccess,
                    FirstMipLevel = mip,
                    MipLevelsCount = 1
                }
            ]);

            currentWidth = Math.Max(1u, (currentWidth + 1) / 2);
            currentHeight = Math.Max(1u, (currentHeight + 1) / 2);
            
            context.CommitShaderResources(reduceSrbs[(int)mip - 1], ResourceStateTransitionMode.None);

            context.DispatchCompute(new DispatchComputeAttribs { 
                ThreadGroupCountX = (currentWidth + 7) / 8,
                ThreadGroupCountY = (currentHeight + 7) / 8,
                ThreadGroupCountZ = 1 
            });

            context.TransitionResourceStates([new StateTransitionDesc {
                Resource = hizTexture,
                OldState = ResourceState.UnorderedAccess,
                NewState = ResourceState.UnorderedAccess,
                FirstMipLevel = mip,
                MipLevelsCount = 1,
                TransitionType = StateTransitionType.Immediate
            }]);
        }

        context.TransitionResourceStates([new StateTransitionDesc {
            Resource = hizTexture,
            OldState = ResourceState.Unknown,
            NewState = ResourceState.ShaderResource,
            FirstMipLevel = 0,
            MipLevelsCount = _mipLevels
        }]);
    }

    public void Dispose()
    {
        _hizSrvs1.ForEach(v => v.Dispose());
        _hizUavs1.ForEach(v => v.Dispose());
        _hizSrvs2.ForEach(v => v.Dispose());
        _hizUavs2.ForEach(v => v.Dispose());
        
        _reduceSrbs1.ForEach(s => s.Dispose());
        _reduceSrbs2.ForEach(s => s.Dispose());

        _copyPso?.Dispose();
        _copySrb?.Dispose();
        _reducePso?.Dispose();
    }
}