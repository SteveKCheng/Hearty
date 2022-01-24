using System;

namespace JobBank.Server.Mocks
{
    /// <summary>
    /// Mathematical functions used to implement mock pricing.
    /// </summary>
    internal static class MathFunctions
    {
        /// <summary>
        /// Computes the probability density function of the standard
        /// Gaussian distribution.
        /// </summary>
        public static double GaussianPdf(double x)
        {
            return Math.Exp(-0.5 * (x * x)) / Math.Sqrt(Math.Tau);
        }

        /// <summary>
        /// Computes the cumulative distribution function 
        /// for the standard Gaussian probability distribution.
        /// </summary>
        public static double GaussianCdf(double x)
        {
            double z = Math.Abs(x);
            double c = 0.0;

            if (z <= 37.0)
            {
                double e = Math.Exp(-0.5 * (z * z));

                if (z < 7.07106781186547)
                {
                    const double n0 = 220.206867912376;
                    const double n1 = 221.213596169931;
                    const double n2 = 112.079291497871;
                    const double n3 = 33.912866078383;
                    const double n4 = 6.37396220353165;
                    const double n5 = 0.700383064443688;
                    const double n6 = 3.52624965998911e-02;
                    const double m0 = 440.413735824752;
                    const double m1 = 793.826512519948;
                    const double m2 = 637.333633378831;
                    const double m3 = 296.564248779674;
                    const double m4 = 86.7807322029461;
                    const double m5 = 16.064177579207;
                    const double m6 = 1.75566716318264;
                    const double m7 = 8.83883476483184e-02;

                    double n = (((((n6 * z + n5) * z + n4) * z + n3) * z + n2) * z + n1) * z + n0;
                    double d = ((((((m7 * z + m6) * z + m5) * z + m4) * z + m3) * z + m2) * z + m1) * z + m0;
                    c = (e * n) / d;
                }
                else
                {
                    double f = z + 1.0 / (z + 2.0 / (z + 3.0 / (z + 4.0 / (z + 13.0 / 20.0))));
                    c = e / (Math.Sqrt(Math.Tau) * f);
                }
            }

            return x <= 0.0 ? c : 1 - c;
        }

        /// <summary>
        /// Computes the inverse of the 
        /// cumulative distribution function for the standard
        /// Gaussian probability distribution.
        /// </summary>
        public static double InverseGaussianCdf(double p)
        {
            // Coefficients in rational approximations.
            const double a1 = -3.969683028665376e+01;
            const double a2 = 2.209460984245205e+02;
            const double a3 = -2.759285104469687e+02;
            const double a4 = 1.383577518672690e+02;
            const double a5 = -3.066479806614716e+01;
            const double a6 = 2.506628277459239e+00;
            const double b1 = -5.447609879822406e+01;
            const double b2 = 1.615858368580409e+02;
            const double b3 = -1.556989798598866e+02;
            const double b4 = 6.680131188771972e+01;
            const double b5 = -1.328068155288572e+01;
            const double c1 = -7.784894002430293e-03;
            const double c2 = -3.223964580411365e-01;
            const double c3 = -2.400758277161838e+00;
            const double c4 = -2.549732539343734e+00;
            const double c5 = 4.374664141464968e+00;
            const double c6 = 2.938163982698783e+00;
            const double d1 = 7.784695709041462e-03;
            const double d2 = 3.224671290700398e-01;
            const double d3 = 2.445134137142996e+00;
            const double d4 = 3.754408661907416e+00;

            const double p_low = 0.02425;
            const double p_high = 1 - p_low;
            double x;

            // Rational approximation for the lower region.
            if (0.0 < p && p < p_low)
            {
                double q = Math.Sqrt(-2.0 * Math.Log(p));
                double numerator = ((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6;
                double denominator = (((d1 * q + d2) * q + d3) * q + d4) * q + 1.0;
                x = numerator / denominator;
            }

            // Rational approximation for the central region.
            else if (p_low <= p && p <= p_high)
            {
                double q = p - 0.5;
                double r = q * q;
                double numerator = (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q;
                double denominator = ((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1.0;
                x = numerator / denominator;
            }

            // Rational approximation for the upper region.
            else if (p_high < p && p < 1.0)
            {
                double q = Math.Sqrt(-2.0 * Math.Log(1 - p));
                double numerator = ((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6;
                double denominator = (((d1 * q + d2) * q + d3) * q + d4) * q + 1.0;
                x = -numerator / denominator;
            }

            else if (p <= 0.0)
            {
                x = double.NegativeInfinity;
            }

            else if (p >= 1.0)
            {
                x = double.PositiveInfinity;
            }

            else
            {
                x = double.NaN;
            }

            return x;
        }
    }
}
