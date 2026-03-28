using System;
using System.IO;

namespace ABC.DiscoveryCity.Embeddings
{
    public class SentenceQuantizer
    {
        public int Subspaces { get; }
        public int CentroidsPerSubspace { get; }
        public int SubspaceDimensions { get; }

        public float[][][] Codebooks { get; private set; } // [subspace][centroidId][dimension]

        public SentenceQuantizer(int subspaces = 16, int centroids = 256, int subspaceDimensions = 64)
        {
            Subspaces = subspaces;
            CentroidsPerSubspace = centroids;
            SubspaceDimensions = subspaceDimensions;
            Codebooks = new float[Subspaces][][];
            for (int i = 0; i < Subspaces; i++)
            {
                Codebooks[i] = new float[CentroidsPerSubspace][];
                for (int c = 0; c < CentroidsPerSubspace; c++)
                    Codebooks[i][c] = new float[SubspaceDimensions];
            }
        }

        public void Load(string filePath)
        {
            using var fs = File.OpenRead(filePath);
            using var br = new BinaryReader(fs);

            // Read magic/header (PQ -> SQ magic could be kept or updated. User said initials become sq, so updating magic to match intent)
            int magic = br.ReadInt32(); // e.g. 0x01535101 (SQ instead of PQ)
            if (magic != 0x01535101) throw new InvalidDataException("Invalid SQ file header.");
            
            int cSub = br.ReadInt32();
            int cCent = br.ReadInt32();
            int cDim = br.ReadInt32();

            if (cSub != Subspaces || cCent != CentroidsPerSubspace || cDim != SubspaceDimensions)
                throw new InvalidDataException($"Mismatch geometry. Expected {Subspaces}x{CentroidsPerSubspace}x{SubspaceDimensions}. Got {cSub}x{cCent}x{cDim}");

            for (int i = 0; i < Subspaces; i++)
            {
                for (int c = 0; c < CentroidsPerSubspace; c++)
                {
                    for (int d = 0; d < SubspaceDimensions; d++)
                    {
                        Codebooks[i][c][d] = br.ReadSingle();
                    }
                }
            }
        }

        public void Save(string filePath)
        {
            using var fs = File.Create(filePath);
            using var bw = new BinaryWriter(fs);

            bw.Write(0x01535101); // Magic header SQ
            bw.Write(Subspaces);
            bw.Write(CentroidsPerSubspace);
            bw.Write(SubspaceDimensions);

            for (int i = 0; i < Subspaces; i++)
            {
                for (int c = 0; c < CentroidsPerSubspace; c++)
                {
                    for (int d = 0; d < SubspaceDimensions; d++)
                    {
                        bw.Write(Codebooks[i][c][d]);
                    }
                }
            }
        }

        public void SetCodebook(int subspace, float[][] trainedCentroids)
        {
            Codebooks[subspace] = trainedCentroids;
        }

        public Guid Quantize(float[] vector)
        {
            if (vector.Length != Subspaces * SubspaceDimensions)
                throw new ArgumentException($"Expected vector of {Subspaces * SubspaceDimensions} dims, got {vector.Length}");

            byte[] bytes = new byte[Subspaces];

            for (int i = 0; i < Subspaces; i++)
            {
                int bestCentroid = 0;
                float bestDist = float.MaxValue;

                int offset = i * SubspaceDimensions;

                for (int c = 0; c < CentroidsPerSubspace; c++)
                {
                    float dist = 0f;
                    float[] centroid = Codebooks[i][c];
                    
                    for (int d = 0; d < SubspaceDimensions; d++)
                    {
                        float diff = vector[offset + d] - centroid[d];
                        dist += diff * diff;
                    }

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestCentroid = c;
                    }
                }
                bytes[i] = (byte)bestCentroid;
            }

            return new Guid(bytes);
        }
    }
}
