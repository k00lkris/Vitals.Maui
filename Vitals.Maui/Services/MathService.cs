namespace Vitals.Maui.Services;

public static class MathService
{
    /// <summary>
    /// LOESS (Locally Weighted Scatterplot Smoothing)
    /// bandwidth: fraction of points used for each local fit (0.2–0.5 typical)
    /// </summary>
    public static double[] Loess(double[] x, double[] y, double bandwidth = 0.3)
    {
        int n = x.Length;
        if (n < 4) return y.ToArray();

        double[] smoothed = new double[n];
        int windowSize = Math.Max(4, (int)Math.Ceiling(bandwidth * n));

        for (int i = 0; i < n; i++)
        {
            // Find the windowSize nearest neighbors
            var distances = x.Select((xi, idx) => (dist: Math.Abs(xi - x[i]), idx))
                             .OrderBy(d => d.dist)
                             .Take(windowSize)
                             .ToList();

            double maxDist = distances.Max(d => d.dist);
            if (maxDist == 0) maxDist = 1;

            // Tricube weights
            double sumW = 0, sumWx = 0, sumWy = 0, sumWxx = 0, sumWxy = 0;
            foreach (var (dist, idx) in distances)
            {
                double u = dist / maxDist;
                double w = Math.Pow(1 - Math.Pow(u, 3), 3); // tricube
                double xi = x[idx], yi = y[idx];
                sumW += w;
                sumWx += w * xi;
                sumWy += w * yi;
                sumWxx += w * xi * xi;
                sumWxy += w * xi * yi;
            }

            // Weighted linear regression
            double denom = sumW * sumWxx - sumWx * sumWx;
            if (Math.Abs(denom) < 1e-10)
            {
                smoothed[i] = sumWy / sumW;
            }
            else
            {
                double slope = (sumW * sumWxy - sumWx * sumWy) / denom;
                double intercept = (sumWy - slope * sumWx) / sumW;
                smoothed[i] = intercept + slope * x[i];
            }
        }

        return smoothed;
    }

    /// <summary>
    /// Convert DateTime list to normalized double array (days from first)
    /// </summary>
    public static double[] DateTimesToDays(IList<DateTime> dates)
    {
        if (dates.Count == 0) return Array.Empty<double>();
        var origin = dates[0];
        return dates.Select(d => (d - origin).TotalDays).ToArray();
    }
}