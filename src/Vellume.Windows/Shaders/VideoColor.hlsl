sampler2D Input : register(s0);
float Brightness : register(c0);
float Contrast : register(c1);
float Saturation : register(c2);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 color = tex2D(Input, uv);
    float3 rgb = (color.rgb * Brightness - 0.5) * Contrast + 0.5;
    float luminance = dot(rgb, float3(0.213, 0.715, 0.072));
    return float4(saturate(lerp(luminance.xxx, rgb, Saturation)), color.a);
}
