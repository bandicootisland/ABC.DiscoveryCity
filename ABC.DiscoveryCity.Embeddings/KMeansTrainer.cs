using System;
using System.Collections.Generic;

namespace ABC.DiscoveryCity.Embeddings
{
    public class KMeansTrainer
    {
        private readonly int _k;
        private readonly int _maxIterations;
        private readonly int _seed;

        public KMeansTrainer(int k = 256, int maxIterations = 100, int seed = 42)
        {
            _k = k;
            _maxIterations = maxIterations;
            _seed = seed;
        }

        public float[][] Train(List<float[]> data)
        {
            if (data == null || data.Count == 0) throw new ArgumentException("Data is empty");
            if (data.Count < _k) throw new ArgumentException($"Need at least {_k} samples to train {_k} clusters.");

            int dims = data[0].Length;
            var random = new Random(_seed);

            // 1. Initialize centroids randomly (K-Means++)
            float[][] centroids = new float[_k][];
            var usedIndices = new HashSet<int>();
            for (int i = 0; i < _k; i++)
            {
                int idx;
                do { idx = random.Next(data.Count); } while (!usedIndices.Add(idx));
                centroids[i] = (float[])data[idx].Clone();
            }

            int[] assignments = new int[data.Count];
            bool changed;
            int iteration = 0;

            do
            {
                changed = false;
                iteration++;

                // 2. Assign to nearest centroid
                for (int i = 0; i < data.Count; i++)
                {
                    float[] pt = data[i];
                    int bestCluster = 0;
                    float bestDist = float.MaxValue;
                    for (int c = 0; c < _k; c++)
                    {
                        float dist = SqlDistSquare(pt, centroids[c]);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestCluster = c;
                        }
                    }
                    if (assignments[i] != bestCluster)
                    {
                        assignments[i] = bestCluster;
                        changed = true;
                    }
                }

                // 3. Recompute centroids
                float[][] newCentroids = new float[_k][];
                int[] counts = new int[_k];
                for (int c = 0; c < _k; c++) newCentroids[c] = new float[dims];

                for (int i = 0; i < data.Count; i++)
                {
                    int cluster = assignments[i];
                    counts[cluster]++;
                    float[] pt = data[i];
                    for (int d = 0; d < dims; d++)
                    {
                        newCentroids[cluster][d] += pt[d];
                    }
                }

                for (int c = 0; c < _k; c++)
                {
                    if (counts[c] > 0)
                    {
                        for (int d = 0; d < dims; d++)
                            newCentroids[c][d] /= counts[c];
                        centroids[c] = newCentroids[c];
                    }
                }

            } while (changed && iteration < _maxIterations);

            return centroids;
        }

        private float SqlDistSquare(float[] a, float[] b)
        {
            float sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                float diff = a[i] - b[i];
                sum += diff * diff;
            }
            return sum;
        }
    }
}
