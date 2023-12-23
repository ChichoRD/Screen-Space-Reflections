using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class ScreenSpaceReflectionsRenderPassFeature : ScriptableRendererFeature
{
    class ScreenSpaceReflectionsRenderPass : ScriptableRenderPass
    {
        private const string SCREEN_SPACE_REFLECTIONS_SHADER_PATH = "Hidden/Screen Space Reflections";
        private Material _screenSpaceReflectionsMaterial;
        private static readonly string s_PassName = nameof(ScreenSpaceReflectionsRenderPass);
        private ProfilingSampler _profilingSampler = new ProfilingSampler(s_PassName);

        private ScreenSpaceReflectionsSettings _settings;

        private RenderTargetIdentifier _temporaryBuffer0;
        private static readonly int s_temporaryBuffer0ID = Shader.PropertyToID(s_PassName +nameof(_temporaryBuffer0));

        private RenderTargetIdentifier _temporaryBuffer1;
        private static readonly int s_temporaryBuffer1ID = Shader.PropertyToID(s_PassName + nameof(_temporaryBuffer1));

        private static readonly int s_RayStepID = Shader.PropertyToID("_RayStep");
        private static readonly int s_RayMinStepID = Shader.PropertyToID("_RayMinStep");
        private static readonly int s_RayMaxStepsID = Shader.PropertyToID("_RayMaxSteps");
        private static readonly int s_RayMaxDistanceID = Shader.PropertyToID("_RayMaxDistance");
        private static readonly int s_BinaryHitSearchSteps = Shader.PropertyToID("_BinaryHitSearchSteps");

        private static readonly int s_HitDepthDifferenceThresholdID = Shader.PropertyToID("_HitDepthDifferenceThreshold");
        private static readonly int s_ReflectionIntensityID = Shader.PropertyToID("_ReflectionIntensity");
        private static readonly int s_ReflectivityBiasID = Shader.PropertyToID("_ReflectivityBias");
        private static readonly int s_FresnelBiasID = Shader.PropertyToID("_FresnelBias");

        private static readonly int s_VignetteRadiusID = Shader.PropertyToID("_VignetteRadius");
        private static readonly int s_VignetteSoftnessID = Shader.PropertyToID("_VignetteSoftness");

        private static readonly int s_BlurRadiusID = Shader.PropertyToID("_BlurRadius");

        private LocalKeyword _deferredGBuffersAvailableKeyword;
        private LocalKeyword _reflectSkyboxKeyword;

        public ScreenSpaceReflectionsRenderPass(ScreenSpaceReflectionsSettings settings)
        {
            _settings = settings;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            base.OnCameraSetup(cmd, ref renderingData);

            _screenSpaceReflectionsMaterial ??= _screenSpaceReflectionsMaterial == null
                                                ? CoreUtils.CreateEngineMaterial(SCREEN_SPACE_REFLECTIONS_SHADER_PATH)
                                                : _screenSpaceReflectionsMaterial;

            _screenSpaceReflectionsMaterial.SetFloat(s_RayStepID, _settings.rayStep);
            _screenSpaceReflectionsMaterial.SetFloat(s_RayMinStepID, _settings.rayMinStep);
            _screenSpaceReflectionsMaterial.SetInteger(s_RayMaxStepsID, _settings.rayMaxSteps);
            _screenSpaceReflectionsMaterial.SetFloat(s_RayMaxDistanceID, _settings.rayMaxDistance);
            _screenSpaceReflectionsMaterial.SetInteger(s_BinaryHitSearchSteps, _settings.binaryHitSearchSteps);

            _screenSpaceReflectionsMaterial.SetFloat(s_HitDepthDifferenceThresholdID, _settings.hitDepthDifferenceThreshold);
            _screenSpaceReflectionsMaterial.SetFloat(s_ReflectionIntensityID, _settings.reflectionIntensity);
            _screenSpaceReflectionsMaterial.SetFloat(s_ReflectivityBiasID, _settings.reflectivityBias);
            _screenSpaceReflectionsMaterial.SetFloat(s_FresnelBiasID, _settings.fresnelBias);

            _screenSpaceReflectionsMaterial.SetFloat(s_VignetteRadiusID, _settings.vignetteRadius);
            _screenSpaceReflectionsMaterial.SetFloat(s_VignetteSoftnessID, _settings.vignetteSoftness);

            _deferredGBuffersAvailableKeyword = new LocalKeyword(_screenSpaceReflectionsMaterial.shader, "DEFERRED_GBUFFERS_AVAILABLE");
            _screenSpaceReflectionsMaterial.SetKeyword(_deferredGBuffersAvailableKeyword, _settings.renderingMode switch { RenderingMode.Forward => false, RenderingMode.Deferred => true, _ => false });

            _reflectSkyboxKeyword = new LocalKeyword(_screenSpaceReflectionsMaterial.shader, "REFLECT_SKYBOX");
            _screenSpaceReflectionsMaterial.SetKeyword(_reflectSkyboxKeyword, _settings.reflectSkybox);

            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.colorFormat = _settings.lowerTextureTo16Bit
                                     ? RenderTextureFormat.ARGB4444
                                     : RenderTextureFormat.ARGB32;
            descriptor.depthBufferBits = 0;

            int halveFactor = 1 << _settings.downsamples;
            descriptor.width /= halveFactor;
            descriptor.height /= halveFactor;

            cmd.GetTemporaryRT(s_temporaryBuffer0ID, descriptor, FilterMode.Bilinear);
            _temporaryBuffer0 = new RenderTargetIdentifier(s_temporaryBuffer0ID);

            cmd.GetTemporaryRT(s_temporaryBuffer1ID, descriptor, FilterMode.Bilinear);
            _temporaryBuffer1 = new RenderTargetIdentifier(s_temporaryBuffer1ID);

            ConfigureInput(ScriptableRenderPassInput.Color
                           | ScriptableRenderPassInput.Depth
                           | ScriptableRenderPassInput.Normal);
            ConfigureTarget(_temporaryBuffer0);
            ConfigureClear(ClearFlag.All, Color.black);
            
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();

            var colorTarget = renderingData.cameraData.renderer.cameraColorTarget;

            using (new ProfilingScope(cmd, _profilingSampler))
            {
               cmd.Blit(colorTarget, _temporaryBuffer1);
               cmd.Blit(_temporaryBuffer1, _temporaryBuffer0, _screenSpaceReflectionsMaterial, 0);

                foreach (int blurRadius in _settings.blurRadii)
                {
                    cmd.SetGlobalInteger(s_BlurRadiusID, blurRadius);
                    cmd.Blit(_temporaryBuffer0, _temporaryBuffer1, _screenSpaceReflectionsMaterial, 1);
                    cmd.Blit(_temporaryBuffer1, _temporaryBuffer0);
                }

                cmd.Blit(_temporaryBuffer0, colorTarget, _screenSpaceReflectionsMaterial, 2);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            base.OnCameraCleanup(cmd);

            cmd ??= CommandBufferPool.Get();
            cmd.ReleaseTemporaryRT(s_temporaryBuffer0ID);
            cmd.ReleaseTemporaryRT(s_temporaryBuffer1ID);
        }
    }

    [Serializable]
    private struct ScreenSpaceReflectionsSettings
    {
        [Min(0.0f)] public float rayStep;
        [Min(0.0f)] public float rayMinStep;
        [Min(0)] public int rayMaxSteps;
        [Min(0.0f)] public float rayMaxDistance;
        [Min(0)] public int binaryHitSearchSteps;

        public bool reflectSkybox;
        [Range(0.0f, 1.0f)] public float hitDepthDifferenceThreshold;
        [Range(0.0f, 1.0f)] public float reflectionIntensity;
        [Range(0.0f, 1.0f)] public float reflectivityBias;
        [Range(0.0f, 1.0f)] public float fresnelBias;

        [Range(0.0f, 1.0f)] public float vignetteRadius;
        [Range(0.0f, 1.0f)] public float vignetteSoftness;

        [Min(0)]
        public int downsamples;
        public bool lowerTextureTo16Bit;

        public RenderingMode renderingMode;
        public int[] blurRadii;
    }

    private ScreenSpaceReflectionsRenderPass _screenSpaceReflectionsPass;

    [SerializeField] private RenderPassEvent _renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    [SerializeField]
    private ScreenSpaceReflectionsSettings _settings = new ScreenSpaceReflectionsSettings()
    {
        rayStep = 0.1f,
        rayMinStep = 0.1f,
        rayMaxSteps = 16,
        rayMaxDistance = 100f,
        binaryHitSearchSteps = 8,

        reflectSkybox = true,
        hitDepthDifferenceThreshold = 0.08f,
        reflectionIntensity = 0.95f,
        reflectivityBias = 0.0f,
        fresnelBias = 0.1f,

        vignetteRadius = 0.15f,
        vignetteSoftness = 0.35f,

        renderingMode = RenderingMode.Deferred,
        blurRadii = new int[] { 0, 1, 2, 2, 3 },
    };

    /// <inheritdoc/>
    public override void Create()
    {
        _screenSpaceReflectionsPass = new ScreenSpaceReflectionsRenderPass(_settings);

        // Configures where the render pass should be injected.
        _screenSpaceReflectionsPass.renderPassEvent = _renderPassEvent;
    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_screenSpaceReflectionsPass);
    }
}


