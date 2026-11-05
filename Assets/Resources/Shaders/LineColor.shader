// 线渲染（粒子轨迹、理论参考圈、大厅里的圆环）专用。
//
// 为什么不用 Shader.Find("Sprites/Default")：那个 shader 不在 Resources 里、
// 场景也没引用任何 Sprite 时会被打包剥离，运行时 Find 返回 null，
// 兜底到 Unlit/Color —— 而 Unlit/Color 不读顶点色，LineRenderer 设的
// startColor/endColor 全部失效，所有线都画成白色。灰白背景上等于隐形，
// 于是「轨迹线」这个最核心的视觉在学生机器上就是没有的。
// 这里自己写一个吃顶点色的 Unlit：颜色 = 顶点色 × _Color。
Shader "ChargeLab/LineColor"
{
    Properties
    {
        _Color ("Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }

    FallBack Off
}
