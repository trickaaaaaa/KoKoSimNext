// ストライクゾーンの重畳（design-18 §4.1 の後段）。
//
// **ポスタライズの後**に描くための最小シェーダ。ポスト前にシーン内で薄いアルファを重ねると、
// 6段の量子化が「同じ段に丸められて消える」か「まるごと1段ずれてベタ板になる」かの二択になり、
// 「うっすら」が絵として存在できない（2026-07-31）。GL 即時モードで Target へ直接引く。
Shader "KokoSim/DuelStageZone"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Overlay" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off ZWrite Off ZTest Always

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target { return i.color; }
            ENDCG
        }
    }
}
