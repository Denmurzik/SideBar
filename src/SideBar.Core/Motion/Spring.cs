namespace SideBar.Core.Motion;

/// <summary>Аналитическая затухающая пружина; прерывается с сохранением позиции и скорости.</summary>
public sealed class Spring(double zeta = 0.8, double responseSec = 0.35)
{
    double _t0, _x0, _v0;

    public double Position { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }

    public bool IsSettled => Math.Abs(Position - Target) < 0.5 && Math.Abs(Velocity) < 5;

    public void Reset(double position)
    {
        Position = position;
        Target = position;
        Velocity = 0;
        _t0 = 0;
        _x0 = 0;
        _v0 = 0;
    }

    public void Retarget(double target, double nowSec)
    {
        Step(nowSec);
        Target = target;
        _t0 = nowSec;
        _x0 = Position - target;
        _v0 = Velocity;
    }

    public void Step(double nowSec)
    {
        double t = Math.Max(nowSec - _t0, 0);
        double w = 2 * Math.PI / responseSec, z = Math.Min(zeta, 0.999);
        double wd = w * Math.Sqrt(1 - z * z);
        double e = Math.Exp(-z * w * t);
        double c1 = _x0, c2 = (_v0 + z * w * _x0) / wd;
        double cos = Math.Cos(wd * t), sin = Math.Sin(wd * t);
        Position = Target + e * (c1 * cos + c2 * sin);
        Velocity = e * ((-z * w) * (c1 * cos + c2 * sin) + wd * (-c1 * sin + c2 * cos));
    }
}
