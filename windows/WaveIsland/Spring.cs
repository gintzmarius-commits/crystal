using System;
using System.Diagnostics;
using System.Windows.Media;

namespace WaveIsland
{
    /// <summary>
    /// Ressort physique facon Apple (meme formule que l'appli Android) :
    /// damping &lt; 1 = rebond, response = duree d'une oscillation en secondes.
    /// </summary>
    public class Spring
    {
        public readonly double Damping, Response;
        private readonly double _w, _wd, _seconds;

        public Spring(double damping, double response)
        {
            Damping = damping;
            Response = response;
            _w = 2 * Math.PI / response;
            _wd = _w * Math.Sqrt(1 - damping * damping);
            _seconds = Math.Log(1000) / (damping * _w);
        }

        public double Seconds => _seconds;

        /// <summary>Progression 0..1 (avec depassement) apres t secondes.</summary>
        public double At(double t)
        {
            if (t >= _seconds) return 1;
            double decay = Math.Exp(-Damping * _w * t);
            return 1 - decay * (Math.Cos(_wd * t) + Damping * _w / _wd * Math.Sin(_wd * t));
        }

        public static readonly Spring Open = new Spring(0.62, 0.46);
        public static readonly Spring OpenW = new Spring(0.6, 0.4);
        public static readonly Spring Close = new Spring(0.8, 0.38);
        public static readonly Spring Soft = new Spring(0.75, 0.4);
        public static readonly Spring Pop = new Spring(0.45, 0.42);
    }

    /// <summary>Une valeur animee par un ressort, mise a jour a chaque image.</summary>
    public class SpringValue
    {
        private double _from, _to;
        private Spring _spring = Spring.Soft;
        private readonly Stopwatch _clock = new Stopwatch();
        public double Value { get; private set; }
        public double Target => _to;
        public bool Moving { get; private set; }

        public SpringValue(double v) { Value = _from = _to = v; }

        public void AnimateTo(double to, Spring s)
        {
            _from = Value;
            _to = to;
            _spring = s;
            _clock.Restart();
            Moving = true;
        }

        public void Set(double v) { Value = _from = _to = v; Moving = false; }

        /// <summary>Avance l'animation ; renvoie true si la valeur a change.</summary>
        public bool Tick()
        {
            if (!Moving) return false;
            double t = _clock.Elapsed.TotalSeconds;
            if (t >= _spring.Seconds) { Value = _to; Moving = false; return true; }
            Value = _from + (_to - _from) * _spring.At(t);
            return true;
        }
    }
}
