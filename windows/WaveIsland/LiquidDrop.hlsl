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
float4 blend : register(c7); // x = 0.5 / douceur ; y = douceur (px)
float4 inv   : register(c6);
float4 magc  : register(c8); // x = loupe de l'ilot ; yz = centre de la loupe * (1 - loupe), en uv (version complete)
float4 disp  : register(c9); // x = 1 + dispersion ; y = 1 - dispersion (version complete) // xy = 1 / demi-taille de l'ilot ; zw = 1 / demi-taille du menu (pour les normales)

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 ra = uv * sz.xy - a.xy;
    float2 qa = abs(ra) - a.zw;
    float2 ma = max(qa, 0.0);
    float la = length(ma);
    float2 rb = uv * sz.xy - b.xy;
    float2 qb = abs(rb) - b.zw;
    float2 mb = max(qb, 0.0);
    float lb = length(mb);

#ifdef FULL
    // Version complete (carte graphique) : exactement le meme verre que l'ilot -> aucun saut au passage de l'un a l'autre
    float da = la + min(max(qa.x, qa.y), 0.0) - rad.x;
    float db = lb + min(max(qb.x, qb.y), 0.0) - rad.y;
#else
    float da = la - rad.x;
    float db = lb - rad.y;
#endif

    // Fusion douce (comme deux gouttes d'eau)
    float h = saturate(0.5 + (db - da) * blend.x);
    float d = lerp(db, da, h) - blend.y * h * (1.0 - h);

#ifdef FULL
    float2 na = (la > 0.001 ? ma / la : (qa.x > qa.y ? float2(1, 0) : float2(0, 1))) * (ra >= 0 ? 1.0 : -1.0);
    float2 nb = (lb > 0.001 ? mb / lb : (qb.x > qb.y ? float2(1, 0) : float2(0, 1))) * (rb >= 0 ? 1.0 : -1.0);
    float2 n = normalize(lerp(nb, na, h) + 0.00001);
#else
    float2 n = normalize(lerp(rb * inv.zw, ra * inv.xy, h));
#endif

    float t = saturate(1.0 + d * rad.w);
    float k = t * t;

#ifdef FULL
    // Loupe de l'ilot (comme son propre verre), separation des couleurs, bord plus sombre, reflets
    float2 base = uv * lerp(1.0, magc.x, h) + magc.yz * h;
    float2 off = n * k * sz.zw;
    float3 col;
    col.r = tex2D(input, saturate(base + off * disp.x)).r;
    col.g = tex2D(input, saturate(base + off)).g;
    col.b = tex2D(input, saturate(base + off * disp.y)).b;
    float s = saturate(dot(n, light.xy));
    float c = saturate(-dot(n, light.xy));
    s = s * s;
    col = col * (1.0 - 0.2 * k) + (s * s * 0.75 + c * c * c * 0.28) * k;
#else
    float3 col = tex2D(input, saturate(uv + n * k * sz.zw)).rgb;
    float s = saturate(dot(n, light.xy));
    s = s * s;
    col += s * s * 0.75 * k;
#endif

    // Voile (ilot et menu)
    col = lerp(col, tint.rgb, lerp(light.w, tint.a, h));

    // Liseré lumineux
    col += saturate(1.0 - abs(d + 0.9)) * 0.3;

    float al = saturate(0.5 - d) * light.z;
    return float4(col * al, al);
}
