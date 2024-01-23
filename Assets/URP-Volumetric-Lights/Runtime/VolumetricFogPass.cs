using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;

using Unity.Collections;

using System.Reflection;
using System.Collections.Generic;


public class VolumetricFogPass : ScriptableRenderPass
{    
    public static readonly GlobalKeyword reprojectionKeyword = GlobalKeyword.Create("TEMPORAL_REPROJECTION_ENABLED");
    private static readonly GlobalKeyword fullDepthSource = GlobalKeyword.Create("SOURCE_FULL_DEPTH");


    readonly struct RTPair
    {
        public readonly int propertyId;
        public readonly RenderTargetIdentifier identifier;

        public RTPair(string propertyName)
        {
            propertyId = Shader.PropertyToID(propertyName);
            identifier = new(propertyId);
        }

        public static implicit operator int(RTPair a) => a.propertyId;
        public static implicit operator RenderTargetIdentifier(RTPair a) => a.identifier; 
    }


    // Depth render targets
    private static readonly RTPair halfDepth = new RTPair("_HalfDepthTarget");
    private static readonly RTPair quarterDepth = new RTPair("_QuarterDepthTarget");

    // Light render targets
    private static readonly RTPair volumeFog = new RTPair("_VolumeFogTexture");
    private static readonly RTPair halfVolumeFog = new RTPair("_HalfVolumeFogTexture");
    private static readonly RTPair quarterVolumeFog = new RTPair("_QuarterVolumeFogTexture");

    // Temp render target 
    private static readonly RTPair temp = new RTPair("_Temp");


    // Bullcrap. I won't explain why--just this is bullcrap.
    private static readonly FieldInfo shadowPassField = typeof(UniversalRenderer).GetField("m_AdditionalLightsShadowCasterPass", BindingFlags.NonPublic | BindingFlags.Instance);

    // Try to extract the private shadow caster field with reflection
    private static bool GetShadowCasterPass(ref RenderingData renderingData, out AdditionalLightsShadowCasterPass pass)
    {
        pass = shadowPassField.GetValue(renderingData.cameraData.renderer) as AdditionalLightsShadowCasterPass;
        return pass != null;
    }


    private static readonly Plane[] cullingPlanes = new Plane[6];


    // Global set of active volumes
    private static readonly HashSet<FogVolume> activeVolumes = new();

    public static void AddVolume(FogVolume volume) => activeVolumes.Add(volume);
    public static void RemoveVolume(FogVolume volume) => activeVolumes.Remove(volume);


    // Global material references
    private static Material bilateralBlur;
    private static Shader fogShader;
    private static Material blitAdd;
    private static Material reprojection;


    private readonly VolumetricFogFeature feature;
    private CommandBuffer commandBuffer;

    private VolumetricResolution Resolution
    {
        get
        {
            // Temporal reprojection forces full-res rendering
            if (feature.resolution != VolumetricResolution.Full && feature.temporalReprojection)
                return VolumetricResolution.Full;
            
            return feature.resolution;
        }   
    }


    // Previous frame reprojection matrices
    private Matrix4x4 prevVMatrix;
    private Matrix4x4 prevVpMatrix;
    private Matrix4x4 prevInvVpMatrix;

    // Temporal pass iterator
    private int temporalPass;

    // Temporal Reprojection Target-
    // NOTE: only a RenderTexture seems to preserve information between frames on my device, otherwise I'd use an RTHandle or RenderTargetIdentifier
    private RenderTexture reprojectionBuffer;
    


    public VolumetricFogPass(VolumetricFogFeature feature, Shader blur, Shader fog, Shader add, Shader reproj)
    {
        this.feature = feature;

        fogShader = fog;

        if (bilateralBlur == null || bilateralBlur.shader != blur)
            bilateralBlur = new Material(blur);

        if (blitAdd == null || blitAdd.shader != add)
            blitAdd = new Material(add);

        if (reprojection == null || reprojection.shader != reproj)
            reprojection = new Material(reproj);
    }   


