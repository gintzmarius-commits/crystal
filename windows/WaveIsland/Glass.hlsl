// Verre liquide de la Wave Island (pixel shader WPF, ps_2_0 : marche aussi sans carte graphique).
// L'entree est l'image de ce qu'il y a DERRIERE l'ilot (la fenetre s'exclut de la capture).
// Tout l'interieur est vu a travers le verre : legere loupe, forte courbure sur les bords
// (comme une goutte d'eau), separation des couleurs, bord plus sombre, reflets selon la lumiere.
// Les calculs constants sont faits a l'avance cote C# (limite de 64 instructions).

sampler2D input : register(s0);

float4 geo   : register(c0); // x,y = centre (px) ; z,w = demi-taille moins le rayon (px)
float4 sizes : register(c1); // x,y = taille de la zone (px) ; z = rayon des coins (px) ; w = 1 / largeur de la lentille
float4 bend  : register(c2); // x,y = courbure convertie en uv ; z,w = decalage de la loupe (uv)
float4 light : register(c3); // x,y = direction de la lumiere (normalisee) ; z = opacite ; w = loupe (ex. 0.95)
float4 tint  : register(c4); // rgb = couleur du voile ; a = force du voile (0 = verre transparent)
float4 disp  : register(c5); // x = 1 + dispersion ; y = 1 - dispersion

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 r = uv * sizes.xy - geo.xy;

    // Distance signee au rectangle arrondi (negative a l'interieur)
    float2 q = abs(r) - geo.zw;
    float2 qm = max(q, 0.0);
    float lq = length(qm);
    float d = min(max(q.x, q.y), 0.0) + lq - sizes.z;

    // Normale du bord
    float2 nIn = q.x > q.y ? float2(1, 0) : float2(0, 1);
    float2 n = lq > 0.001 ? qm / lq : nIn;
    n *= r >= 0 ? 1.0 : -1.0;

    float t = saturate(1.0 + d * sizes.w);   // 1 au bord, 0 vers le centre
    float k = t * t;

    // Loupe douce + courbure forte au bord (le fond est pris plus loin vers l'exterieur)
    float2 base = uv * light.w + bend.zw;
    float2 off = n * k * bend.xy;
    float3 col;
    col.r = tex2D(input, saturate(base + off * disp.x)).r;
    col.g = tex2D(input, saturate(base + off)).g;
    col.b = tex2D(input, saturate(base + off * disp.y)).b;

    // Comme l'eau : le bord s'assombrit un peu ; reflets selon la lumiere
    float s = saturate(dot(n, light.xy));
    float c = saturate(-dot(n, light.xy));
    s = s * s;
    col = col * (1.0 - 0.2 * k) + (s * s * 0.75 + c * c * c * 0.28) * k;

    // Voile pour lire le contenu
    col = lerp(col, tint.rgb, tint.a);

    // Fin liseré lumineux sur le contour
    col += saturate(1.0 - abs(d + 0.9)) * 0.3;

    float a = saturate(0.5 - d) * light.z;
    return float4(col * a, a);
}
