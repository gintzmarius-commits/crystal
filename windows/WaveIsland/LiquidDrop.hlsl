// Goutte de verre liquide en deux morceaux (ilot + menu) qui se collent et se decollent comme de l'eau.
// Deux rectangles arrondis fusionnes en douceur : quand ils s'eloignent, le pont entre eux s'affine puis casse.
// ps_2_0 : les calculs constants sont faits a l'avance cote C#.

sampler2D input : register(s0);

float4 a     : register(c0); // ilot : centre xy (px) ; zw = demi-taille moins le rayon (px)
float4 b     : register(c1); // menu : idem
float4 rad   : register(c2); // x = rayon ilot ; y = rayon menu ; z = douceur de la fusion (px) ; w = 1 / largeur de la lentille
float4 sz    : register(c3); // xy = taille de la zone (px) ; zw = courbure convertie en uv
float4 tint  : register(c4); // rgb = couleur du voile ; a = voile de l'ilot
float4 light : register(c5); // xy = direction de la lumiere ; z = opacite ; w = voile du menu
float4 inv   : register(c6); // xy = 1 / demi-taille de l'ilot ; zw = 1 / demi-taille du menu (pour les normales)

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv * sz.xy;

    float2 ra = p - a.xy;
    float2 qa = abs(ra) - a.zw;
    float2 ma = max(qa, 0.0);
    float la = length(ma);
    float da = la - rad.x; // (approche suffisante a l'interieur)

    float2 rb = p - b.xy;
    float2 qb = abs(rb) - b.zw;
    float2 mb = max(qb, 0.0);
    float lb = length(mb);
    float db = lb - rad.y;

    // Fusion douce (comme deux gouttes d'eau)
    float h = saturate(0.5 + 0.5 * (db - da) / rad.z);
    float d = lerp(db, da, h) - rad.z * h * (1.0 - h);

    // Normales (approchees par l'ellipse de chaque forme), melangees
    float2 n = normalize(lerp(rb * inv.zw, ra * inv.xy, h));

    float t = saturate(1.0 + d * rad.w);
    float k = t * t;

    // Le fond vu a travers le verre, tire vers l'exterieur sur les bords
    float3 col = tex2D(input, saturate(uv + n * k * sz.zw)).rgb;
    col *= 1.0 - 0.2 * k;
    float s = saturate(dot(n, light.xy));
    s = s * s;
    col += s * s * 0.75 * k;

    // Voile (ilot transparent, menu un peu plus voile pour lire les icones)
    col = lerp(col, tint.rgb, lerp(light.w, tint.a, h));

    // Liseré lumineux
    col += saturate(1.0 - abs(d + 0.9)) * 0.3;

    float al = saturate(0.5 - d) * light.z;
    return float4(col * al, al);
}
