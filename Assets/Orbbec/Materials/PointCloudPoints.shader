Shader "Orbbec/PointCloudPoints"
{
    // billboard 四边形点云：每个点由 mesh 提供 4 个共位顶点 + corner(UV) 偏移，
    // 顶点着色器在屏幕空间把它们扩展成一个面向相机的方块。
    // 不依赖 PSIZE（移动端/GLES 普遍忽略 PSIZE，点会被强制成 1px 而看不清）。
    Properties
    {
        _PointSize ("Point Size (px)", Range(0.5, 40)) = 8

        [Toggle] _UseDepthColor ("Use Depth Pseudo Color", Float) = 1
        _DepthMin ("Depth Min (m)", Float) = 0.2
        _DepthMax ("Depth Max (m)", Float) = 4.0

        // 默认 Always(8)：点云始终绘制在 AR 背景之上，避免被遮挡而看不清。
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTestMode ("ZTest", Float) = 8
        [Enum(Off, 0, On, 1)] _ZWriteMode ("ZWrite", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+100" }
        LOD 100
        Cull Off
        ZWrite [_ZWriteMode]
        ZTest [_ZTestMode]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            float _PointSize;
            float _UseDepthColor;
            float _DepthMin;
            float _DepthMax;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 corner : TEXCOORD0; // 四角偏移 (±1, ±1)
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                fixed4 color : COLOR;
                float  depth : TEXCOORD0; // 传感器原始米制深度（物体空间 z），不受 GameObject 缩放影响
            };

            float3 hsv2rgb(float3 c)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            v2f vert (appdata v)
            {
                v2f o;
                float4 clip = UnityObjectToClipPos(v.vertex);

                // 在 clip 空间按屏幕像素扩展：corner(±1) * 半边长(px) -> NDC -> 乘以 w。
                float2 ndc = v.corner * (_PointSize * 0.5) * 2.0 / _ScreenParams.xy;
                clip.xy += ndc * clip.w;

                o.pos = clip;
                o.color = v.color;
                // 用物体空间 z（传感器米制深度）做伪彩：与 GameObject 的缩放/位移无关。
                // 该点云物体在 AR 场景里通常带很大缩放，若用视图空间深度会整体偏远而失去层次。
                o.depth = abs(v.vertex.z);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                if (_UseDepthColor > 0.5)
                {
                    float t = saturate((i.depth - _DepthMin) / max(0.0001, (_DepthMax - _DepthMin)));
                    // 近 = 红, 远 = 蓝/紫，高饱和高亮度，AR 背景中清晰可辨。
                    float hue = t * 0.66;
                    return fixed4(hsv2rgb(float3(hue, 1.0, 1.0)), 1.0);
                }
                return i.color;
            }
            ENDCG
        }
    }
    Fallback Off
}