    // Allocate temporary textures
    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data)
    {
        RenderTextureDescriptor descriptor = data.cameraData.cameraTargetDescriptor;

        int width = descriptor.width;
        int height = descriptor.height;
        var colorFormat = RenderTextureFormat.ARGBHalf;
        var depthFormat = RenderTextureFormat.RFloat;

        cmd.GetTemporaryRT(volumeFog, width, height, 0, FilterMode.Point, colorFormat);

        if (Resolution == VolumetricResolution.Half)
            cmd.GetTemporaryRT(halfVolumeFog, width / 2, height / 2, 0, FilterMode.Bilinear, colorFormat);

        // Half/Quarter res both need half-res depth buffer for downsampling
        if (Resolution == VolumetricResolution.Half || Resolution == VolumetricResolution.Quarter)
            cmd.GetTemporaryRT(halfDepth, width / 2, height / 2, 0, FilterMode.Point, depthFormat);

        if (Resolution == VolumetricResolution.Quarter)
        {
            cmd.GetTemporaryRT(quarterVolumeFog, width / 4, height / 4, 0, FilterMode.Bilinear, colorFormat);
            cmd.GetTemporaryRT(quarterDepth, width / 4, height / 4, 0, FilterMode.Point, depthFormat);
        }
    }


    // Package visible lights and initialize lighting data
    private List<NativeLight> SetupLights(ref RenderingData renderingData)
    {
        GetShadowCasterPass(ref renderingData, out AdditionalLightsShadowCasterPass shadowPass);

        LightData lightData = renderingData.lightData;
        NativeArray<VisibleLight> visibleLights = lightData.visibleLights;

        List<NativeLight> initializedLights = new();

        for (int i = 0; i < visibleLights.Length; i++)
        {
            var visibleLight = visibleLights[i];

            // Curse you unity internals
            int shadowIndex = shadowPass.GetShadowLightIndexFromLightIndex(i);

            NativeLight light = new()
            {
                isDirectional = visibleLight.lightType == LightType.Directional,
                shadowIndex = i == lightData.mainLightIndex ? -1 : shadowIndex, // Main light gets special treatment
                range = visibleLight.range,
                layer = visibleLight.light.gameObject.layer
            };

            // Set up light properties
            UniversalRenderPipeline.InitializeLightConstants_Common(visibleLights, i,
                out light.position,
                out light.color, 
                out light.attenuation,
                out light.spotDirection,
                out _
            );

            initializedLights.Add(light);
        }

        return initializedLights;
    }


    // Cull active volumes and package only the visible ones
    private List<FogVolume> SetupVolumes(ref RenderingData renderingData)
    {
        Camera camera = renderingData.cameraData.camera;
        GeometryUtility.CalculateFrustumPlanes(camera, cullingPlanes);
        Vector3 camPos = camera.transform.position;

        List<FogVolume> fogVolumes = new();

        foreach (FogVolume volume in activeVolumes)
        {
            if (!volume.CullVolume(camPos, cullingPlanes))
                fogVolumes.Add(volume);
        }
        
        return fogVolumes;
    }

    
    // Draw all of the volumes into the active render target
    private void DrawVolumes(List<FogVolume> volumes, ref RenderingData renderingData)
    {
        List<NativeLight> lights = SetupLights(ref renderingData);

        int perObjectLightCount = renderingData.lightData.maxPerObjectAdditionalLightsCount;

        for (int i = 0; i < volumes.Count; i++)
            volumes[i].DrawVolume(ref renderingData, commandBuffer, fogShader, lights, perObjectLightCount);
    }


    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        var fogVolumes = SetupVolumes(ref renderingData);
        if (fogVolumes.Count == 0)
            return;

        var renderer = renderingData.cameraData.renderer;
        var descriptor = renderingData.cameraData.cameraTargetDescriptor;

        #if UNITY_2022_1_OR_NEWER
            var cameraColor = renderer.cameraColorTargetHandle;
        #else
            var cameraColor = renderer.cameraColorTarget;
        #endif

        commandBuffer = CommandBufferPool.Get("Volumetric Fog Pass");

        DownsampleDepthBuffer();

        InitFogRenderTarget(renderingData.cameraData.camera);
        
        DrawVolumes(fogVolumes, ref renderingData);

        SetReprojectionBuffer(ref renderingData);

        BilateralBlur(descriptor.width, descriptor.height);
        BlendFog(cameraColor, ref renderingData);

        context.ExecuteCommandBuffer(commandBuffer);
        CommandBufferPool.Release(commandBuffer);
    }


    // Release temporary textures
    public override void OnCameraCleanup(CommandBuffer cmd)
    {
        cmd.ReleaseTemporaryRT(volumeFog);

        if (Resolution == VolumetricResolution.Half)
            cmd.ReleaseTemporaryRT(halfVolumeFog);

        if (Resolution == VolumetricResolution.Half || Resolution == VolumetricResolution.Quarter)
            cmd.ReleaseTemporaryRT(halfDepth);

        if (Resolution == VolumetricResolution.Quarter)
        {
            cmd.ReleaseTemporaryRT(quarterVolumeFog);
            cmd.ReleaseTemporaryRT(quarterDepth);
        }
    }


    // Additively blend the fog volumes with the scene
    private void BlendFog(RenderTargetIdentifier target, ref RenderingData data)
    {
        commandBuffer.GetTemporaryRT(temp, data.cameraData.cameraTargetDescriptor);
        commandBuffer.Blit(target, temp);

        commandBuffer.SetGlobalTexture("_BlitSource", temp);
        commandBuffer.SetGlobalTexture("_BlitAdd", volumeFog);

        // Use blit add kernel to merge target color and the light buffer
        TargetBlit(commandBuffer, target, blitAdd, 0);
 
        commandBuffer.ReleaseTemporaryRT(temp);
    }


    // Equivalent to CommandBuffer.Blit, except for the use of a custom mesh and lack of a source
    private static void TargetBlit(CommandBuffer cmd, RenderTargetIdentifier destination, Material material, int pass)
    {
        cmd.SetRenderTarget(destination);
        cmd.DrawMesh(MeshUtility.FullscreenMesh, Matrix4x4.identity, material, 0, pass);
    }


    // Blurs the active resolution texture, upscaling to full resolution if needed
    private void BilateralBlur(int width, int height)
    {
        Resolution.SetResolutionKeyword(commandBuffer);

        // Blur quarter-res texture and upsample to full res
        if (Resolution == VolumetricResolution.Quarter)
        {
            BilateralBlur(quarterVolumeFog, quarterDepth, width / 4, height / 4); 
            Upsample(quarterVolumeFog, quarterDepth, volumeFog);
            return;
        }
        
        // Blur half-res texture and upsample to full res
        if (Resolution == VolumetricResolution.Half)
        {
            BilateralBlur(halfVolumeFog, halfDepth, width / 2, height / 2);
            Upsample(halfVolumeFog, halfDepth, volumeFog);
            return;
        }

        if (feature.disableBlur)
            return;

        // Blur full-scale texture 
        BilateralBlur(volumeFog, null, width, height);
    }


    // Blurs source texture with provided depth texture- uses camera depth if null
    private void BilateralBlur(RenderTargetIdentifier source, RenderTargetIdentifier? depthBuffer, int sourceWidth, int sourceHeight)
    {
        commandBuffer.GetTemporaryRT(temp, sourceWidth, sourceHeight, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);

        SetDepthTexture("_DepthTexture", depthBuffer);

        // Horizontal blur
        commandBuffer.SetGlobalTexture("_BlurSource", source);
        TargetBlit(commandBuffer, temp, bilateralBlur, 0);

        // Vertical blur
        commandBuffer.SetGlobalTexture("_BlurSource", temp);
        TargetBlit(commandBuffer, source, bilateralBlur, 1);

        commandBuffer.ReleaseTemporaryRT(temp);
    }


    // Downsamples depth texture to active resolution buffer
    private void DownsampleDepthBuffer()
    {
        if (Resolution == VolumetricResolution.Half || Resolution == VolumetricResolution.Quarter)
        {
            SetDepthTexture("_DownsampleSource", null);
            TargetBlit(commandBuffer, halfDepth, bilateralBlur, 2);
        }

        if (Resolution == VolumetricResolution.Quarter)
        {
            SetDepthTexture("_DownsampleSource", halfDepth);
            TargetBlit(commandBuffer, quarterDepth, bilateralBlur, 2);
        }
    }


    // Perform depth-aware upsampling to the destination
    private void Upsample(RenderTargetIdentifier sourceColor, RenderTargetIdentifier sourceDepth, RenderTargetIdentifier destination)
    {
        commandBuffer.SetGlobalTexture("_DownsampleColor", sourceColor);
        commandBuffer.SetGlobalTexture("_DownsampleDepth", sourceDepth);

        TargetBlit(commandBuffer, destination, bilateralBlur, 3);
    }


    // Use shader variants to either 
    // 1: Use the depth texture being assigned 
    // 2: Use the builtin _CameraDepthTexture property
    private void SetDepthTexture(string textureId, RenderTargetIdentifier? depth)
    {
        commandBuffer.SetKeyword(fullDepthSource, !depth.HasValue);

        if (depth.HasValue)
            commandBuffer.SetGlobalTexture(textureId, depth.Value);
    }


    // Set the current and previous matrices neccesary to generate motion vectors, since the unity builtins aren't reliable in edit mode
    private void SetReprojectionMatrices(Camera cam)
    {
        Matrix4x4 vpMatrix = cam.worldToCameraMatrix * cam.projectionMatrix;
        Matrix4x4 invVpMatrix = vpMatrix.inverse;

        commandBuffer.SetGlobalMatrix("_PrevView", prevVMatrix);
        commandBuffer.SetGlobalMatrix("_PrevViewProjection", prevVpMatrix);
        commandBuffer.SetGlobalMatrix("_PrevInvViewProjection", prevInvVpMatrix);

        commandBuffer.SetGlobalMatrix("_CameraView", cam.worldToCameraMatrix);
        commandBuffer.SetGlobalMatrix("_CameraViewProjection", vpMatrix);
        commandBuffer.SetGlobalMatrix("_InverseViewProjection", invVpMatrix);

        prevVMatrix = cam.worldToCameraMatrix;
        prevVpMatrix = vpMatrix;
        prevInvVpMatrix = invVpMatrix;
    }   


    // Set the volumetric fog render target
    // Clear the target if there is nothing to reproject
    // Otherwise, reproject the previous frame
    private void InitFogRenderTarget(Camera cam)
    {
        commandBuffer.SetKeyword(reprojectionKeyword, feature.temporalReprojection);

        if (!feature.temporalReprojection || reprojectionBuffer == null || !reprojectionBuffer.IsCreated())
        {
            if (Resolution == VolumetricResolution.Quarter)
                commandBuffer.SetRenderTarget(quarterVolumeFog);
            else if (Resolution == VolumetricResolution.Half)
                commandBuffer.SetRenderTarget(halfVolumeFog);
            else
                commandBuffer.SetRenderTarget(volumeFog);

            commandBuffer.ClearRenderTarget(true, true, Color.black);
            return;
        }

        SetReprojectionMatrices(cam);

        temporalPass = (temporalPass + 1) % feature.temporalPassCount;
        commandBuffer.SetGlobalInt("_TemporalPassCount", feature.temporalPassCount);
        commandBuffer.SetGlobalInt("_TemporalPass", temporalPass);

        commandBuffer.SetGlobalTexture("_ReprojectSource", reprojectionBuffer);

        // TargetBlit will set the RenderTarget for us
        TargetBlit(commandBuffer, volumeFog, reprojection, 0);
    }


    // Create the reprojection buffer from the current frame's texture to use next frame
    private void SetReprojectionBuffer(ref RenderingData data)
    {
        if (!feature.temporalReprojection)
            return;

        RenderTextureDescriptor descriptor = data.cameraData.cameraTargetDescriptor;
        int width = descriptor.width;
        int height = descriptor.height;
        descriptor.colorFormat = RenderTextureFormat.ARGBHalf;

        if (reprojectionBuffer == null || !reprojectionBuffer.IsCreated() || reprojectionBuffer.width != width || reprojectionBuffer.height != height)
        {
            if (reprojectionBuffer != null && reprojectionBuffer.IsCreated())
                reprojectionBuffer.Release();

            reprojectionBuffer = new RenderTexture(descriptor);
            reprojectionBuffer.Create();
        }

        commandBuffer.CopyTexture(volumeFog, 0, 0, reprojectionBuffer, 0, 0);
    }


    public void Dispose()
    {
        if (reprojectionBuffer != null && reprojectionBuffer.IsCreated())
            reprojectionBuffer.Release();
    }
}