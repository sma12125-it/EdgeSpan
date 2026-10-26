namespace EdgeSpan.Product;

/// <summary>
/// Apple-style springs. Response and damping define stiffness and damping
/// with mass 1. A retarget keeps velocity. Reduce motion snaps.
/// </summary>
public static class Motion
{
    public readonly record struct SpringSpec(double Response, double Damping);

    public static readonly SpringSpec Press = new(0.16, 0.92);
    public static readonly SpringSpec Standard = new(0.28, 0.86);
    public static readonly SpringSpec Breakthrough = new(0.32, 0.72);
    public static readonly SpringSpec Toast = new(0.30, 0.84);

    public static bool Reduced(bool appToggle, bool systemToggle) => appToggle || systemToggle;

    public sealed class Spring1D
    {
        private readonly double _stiffness;
        private readonly double _damping;

        public Spring1D(SpringSpec spec, double value = 0)
        {
            var response = spec.Response <= 0.01 ? 0.16 : spec.Response;
            _stiffness = 4 * Math.PI * Math.PI / (response * response);
            _damping = 4 * Math.PI * spec.Damping / response;
            Value = value;
            Target = value;
        }

        public double Value { get; private set; }
        public double Velocity { get; private set; }
        public double Target { get; private set; }

        public void Retarget(double target) => Target = target;

        public void Nudge(double value) => Value = value;

        public bool Step(double dt, bool reduceMotion)
        {
            if (reduceMotion || dt <= 0)
            {
                Value = Target;
                Velocity = 0;
                return true;
            }
            var step = Math.Min(dt, 0.034);
            var acceleration = -_stiffness * (Value - Target) - _damping * Velocity;
            Velocity += acceleration * step;
            Value += Velocity * step;
            if (Math.Abs(Value - Target) < 0.001 && Math.Abs(Velocity) < 0.01)
            {
                Value = Target;
                Velocity = 0;
                return true;
            }
            return false;
        }
    }
}
