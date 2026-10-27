// Cached optical maps are derived from liquid-glass-react (MIT).
// Geometry is calculated only when a surface resizes; live pixels are shaded by WPF.
sampler2D backdrop : register(s0);
sampler2D optics : register(s1);
sampler2D rim : register(s2);
float4 geometry : register(c0); // source width, height, unused, core tint
float4 tintColor : register(c1);
float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 map = tex2D(optics, uv);
    float4 edge = tex2D(rim, uv);
    float2 bend = (map.rg - 128.0/255.0) * 64.0 / geometry.xy;
    float3 color = float3(tex2D(backdrop, uv-bend*1.06).r,
        tex2D(backdrop, uv-bend).g, tex2D(backdrop, uv-bend*.94).b);
    float2 dx = float2(map.b * 2 / geometry.x, 0);
    float2 dy = float2(0, map.b * 2 / geometry.y);
    color = color * .6 + (tex2D(backdrop, uv-bend-dx).rgb + tex2D(backdrop, uv-bend+dx).rgb
        + tex2D(backdrop, uv-bend-dy).rgb + tex2D(backdrop, uv-bend+dy).rgb) * .1;
    return float4(saturate(lerp(color, tintColor.rgb, geometry.w * edge.b) + edge.r) * edge.g, edge.g);
}
