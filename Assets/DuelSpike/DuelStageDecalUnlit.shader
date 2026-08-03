// 背番号デカール（issue #393）。ジャージの上に貼る薄板メッシュ用の最小シェーダ。
// _MainTex はランタイム生成の数字ビットマップ（背景=透明・数字=不透明の白＋濃色の縁取り）。
// 電光掲示板と同じ発光風の均一表示にするため、DuelStageProxyLambert と違い陰影は付けない（design-16）。
// _CullMode は DuelStageProxyLambert と同じ仕組み（DuelStageRig.SetMirrorState が Renderer を
// 総なめして _CullMode プロパティを持つ全マテリアルへ書き戻す）に相乗りする＝
// 左投げ/左打ちのミラー対応（両面描画への切り替え）を自前で配線しなくてよい。
Shader "KokoSim/DuelStageDecalUnlit"
{
    Properties
    {
        _MainTex ("Number Texture", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        _CullMode ("Cull Mode (2=Back, 0=Off)", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        Pass
        {
            Cull [_CullMode]
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _BaseColor;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv) * _BaseColor;
            }
            ENDCG
        }
    }
}
