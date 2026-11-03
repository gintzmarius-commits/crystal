// Verre liquide de la Wave Island (pixel shader WPF, ps_2_0 pour marcher aussi sans carte graphique).
// L'entree est l'image de ce qu'il y a DERRIERE l'ilot (la fenetre s'exclut de la capture).
// Tout l'interieur est vu a travers le verre : legere loupe, forte courbure sur les bords
// (comme une goutte d'eau), separation des couleurs, bord plus sombre, reflets selon la lumiere.

sampler2D input : register(s0);

float4 geo   : register(c0); // x,y = centre de l'ilot (px) ; z,w = demi-largeur, demi-hauteur (px)
float4 sizes : register(c1); // x,y = taille de la zone (px) ; z = rayon des coins (px) ; w = largeur de la lentille (px)
float4 opt   : register(c2); // x = force de courbure (px) ; y = dispersion ; z = teinte (0..1) ; w = 1 sombre / 0 clair
float4 light : register(c3); // x,y = direction de la lumiere (normalisee) ; z = opacite ; w = loupe (ex. 0.95)

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = uv * sizes.xy;
    float2 r = p - geo.xy;

    // Distance signee au rectangle arrondi (negative a l'interieur)
    float2 q = abs(r) - geo.zw + sizes.z;
    float2 qm = max(q, 0.0);
    float lq = length(qm);
    float d = min(max(q.x, q.y), 0.0) + lq - sizes.z;
    float aa = saturate(0.5 - d);

    // Normale du bord (calculee directement, sans echantillons supplementaires)
    float2 nIn = q.x > q.y ? float2(1, 0) : float2(0, 1);
    float2 n = (lq > 0.001 ? qm / max(lq, 0.001) : nIn) * sign(r);

    float t = saturate(1.0 + d / sizes.w);   // 1 au bord, 0 vers le centre
    float k = t * t;

    // Loupe douce au centre + courbure forte au bord (le fond est pris plus loin vers l'exterieur)
    float2 inv = 1.0 / sizes.xy;
    float2 base = geo.xy + r * light.w;
    float2 off = n * (opt.x * k);
    float3 col;
    col.r = tex2D(input, saturate((base + off * (1.0 + opt.y)) * inv)).r;
    col.g = tex2D(input, saturate((base + off) * inv)).g;
    col.b = tex2D(input, saturate((base + off * (1.0 - opt.y)) * inv)).b;

    // Comme l'eau : le bord s'assombrit un peu
    col *= 1.0 - 0.2 * k;

    // Reflets : la lumiere accroche le bord tourne vers elle et ressort de l'autre cote
    float s = saturate(dot(n, light.xy));
    float c = saturate(-dot(n, light.xy));
    s = s * s;
    col += (s * s * 0.75 + c * c * c * 0.28) * k;

    // Teinte pour lire le contenu (0 = verre transparent)
    float3 tintCol = lerp(float3(0.97, 0.98, 1.0), float3(0.05, 0.06, 0.08), opt.w);
    col = lerp(col, tintCol, opt.z);

    // Fin liseré lumineux sur le contour
    col += saturate(1.0 - abs(d + 0.9) / 1.1) * 0.32;

    float a = aa * light.z;
    return float4(col * a, a);
}
