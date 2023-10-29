using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

#if UNITY_EDITOR
using UnityEditor;
#endif


public class VolumetricLightFeature : ScriptableRendererFeature
{
    public VolumetricResolution resolution;
    public float lightRange;
    public float falloffRange;

    private VolumetricLightPass lightPass;

    private Shader bilateralBlur;
    private Shader volumetricLight;


    public override void Create()
    {
        ValidateShaders();

        lightPass = new VolumetricLightPass(bilateralBlur, volumetricLight) 
        { 
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques,
            resolution = resolution,
            feature = this,
            lightRange = lightRange,
            falloffRange = falloffRange
        };
    }


    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!renderingData.cameraData.isPreviewCamera)
        {
            lightPass.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(lightPass);
        }
    }


    void ValidateShaders() 
    {
        bilateralBlur = AddAlwaysIncludedShader("Hidden/BilateralBlur");
        volumetricLight = AddAlwaysIncludedShader("Hidden/VolumetricLight");

        if (bilateralBlur == null) 
            Debug.LogError($"BilateralBlur shader missing! Make sure 'Hidden/BilateralBlur' is located somewhere in your project and included in 'Always Included Shaders'", this);

        if (volumetricLight == null)
            Debug.LogError($"VolumetricLight shader missing! Make sure 'Hidden/VolumetricLight' is located somewhere in your project and included in 'Always Included Shaders'", this);
    }


    static Shader AddAlwaysIncludedShader(string shaderName)
    {
        var shader = Shader.Find(shaderName);
        if (shader == null) 
        {
            return null;
        }
     
#if UNITY_EDITOR
        var graphicsSettingsObj = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
        var serializedObject = new SerializedObject(graphicsSettingsObj);
        var arrayProp = serializedObject.FindProperty("m_AlwaysIncludedShaders");
        bool hasShader = false;

        for (int i = 0; i < arrayProp.arraySize; ++i)
        {
            var arrayElem = arrayProp.GetArrayElementAtIndex(i);
            if (shader == arrayElem.objectReferenceValue)
            {
                hasShader = true;
                break;
            }
        }
     
        if (!hasShader)
        {
            int arrayIndex = arrayProp.arraySize;
            arrayProp.InsertArrayElementAtIndex(arrayIndex);
            var arrayElem = arrayProp.GetArrayElementAtIndex(arrayIndex);
            arrayElem.objectReferenceValue = shader;
     
            serializedObject.ApplyModifiedProperties();
     
            AssetDatabase.SaveAssets();
        }
#endif

        return shader;
    }
}
