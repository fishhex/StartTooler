using System;
using System.IO;
using System.Linq;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StartTooler.Services;

public static class ImageHashService
{
    private const int HashSize = 8;
    private const int ReducedSize = 32;
    private const long DefaultHash = 0;

    public static long ComputePerceptualHash(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return DefaultHash;
        }

        try
        {
            using var image = Image.Load<Rgba32>(filePath);
            image.Mutate(ctx => ctx.Resize(ReducedSize, ReducedSize).Grayscale());

            var pixels = new float[ReducedSize * ReducedSize];
            int index = 0;
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < ReducedSize; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < ReducedSize; x++)
                    {
                        pixels[index++] = row[x].R;
                    }
                }
            });

            var dctValues = Dct2D(pixels, ReducedSize, ReducedSize);
            var avg = dctValues
                .Take(HashSize * HashSize)
                .Skip(1)
                .Average();

            ulong hash = 0;
            for (int i = 0; i < HashSize * HashSize; i++)
            {
                hash <<= 1;
                hash |= dctValues[i] > avg ? 1UL : 0UL;
            }

            return (long)hash;
        }
        catch (Exception)
        {
            return DefaultHash;
        }
    }

    private static float[] Dct2D(float[] data, int width, int height)
    {
        var result = new float[width * height];
        var basis = new float[height, height];

        for (int i = 0; i < height; i++)
        {
            for (int j = 0; j < height; j++)
            {
                basis[i, j] = (float)Math.Cos(((2 * i + 1) * j * Math.PI) / (2 * height));
            }
        }

        for (int u = 0; u < height; u++)
        {
            for (int v = 0; v < width; v++)
            {
                double sum = 0;
                for (int i = 0; i < height; i++)
                {
                    for (int j = 0; j < width; j++)
                    {
                        sum += data[i * width + j] * basis[i, u] * basis[j, v];
                    }
                }

                double cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
                double cv = v == 0 ? 1 / Math.Sqrt(2) : 1;
                result[u * width + v] = (float)(0.25 * cu * cv * sum);
            }
        }

        return result;
    }
}
