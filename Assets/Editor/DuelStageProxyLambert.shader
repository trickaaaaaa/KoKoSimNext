// design-18 §4 ピクセル化ポストスパイク用のプロキシ陰影シェーダ。
// シーンのライト・RenderSettings に依存せず、プロパティの光方向だけで Lambert 陰影を付ける
// （スパイクの決定論とシーン非汚染のため）。本実装では URP Lit＋実ライトに置き換える。
Shader "KokoSim/DuelStageProxyLambert"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _LightDir ("Light Direction (world, toward surface)", Vector) = (-0.35, -0.85, -0.4, 0)
        _Ambient ("Ambient Floor", Range(0, 1)) = 0.45
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

            fixed4 _BaseColor;
            float4 _LightDir;
            float _Ambient;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float l = saturate(dot(normalize(i.normal), -normalize(_LightDir.xyz)));
                float shade = _Ambient + (1.0 - _Ambient) * l;
                return fixed4(_BaseColor.rgb * shade, 1.0);
            }
            ENDCG
        }
    }
}
