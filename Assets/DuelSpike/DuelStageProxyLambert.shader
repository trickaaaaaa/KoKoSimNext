// design-18 §4 ピクセル化ポストスパイク用のプロキシ陰影シェーダ。
// シーンのライト・RenderSettings に依存せず、プロパティの光方向だけで Lambert 陰影を付ける
// （スパイクの決定論とシーン非汚染のため）。本実装では URP Lit＋実ライトに置き換える。
//
// 領域塗り分け（design-18 §3.5 U2着衣メッシュ用・2026-07-28）:
// _UseRegions=1 のとき、UV1.x（DuelUniformMeshBuilder が載せた領域ID (id+0.5)/8）で
// _RegionColors[8]（SetColorArray でスクリプトから設定・Properties宣言不可）を引く。
// _UseRegions=0（既定）は従来どおり _BaseColor 一色＝地形・小道具の既存呼び出しは無改修で動く。
// 1アクター1マテリアルを維持する（サブメッシュ分割にしない）＝DuelMotionCatalog.BakeRig の
// sharedMaterial（単数）前提を壊さない。
//
// 縦縞（§3.5.3）は次段: _StripeOn/_StripeColor/_StripeFreq の枠だけ確保してある（未実装・常に無視）。
Shader "KokoSim/DuelStageProxyLambert"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _LightDir ("Light Direction (world, toward surface)", Vector) = (-0.35, -0.85, -0.4, 0)
        _Ambient ("Ambient Floor", Range(0, 1)) = 0.45
        _UseRegions ("Use Region Colors (needs UV1 region ids)", Float) = 0
        _StripeOn ("Stripe On (reserved)", Float) = 0
        _StripeColor ("Stripe Color (reserved)", Color) = (0, 0, 0, 1)
        _StripeFreq ("Stripe Frequency (reserved)", Float) = 0
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
            float _UseRegions;
            float4 _RegionColors[8];

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv1 : TEXCOORD1;   // 領域ID（mesh.uv2）。非溶接メッシュなので三角形内で定数
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float region : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.region = v.uv1.x;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 baseCol = _BaseColor.rgb;
                if (_UseRegions > 0.5)
                    baseCol = _RegionColors[(int)(i.region * 8.0)].rgb;
                float l = saturate(dot(normalize(i.normal), -normalize(_LightDir.xyz)));
                float shade = _Ambient + (1.0 - _Ambient) * l;
                return fixed4(baseCol * shade, 1.0);
            }
            ENDCG
        }
    }
}
