using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ABC.DiscoveryCity.Embeddings
{
    public class SentenceTrainer
    {
        public SentenceQuantizer Train(List<float[]> fullVectors, int subspaces = 16, int centroids = 256)
        {
            if (fullVectors.Count == 0) throw new ArgumentException("No training vectors provided.");
            
            int totalDims = fullVectors[0].Length;
            if (totalDims % subspaces != 0) 
                throw new ArgumentException($"Total dims {totalDims} is not cleanly divisible by {subspaces} subspaces.");
            
            int subDims = totalDims / subspaces;
            var quantizer = new SentenceQuantizer(subspaces, centroids, subDims);

            Parallel.For(0, subspaces, i =>
            {
                Console.WriteLine($"Training Codebook {i+1}/{subspaces} (dim {i*subDims}..{i*subDims+subDims-1})...");
                
                // Extract all subvectors for this slice
                var sliceData = new List<float[]>();
                int offset = i * subDims;
                foreach (var vec in fullVectors)
                {
                    float[] subvec = new float[subDims];
                    Array.Copy(vec, offset, subvec, 0, subDims);
                    sliceData.Add(subvec);
                }

                var kmeans = new KMeansTrainer(centroids, maxIterations: 50, seed: 42 + i);
                float[][] trainedCentroids = kmeans.Train(sliceData);
                
                quantizer.SetCodebook(i, trainedCentroids);
                Console.WriteLine($"Codebook {i+1}/{subspaces} finished.");
            });

            return quantizer;
        }
    }
}
