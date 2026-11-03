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

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv * sz.xy;

    float2 ra = p - a.xy;
    float2 qa = abs(ra) - a.zw;
    float2 ma = max(qa, 0.0);
    float la = length(ma);
    float da = la + min(max(qa.x, qa.y), 0.0) - rad.x;

    float2 rb = p - b.xy;
    float2 qb = abs(rb) - b.zw;
    float2 mb = max(qb, 0.0);
    float lb = length(mb);
    float db = lb + min(max(qb.x, qb.y), 0.0) - rad.y;

    // Fusion douce (comme deux gouttes d'eau)
    float h = saturate(0.5 + 0.5 * (db - da) / rad.z);
    float d = lerp(db, da, h) - rad.z * h * (1.0 - h);

    // Normales des deux formes, melangees
    float2 na = (la > 0.001 ? ma / la : (qa.x > qa.y ? float2(1, 0) : float2(0, 1))) * (ra >= 0 ? 1.0 : -1.0);
    float2 nb = (lb > 0.001 ? mb / lb : (qb.x > qb.y ? float2(1, 0) : float2(0, 1))) * (rb >= 0 ? 1.0 : -1.0);
    float2 n = normalize(lerp(nb, na, h) + 0.0001);

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
