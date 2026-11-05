// 自发光（灯管、指示灯、屏幕、实验舱光环）。
//
// 为什么不用 Standard 的 _EMISSION：运行时 new Material 才设 EnableKeyword("_EMISSION")，
// 打包时这个变体常常被剥离，结果材质一声不响地变成普通不发光表面 ——
// 灯带不亮、指示灯不亮，肉眼以为是「参数没调对」，其实是变体没了。
// 这里用 Unlit 写死：颜色就是最终颜色，留一点基于视线角的法线明暗让球体仍有体积。
Shader "ChargeLab/Glow"
{
    Properties
    {
        _Color ("Color", Color) = (0.6, 0.9, 1.0, 1)
        _Shade ("Shading", Range(0, 1)) = 0.3
    }

    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Shade;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 viewDir : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = WorldSpaceViewDir(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                float3 v = normalize(i.viewDir);
                // 只有中心正对镜头的地方最亮，边缘略暗 —— 平涂的球看起来才不是一张纸片
                float facing = saturate(dot(n, v));
                float k = lerp(1.0, 0.55 + 0.45 * facing, _Shade);
                return fixed4(_Color.rgb * k, _Color.a);
            }
            ENDCG
        }
    }

    FallBack Off
}
