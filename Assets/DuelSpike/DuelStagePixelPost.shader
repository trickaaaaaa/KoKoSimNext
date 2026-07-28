// design-18 §4.1 ベースライン（自前二段）の Blit 用ポストパス。
// Pass 0: ポスタライズ（ガンマ空間で量子化＝知覚的に均等な帯）
// Pass 1: エッジ合成（_DataTex＝DuelStageDepthNormal 出力から深度/法線の不連続を検出し暗色1pxを重畳）
// いずれも低解像度RT上で実行する（1px＝表示上の1ドット）。
Shader "KokoSim/DuelStagePixelPost"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _DataTex ("Depth+Normal", 2D) = "black" {}
        _PosterLevels ("Posterize Levels", Float) = 6
        _EdgeColor ("Edge Color", Color) = (0.23, 0.14, 0.09, 1)  // 焦げ茶（§4.2: 純黒は硬すぎ）
        _DepthThreshold ("Depth Threshold (0..1 of MaxDepth)", Float) = 0.02
        _NormalThreshold ("Normal Threshold (1-dot)", Float) = 0.4
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // Pass 0: ポスタライズ
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _PosterLevels;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                float3 g = pow(saturate(c.rgb), 1.0 / 2.2);
                g = floor(g * _PosterLevels + 0.5) / _PosterLevels;
                return fixed4(pow(g, 2.2), c.a);
            }
            ENDCG
        }

        // Pass 1: エッジ合成
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            sampler2D _DataTex;
            fixed4 _EdgeColor;
            float _DepthThreshold;
            float _NormalThreshold;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 px = _MainTex_TexelSize.xy;
                float4 c0 = tex2D(_DataTex, i.uv);
                float4 cr = tex2D(_DataTex, i.uv + float2(px.x, 0));
                float4 cu = tex2D(_DataTex, i.uv + float2(0, px.y));

                float depthEdge = max(abs(cr.r - c0.r), abs(cu.r - c0.r));
                float3 n0 = c0.gba * 2.0 - 1.0;
                float3 nr = cr.gba * 2.0 - 1.0;
                float3 nu = cu.gba * 2.0 - 1.0;
                float normalEdge = max(1.0 - dot(n0, nr), 1.0 - dot(n0, nu));

                float e = (depthEdge > _DepthThreshold || normalEdge > _NormalThreshold) ? 1.0 : 0.0;
                fixed4 col = tex2D(_MainTex, i.uv);
                return fixed4(lerp(col.rgb, _EdgeColor.rgb, e), col.a);
            }
            ENDCG
        }
    }
}
