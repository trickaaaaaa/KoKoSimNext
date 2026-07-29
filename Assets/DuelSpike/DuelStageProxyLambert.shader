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
// 縦縞（§3.5.3・2026-07-29実装）: テクスチャに焼かず、地色領域（ユニ上・ズボン）の上へ手続き生成する。
//
// 刻み座標は **UV1.y に焼いたバインドポーズの左右座標**（ベイカが書く）。
// スキニング後の頂点位置（v.vertex.x）で刻んではいけない: スキンメッシュの頂点シェーダが受け取るのは
// 変形後の座標なので、縞が空間に固定されて「体が縞の中を通り抜ける」絵になる（2026-07-29 実害・監督指摘）。
// 静止座標で刻めば、腕を上げても体を捻っても縞は生地に付いたまま一緒に伸び縮みする＝実物の印刷と同じ。
//
// _StripeFreq は「刻み座標1単位あたりの縞の本数」＝呼び元がアクターのスケールを掛けて渡す
// （データ側は本/m で持つ。DuelStageRig.ApplyRegionColors 参照）。
Shader "KokoSim/DuelStageProxyLambert"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _LightDir ("Light Direction (world, toward surface)", Vector) = (-0.35, -0.85, -0.4, 0)
        _Ambient ("Ambient Floor", Range(0, 1)) = 0.45
        _UseRegions ("Use Region Colors (needs UV1 region ids)", Float) = 0
        _StripeOn ("Stripe On", Float) = 0
        _StripeColor ("Stripe Color", Color) = (0, 0, 0, 1)
        _StripeFreq ("Stripe Frequency (per object unit)", Float) = 0
        _StripeDuty ("Stripe Duty (line width ratio)", Range(0.02, 0.5)) = 0.2
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
            float _StripeOn;
            fixed4 _StripeColor;
            float _StripeFreq;
            float _StripeDuty;

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
                float restX : TEXCOORD2;   // 縦縞の刻み座標（バインドポーズの左右座標。UV1.y に焼いてある）
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.region = v.uv1.x;
                o.restX = v.uv1.y;
                return o;
            }

            // 周期1のパルス（[0,duty) が1）を、幅 w の箱フィルタで解析的に平均する。
            // 累積関数 F(x) = floor(x)*duty + min(frac(x), duty) の差分＝厳密な被覆率。
            // 閾値比較（phase < duty）だと1画素より細い線がサンプル位置次第で出たり消えたりして
            // **点線に割れる**（2026-07-29 実害）。w が周期を超えると duty（＝平均色）へ収束する
            float pulseCoverage(float x, float duty, float w)
            {
                float hw = max(w, 1e-6) * 0.5;
                float a = x - hw, b = x + hw;
                float Fb = floor(b) * duty + min(frac(b), duty);
                float Fa = floor(a) * duty + min(frac(a), duty);
                return saturate((Fb - Fa) / max(w, 1e-6));
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 baseCol = _BaseColor.rgb;
                if (_UseRegions > 0.5)
                {
                    int region = (int)(i.region * 8.0);
                    baseCol = _RegionColors[region].rgb;
                    // 縞が乗るのは地色の領域だけ（0=ユニ上 / 1=ズボン）。アンダー・ソックス・帽子・肌は無地。
                    // 実物のストライプもユニフォーム本体にしか入らない
                    if (_StripeOn > 0.5 && region <= 1)
                    {
                        float x = i.restX * _StripeFreq;
                        float cov = pulseCoverage(x, _StripeDuty, fwidth(x));
                        baseCol = lerp(baseCol, _StripeColor.rgb, cov);
                    }
                }
                float l = saturate(dot(normalize(i.normal), -normalize(_LightDir.xyz)));
                float shade = _Ambient + (1.0 - _Ambient) * l;
                return fixed4(baseCol * shade, 1.0);
            }
            ENDCG
        }
    }
}
