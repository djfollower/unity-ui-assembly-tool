using UnityEngine;

namespace UiAssemblerSlice.Editor.Matcher
{
    // Hand-port of node_modules/ssim.js's DEFAULT configuration (the one
    // matcher/visual-signal.ts calls with no options): windowSize 11, k1
    // 0.01, k2 0.03, bitDepth 8, rgb2grayVersion "integer", ssim "weber",
    // downsample "original". Only that one configured path is ported - not
    // the other rgb2gray/ssim/downsample variants ssim.js also offers, since
    // nothing in this codebase selects them.
    //
    // "original" downsample only kicks in when
    // min(width,height)/maxSize(256) rounds to a factor > 1 - VisualSignal
    // always compares two COMPARISON_SIZE (64x64) images, so
    // 64/256 = 0.25 rounds to 0 and downsampling is always a no-op here.
    // Not ported - would need ssim.js's matlab-style symmetric-padding box
    // filter for no reason this codebase exercises.
    //
    // weberSsim.js stores intermediate variance/covariance sums in a
    // JS Int32Array scaled by 1024 (a fixed-point trick for perf) - each
    // assignment there implicitly truncates toward zero. Replicated exactly
    // via C#'s int cast (also truncates toward zero) so this stays
    // numerically faithful to what matcher/gate.ts's tuned thresholds were
    // actually calibrated against, not just "an SSIM implementation".
    internal static class Ssim
    {
        private const int WindowSize = 11;
        private const double K1 = 0.01;
        private const double K2 = 0.03;
        private const int BitDepth = 8;

        // ssim.js's matlab/rgb2gray.js::rgb2grayInteger, verbatim (integer
        // bit-shift arithmetic, not floating-point luma).
        private static int[] ToGrayscale(Color32[] pixels)
        {
            var gray = new int[pixels.Length];
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                gray[i] = (77 * p.r + 150 * p.g + 29 * p.b + 128) >> 8;
            }
            return gray;
        }

        // weberSsim.js::partialSumMatrix1 - summed-area table (integral
        // image) of f(value) over a (width+1) x (height+1) grid, computed
        // bottom-right to top-left exactly like the original.
        private static int[] PartialSumMatrix(int[] data, int width, int height, System.Func<int, int> f)
        {
            var matrixWidth = width + 1;
            var matrixHeight = height + 1;
            var sum = new int[matrixWidth * matrixHeight];
            for (var h = height - 1; h >= 0; --h)
            {
                for (var w = width - 1; w >= 0; --w)
                {
                    var rightEdge = sum[h * matrixWidth + w + 1];
                    var bottomEdge = sum[(h + 1) * matrixWidth + w];
                    var bottomRightEdge = sum[(h + 1) * matrixWidth + w + 1];
                    sum[h * matrixWidth + w] = f(data[h * width + w]) + rightEdge + bottomEdge - bottomRightEdge;
                }
            }
            return sum;
        }

        // Two-array variant for covariance (f(a,b) instead of f(a)).
        private static int[] PartialSumMatrix2(int[] data1, int[] data2, int width, int height)
        {
            var matrixWidth = width + 1;
            var matrixHeight = height + 1;
            var sum = new int[matrixWidth * matrixHeight];
            for (var h = height - 1; h >= 0; --h)
            {
                for (var w = width - 1; w >= 0; --w)
                {
                    var rightEdge = sum[h * matrixWidth + w + 1];
                    var bottomEdge = sum[(h + 1) * matrixWidth + w];
                    var bottomRightEdge = sum[(h + 1) * matrixWidth + w + 1];
                    var offset = h * width + w;
                    sum[h * matrixWidth + w] = data1[offset] * data2[offset] + rightEdge + bottomEdge - bottomRightEdge;
                }
            }
            return sum;
        }

        // weberSsim.js::windowMatrix - box sum over each windowSize x
        // windowSize window via the integral image, divisor always 1 at
        // every call site here (matches the original).
        private static int[] WindowMatrix(int[] sumArray, int imageWidth, int imageHeight, int windowSize, out int windowWidth, out int windowHeight)
        {
            var matrixWidth = imageWidth + 1;
            windowWidth = imageWidth - windowSize + 1;
            windowHeight = imageHeight - windowSize + 1;
            var windows = new int[windowWidth * windowHeight];
            for (var h = 0; h < windowHeight; ++h)
            {
                for (var w = 0; w < windowWidth; ++w)
                {
                    var sum =
                        sumArray[matrixWidth * h + w] -
                        sumArray[matrixWidth * h + w + windowSize] -
                        sumArray[matrixWidth * (h + windowSize) + w] +
                        sumArray[matrixWidth * (h + windowSize) + w + windowSize];
                    windows[h * windowWidth + w] = sum;
                }
            }
            return windows;
        }

