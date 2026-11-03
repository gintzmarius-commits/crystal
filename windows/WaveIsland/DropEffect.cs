using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace WaveIsland
{
    /// <summary>Effet « goutte qui se detache » (LiquidDrop.hlsl compile en LiquidDrop.ps).</summary>
    public class DropEffect : ShaderEffect
    {
        private static readonly PixelShader Shader = new PixelShader
        {
            UriSource = new Uri("pack://application:,,,/WaveIsland;component/LiquidDrop.ps", UriKind.Absolute)
        };

        public DropEffect()
        {
            PixelShader = Shader;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(AProperty);
            UpdateShaderValue(BProperty);
            UpdateShaderValue(RadProperty);
            UpdateShaderValue(SzProperty);
            UpdateShaderValue(TintProperty);
            UpdateShaderValue(LightProperty);
            UpdateShaderValue(InvProperty);
            UpdateShaderValue(BlendProperty);
        }

        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(DropEffect), 0);
        public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

        private static DependencyProperty Reg(string name, int reg, Point4D def) => DependencyProperty.Register(
            name, typeof(Point4D), typeof(DropEffect), new UIPropertyMetadata(def, PixelShaderConstantCallback(reg)));

        public static readonly DependencyProperty AProperty = Reg("A", 0, new Point4D(0, 0, 0, 0));
        public static readonly DependencyProperty BProperty = Reg("B", 1, new Point4D(0, 0, 0, 0));
        public static readonly DependencyProperty RadProperty = Reg("Rad", 2, new Point4D(1, 1, 1, 1));
        public static readonly DependencyProperty SzProperty = Reg("Sz", 3, new Point4D(1, 1, 0, 0));
        public static readonly DependencyProperty TintProperty = Reg("Tint", 4, new Point4D(0, 0, 0, 0));
        public static readonly DependencyProperty LightProperty = Reg("Light", 5, new Point4D(-0.5, -0.866, 1, 0));
        public static readonly DependencyProperty InvProperty = Reg("Inv", 6, new Point4D(1, 1, 1, 1));
        public static readonly DependencyProperty BlendProperty = Reg("Blend", 7, new Point4D(0.02, 30, 0, 0));
        public Point4D Blend { get => (Point4D)GetValue(BlendProperty); set => SetValue(BlendProperty, value); }
        public Point4D Inv { get => (Point4D)GetValue(InvProperty); set => SetValue(InvProperty, value); }

        public Point4D A { get => (Point4D)GetValue(AProperty); set => SetValue(AProperty, value); }
        public Point4D B { get => (Point4D)GetValue(BProperty); set => SetValue(BProperty, value); }
        public Point4D Rad { get => (Point4D)GetValue(RadProperty); set => SetValue(RadProperty, value); }
        public Point4D Sz { get => (Point4D)GetValue(SzProperty); set => SetValue(SzProperty, value); }
        public Point4D Tint { get => (Point4D)GetValue(TintProperty); set => SetValue(TintProperty, value); }
        public Point4D Light { get => (Point4D)GetValue(LightProperty); set => SetValue(LightProperty, value); }
    }
}
