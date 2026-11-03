using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace WaveIsland
{
    /// <summary>Effet « verre liquide » (Glass.hlsl compile en Glass.ps) applique sur l'image du fond.</summary>
    public class GlassEffect : ShaderEffect
    {
        private static readonly PixelShader Shader = new PixelShader
        {
            UriSource = new Uri("pack://application:,,,/WaveIsland;component/Glass.ps", UriKind.Absolute)
        };

        public GlassEffect()
        {
            PixelShader = Shader;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(GeoProperty);
            UpdateShaderValue(SizesProperty);
            UpdateShaderValue(BendProperty);
            UpdateShaderValue(TintProperty);
            UpdateShaderValue(DispProperty);
            UpdateShaderValue(LightProperty);
        }

        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(GlassEffect), 0);

        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        public static readonly DependencyProperty GeoProperty = DependencyProperty.Register(
            "Geo", typeof(Point4D), typeof(GlassEffect), new UIPropertyMetadata(new Point4D(0, 0, 0, 0), PixelShaderConstantCallback(0)));
        public static readonly DependencyProperty SizesProperty = DependencyProperty.Register(
            "Sizes", typeof(Point4D), typeof(GlassEffect), new UIPropertyMetadata(new Point4D(1, 1, 1, 1), PixelShaderConstantCallback(1)));
        public static readonly DependencyProperty LightProperty = DependencyProperty.Register(
            "Light", typeof(Point4D), typeof(GlassEffect), new UIPropertyMetadata(new Point4D(0, -1, 1, 1), PixelShaderConstantCallback(3)));

        public static readonly DependencyProperty BendProperty = DependencyProperty.Register(
            "Bend", typeof(Point4D), typeof(GlassEffect), new UIPropertyMetadata(new Point4D(0, 0, 0, 0), PixelShaderConstantCallback(2)));
        public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
            "Tint", typeof(Point4D), typeof(GlassEffect), new UIPropertyMetadata(new Point4D(0, 0, 0, 0), PixelShaderConstantCallback(4)));
        public static readonly DependencyProperty DispProperty = DependencyProperty.Register(
            "Disp", typeof(Point4D), typeof(GlassEffect), new UIPropertyMetadata(new Point4D(1, 1, 0, 0), PixelShaderConstantCallback(5)));

        public Point4D Bend { get => (Point4D)GetValue(BendProperty); set => SetValue(BendProperty, value); }
        public Point4D Tint { get => (Point4D)GetValue(TintProperty); set => SetValue(TintProperty, value); }
        public Point4D Disp { get => (Point4D)GetValue(DispProperty); set => SetValue(DispProperty, value); }

        /// <summary>Centre (x, y) et demi-taille (z, w) de l'ilot, en pixels de la zone.</summary>
        public Point4D Geo { get => (Point4D)GetValue(GeoProperty); set => SetValue(GeoProperty, value); }
        /// <summary>Taille de la zone (x, y), rayon des coins (z), largeur de la lentille (w).</summary>
        public Point4D Sizes { get => (Point4D)GetValue(SizesProperty); set => SetValue(SizesProperty, value); }
        /// <summary>Lumiere (x, y), opacite (z), loupe (w).</summary>
        public Point4D Light { get => (Point4D)GetValue(LightProperty); set => SetValue(LightProperty, value); }
    }
}
