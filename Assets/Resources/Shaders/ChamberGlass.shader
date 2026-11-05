// 实验舱的玻璃罩。
//
// 为什么不用 Standard 材质调透明度：运行时 new Material(Shader.Find("Standard")) 再手动
// 设 _Mode / blend / keyword，在打包后经常退化成不透明（变体被剥离时没有任何报错，
// 只是看起来像一块实心塑料）。玻璃罩又是「必须能看穿」的部件，赌不起。
//
// 这里自己写一个最小透明 shader：只算菲涅尔边缘加亮，正视时几乎全透。
// 放在 Resources 下，Resources.Load 一定能拿到，不受剥离影响。
Shader "ChargeLab/ChamberGlass"
{
    Properties
    {
        _Color ("Tint", Color) = (0.42, 0.68, 0.82, 0.20)
        _RimPower ("Rim Power", Range(0.5, 8)) = 2.2
        _RimIntensity ("Rim Intensity", Range(0, 4)) = 1.10
        _RimAlpha ("Rim Alpha", Range(0, 1)) = 0.55
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _RimPower;
            float _RimIntensity;
            float _RimAlpha;

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

                // 正对镜头的地方几乎全透，掠射角沿边缘亮起来 —— 这才是玻璃的读法
                float rim = pow(saturate(1.0 - abs(dot(n, v))), _RimPower);

                float3 col = _Color.rgb + rim * _RimIntensity;
                float a = saturate(_Color.a + rim * _RimAlpha);
                return fixed4(col, a);
            }
            ENDCG
        }
    }

    FallBack Off
}
