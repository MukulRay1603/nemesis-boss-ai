// Composites the HUD over the recorded scene (EvalRunner -nemesisRecord).
// URP writes opaque alpha, so the HUD is rendered twice: over black (B) and over white (W).
// For each pixel B = c*a and W = c*a + (1 - a), so the result over the scene S is B + S * (W - B).
Shader "Hidden/Nemesis/UIComposite"
{
    Properties
    {
        _MainTex ("Scene", 2D) = "black" {}
        _UIBlack ("UI over black", 2D) = "black" {}
        _UIWhite ("UI over white", 2D) = "white" {}
    }
    SubShader
    {
        ZTest Always ZWrite Off Cull Off Blend Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _UIBlack, _UIWhite;
            fixed4 frag(v2f_img i) : SV_Target
            {
                float3 s = tex2D(_MainTex, i.uv).rgb;
                float3 b = tex2D(_UIBlack, i.uv).rgb;
                float3 w = tex2D(_UIWhite, i.uv).rgb;
                return fixed4(b + s * saturate(w - b), 1);
            }
            ENDCG
        }
    }
}