        private static int[] WindowSums(int[] pixels, int width, int height, int windowSize, out int windowWidth, out int windowHeight)
        {
            var sat = PartialSumMatrix(pixels, width, height, v => v);
            return WindowMatrix(sat, width, height, windowSize, out windowWidth, out windowHeight);
        }

        // weberSsim.js::windowVariance - note the 1024-scaled, truncated-
        // to-int fixed-point storage (see the class doc comment).
        private static int[] WindowVariance(int[] pixels, int width, int height, int[] sums, int windowSize)
        {
            var windowSquared = windowSize * windowSize;
            var sat = PartialSumMatrix(pixels, width, height, v => v * v);
            var varX = WindowMatrix(sat, width, height, windowSize, out _, out _);
            for (var i = 0; i < sums.Length; ++i)
            {
                double mean = (double)sums[i] / windowSquared;
                double sumSquares = (double)varX[i] / windowSquared;
                double squareMeans = mean * mean;
                varX[i] = (int)(1024 * (sumSquares - squareMeans));
            }
            return varX;
        }

        // weberSsim.js::windowCovariance - same 1024-scaled truncation.
        private static int[] WindowCovariance(int[] pixels1, int[] pixels2, int width, int height, int[] sums1, int[] sums2, int windowSize)
        {
            var windowSquared = windowSize * windowSize;
            var sat = PartialSumMatrix2(pixels1, pixels2, width, height);
            var covXY = WindowMatrix(sat, width, height, windowSize, out _, out _);
            for (var i = 0; i < sums1.Length; ++i)
            {
                double cov = (double)covXY[i] / windowSquared;
                double meanX = (double)sums1[i] / windowSquared;
                double meanY = (double)sums2[i] / windowSquared;
                covXY[i] = (int)(1024 * (cov - meanX * meanY));
            }
            return covXY;
        }

        // matcher/visual-signal.ts's `ssim(imageA, imageB).mssim` - mean
        // SSIM over the whole image, weber algorithm, default options.
        // pixels1/pixels2 must be the same width x height, at least
        // WindowSize (11) on each axis (true for every real call site here -
        // both are always resized to VisualSignal.ComparisonSize first).
        public static double Compare(Color32[] pixels1, Color32[] pixels2, int width, int height)
        {
            var gray1 = ToGrayscale(pixels1);
            var gray2 = ToGrayscale(pixels2);

            var L = System.Math.Pow(2, BitDepth) - 1;
            var c1 = K1 * L * (K1 * L);
            var c2 = K2 * L * (K2 * L);
            var windowSquared = WindowSize * WindowSize;

            var sums1 = WindowSums(gray1, width, height, WindowSize, out var windowWidth, out var windowHeight);
            var variance1 = WindowVariance(gray1, width, height, sums1, WindowSize);
            var sums2 = WindowSums(gray2, width, height, WindowSize, out _, out _);
            var variance2 = WindowVariance(gray2, width, height, sums2, WindowSize);
            var covariance = WindowCovariance(gray1, gray2, width, height, sums1, sums2, WindowSize);

            var size = windowWidth * windowHeight;
            double mssim = 0;
            for (var i = 0; i < size; ++i)
            {
                double meanx = (double)sums1[i] / windowSquared;
                double meany = (double)sums2[i] / windowSquared;
                double varx = (double)variance1[i] / 1024;
                double vary = (double)variance2[i] / 1024;
                double cov = (double)covariance[i] / 1024;
                double na = 2 * meanx * meany + c1;
                double nb = 2 * cov + c2;
                double da = meanx * meanx + meany * meany + c1;
                double db = varx + vary + c2;
                double ssimValue = (na * nb) / da / db;
                mssim = i == 0 ? ssimValue : mssim + (ssimValue - mssim) / (i + 1);
            }
            return mssim;
        }
    }
}
