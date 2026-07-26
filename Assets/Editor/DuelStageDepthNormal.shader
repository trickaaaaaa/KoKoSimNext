// design-18 §4.1 のエッジ検出（深度/法線）用データパス。差し替えマテリアルとして全レンダラに
// 割り当てて2回目のレンダを行い、R=線形視点深度(0..1)/GBA=ビュー空間法線を出力する。
// 出力先RTは Linear（sRGB変換なし）で作ること。
Shader "KokoSim/DuelStageDepthNormal"
{
    Properties
    {
        _MaxDepth ("Max Depth (m)", Float) = 80
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _MaxDepth;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 viewNormal : TEXCOORD0;
                float eyeDepth : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.viewNormal = mul((float3x3)UNITY_MATRIX_V, UnityObjectToWorldNormal(v.normal));
                o.eyeDepth = -UnityObjectToViewPos(v.vertex).z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float d = saturate(i.eyeDepth / _MaxDepth);
                return float4(d, normalize(i.viewNormal) * 0.5 + 0.5);
            }
            ENDCG
        }
    }
}
