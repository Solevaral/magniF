// The whole lens in one pixel shader: magnified desktop, rounded shape, border,
// shadow and center mark, all antialiased via signed distances.
// Keep this file ASCII-only: the compiler receives the source length in characters.

cbuffer Params : register(b0)
{
    float2 LensPos;       // lens top-left, overlay pixels
    float2 LensSize;
    float2 SrcPos;        // source top-left, desktop texture pixels
    float2 TexSize;
    float  Zoom;
    float  Radius;
    float  Border;
    float  Opacity;
    float4 BorderColor;   // straight alpha
    float4 MarkColor;
    float2 MarkPos;
    float  MarkKind;      // 0 none, 1 dot, 2 cross
    float  MarkScale;
    float  Shadow;
    float  ShadowOpacity;
    float  ShadowOffset;
    float  Smooth;
};

Texture2D Desk : register(t0);
SamplerState Linear : register(s0);

static const float3 Outline = float3(0.04, 0.04, 0.055);

float4 VS(uint id : SV_VertexID) : SV_Position
{
    // One triangle covering the viewport.
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float RoundRect(float2 p, float2 halfSize, float r)
{
    float2 d = abs(p) - halfSize + r;
    return length(max(d, 0)) + min(max(d.x, d.y), 0) - r;
}

float Segment(float2 p, float2 a, float2 b)
{
    float2 pa = p - a, ba = b - a;
    float h = saturate(dot(pa, ba) / dot(ba, ba));
    return length(pa - ba * h);
}

// Catmull-Rom from 9 bilinear taps: sharper than bilinear, no stair-stepping.
float3 SampleBicubic(float2 px)
{
    float2 t1 = floor(px - 0.5) + 0.5;
    float2 f = px - t1;

    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;

    float2 c0 = (t1 - 1) / TexSize;
    float2 c3 = (t1 + 2) / TexSize;
    float2 c12 = (t1 + w2 / w12) / TexSize;

    float3 r = 0;
    r += Desk.SampleLevel(Linear, float2(c0.x,  c0.y),  0).rgb * w0.x  * w0.y;
    r += Desk.SampleLevel(Linear, float2(c12.x, c0.y),  0).rgb * w12.x * w0.y;
    r += Desk.SampleLevel(Linear, float2(c3.x,  c0.y),  0).rgb * w3.x  * w0.y;
    r += Desk.SampleLevel(Linear, float2(c0.x,  c12.y), 0).rgb * w0.x  * w12.y;
    r += Desk.SampleLevel(Linear, float2(c12.x, c12.y), 0).rgb * w12.x * w12.y;
    r += Desk.SampleLevel(Linear, float2(c3.x,  c12.y), 0).rgb * w3.x  * w12.y;
    r += Desk.SampleLevel(Linear, float2(c0.x,  c3.y),  0).rgb * w0.x  * w3.y;
    r += Desk.SampleLevel(Linear, float2(c12.x, c3.y),  0).rgb * w12.x * w3.y;
    r += Desk.SampleLevel(Linear, float2(c3.x,  c3.y),  0).rgb * w3.x  * w3.y;
    return saturate(r);
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 p = pos.xy;
    float2 halfSize = LensSize * 0.5;
    float2 center = LensPos + halfSize;
    float d = RoundRect(p - center, halfSize, Radius);
    float inside = saturate(0.5 - d);

    float3 color = 0;
    if (inside > 0)
    {
        float2 px = clamp(SrcPos + (p - LensPos) / Zoom, 0.5, TexSize - 0.5);
        color = Smooth > 0.5 ? SampleBicubic(px) : Desk.Load(int3(px, 0)).rgb;

        if (Border > 0)
            color = lerp(color, BorderColor.rgb, saturate(d + Border + 0.5) * BorderColor.a);

        if (MarkKind > 0.5)
        {
            float s = MarkScale;
            float dist, outer, inner;
            if (MarkKind < 1.5)
            {
                dist = length(p - MarkPos);
                outer = 4.2 * s;
                inner = 3.0 * s;
            }
            else
            {
                float gap = 3.5 * s, arm = 8.0 * s;
                float2 q = p - MarkPos;
                dist = min(min(Segment(q, float2(-gap - arm, 0), float2(-gap, 0)),
                               Segment(q, float2(gap, 0), float2(gap + arm, 0))),
                           min(Segment(q, float2(0, -gap - arm), float2(0, -gap)),
                               Segment(q, float2(0, gap), float2(0, gap + arm))));
                outer = 1.75 * s;
                inner = 0.8 * s;
            }
            color = lerp(color, Outline, saturate(outer + 0.5 - dist) * 0.67 * MarkColor.a);
            color = lerp(color, MarkColor.rgb, saturate(inner + 0.5 - dist) * MarkColor.a);
        }
    }

    float shadow = 0;
    if (Shadow > 0)
    {
        float ds = RoundRect(p - center - float2(0, ShadowOffset), halfSize, Radius);
        float k = saturate(1 - max(ds, 0) / Shadow);
        shadow = ShadowOpacity * k * k;
    }

    // Premultiplied alpha: lens color over a black shadow.
    float alpha = inside + shadow * (1 - inside);
    return float4(color * inside, alpha) * Opacity;
}
