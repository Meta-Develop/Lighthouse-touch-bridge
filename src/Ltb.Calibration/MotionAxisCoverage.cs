using System.Numerics;

namespace Ltb.Calibration;

internal static class MotionAxisCoverage
{
    public static double Compute(IEnumerable<Vector3> axes)
    {
        ArgumentNullException.ThrowIfNull(axes);

        var xx = 0d;
        var xy = 0d;
        var xz = 0d;
        var yy = 0d;
        var yz = 0d;
        var zz = 0d;
        var count = 0;
        foreach (var candidate in axes)
        {
            if (!IsFinite(candidate) || candidate.LengthSquared() <= 1e-12f)
            {
                continue;
            }

            var axis = Vector3.Normalize(candidate);
            xx += axis.X * axis.X;
            xy += axis.X * axis.Y;
            xz += axis.X * axis.Z;
            yy += axis.Y * axis.Y;
            yz += axis.Y * axis.Z;
            zz += axis.Z * axis.Z;
            count++;
        }

        if (count == 0)
        {
            return 0d;
        }

        var eigenvalues = Eigenvalues(xx, xy, xz, yy, yz, zz);
        Array.Sort(eigenvalues);
        return eigenvalues[2] > 1e-12d
            ? Math.Clamp(eigenvalues[1] / eigenvalues[2], 0d, 1d)
            : 0d;
    }

    private static double[] Eigenvalues(
        double xx,
        double xy,
        double xz,
        double yy,
        double yz,
        double zz)
    {
        var offDiagonalEnergy = (xy * xy) + (xz * xz) + (yz * yz);
        if (offDiagonalEnergy <= 1e-24d)
        {
            return [xx, yy, zz];
        }

        var mean = (xx + yy + zz) / 3d;
        var centeredEnergy =
            ((xx - mean) * (xx - mean)) +
            ((yy - mean) * (yy - mean)) +
            ((zz - mean) * (zz - mean)) +
            (2d * offDiagonalEnergy);
        var scale = Math.Sqrt(centeredEnergy / 6d);
        if (scale <= 1e-12d)
        {
            return [mean, mean, mean];
        }

        var bxx = (xx - mean) / scale;
        var bxy = xy / scale;
        var bxz = xz / scale;
        var byy = (yy - mean) / scale;
        var byz = yz / scale;
        var bzz = (zz - mean) / scale;
        var determinant =
            (bxx * ((byy * bzz) - (byz * byz))) -
            (bxy * ((bxy * bzz) - (byz * bxz))) +
            (bxz * ((bxy * byz) - (byy * bxz)));
        var angle = Math.Acos(Math.Clamp(determinant / 2d, -1d, 1d)) / 3d;
        var largest = mean + (2d * scale * Math.Cos(angle));
        var smallest = mean + (2d * scale * Math.Cos(angle + (2d * Math.PI / 3d)));
        var middle = (3d * mean) - largest - smallest;
        return [largest, middle, smallest];
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
