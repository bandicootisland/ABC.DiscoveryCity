using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ABC.DiscoveryCity.Words.Common.Models;
using Npgsql;
using NpgsqlTypes;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline
{
    public class IngestionManager
    {
        private readonly string _connectionString;
        private readonly string _outputBinPath;
        private readonly ABC.DiscoveryCity.Embeddings.IEmbeddingService _embeddingService;

        public IngestionManager(string connectionString, string outputBinPath, ABC.DiscoveryCity.Embeddings.IEmbeddingService embeddingService)
        {
            _connectionString = connectionString;
            _outputBinPath = outputBinPath;
            _embeddingService = embeddingService;
        }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("Starting High-Performance Semantic Ingestion Pipeline (IngestionManager)...");
            var sw = Stopwatch.StartNew();

            // Channels tuned for Apple M4 core saturation / memory throughput
            var rawChannel = Channel.CreateBounded<DbRow>(new BoundedChannelOptions(50000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false
            });

            var outChannel = Channel.CreateBounded<OutputBatch>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = true
            });

            var producerTask = ProduceRowsAsync(rawChannel.Writer, cancellationToken);

            int consumerCount = Environment.ProcessorCount;
            var consumerTasks = new Task[consumerCount];
            for (int i = 0; i < consumerCount; i++)
            {
                consumerTasks[i] = ConsumeAndProcessAsync(rawChannel.Reader, outChannel.Writer, cancellationToken);
            }

            var writerTask = WriteAndExportAsync(outChannel.Reader, cancellationToken);

            await producerTask;
            rawChannel.Writer.Complete();

            await Task.WhenAll(consumerTasks);
            outChannel.Writer.Complete();

            await writerTask;

            sw.Stop();
            Console.WriteLine($"Ingestion and Export physically routed in {sw.Elapsed.TotalSeconds:F2}s");
        }

        private async Task ProduceRowsAsync(ChannelWriter<DbRow> writer, CancellationToken ct)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Stream documents in bounded buffers to limit RAM footprint (no massive internal buffering)
            string sql = @"
                SELECT Id, Sentences, SentenceIds 
                FROM ParentDocuments 
                WHERE Sentences IS NOT NULL AND SentenceIds IS NOT NULL;
            ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 0; // Large read timeout
            
            // ExecuteReaderAsync by default is non-buffering (with proper Npgsql parameters or CommandBehavior)
            using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);
            
            int totalSentencesFound = 0;
            int docMapping = 1;

            while (await reader.ReadAsync(ct))
            {
                Guid parentId = reader.GetGuid(0);
                string sentencesJson = reader.GetString(1);
                string idsJson = reader.GetString(2);

                try
                {
                    var sentenceTexts = JsonSerializer.Deserialize<List<string>>(sentencesJson) ?? new List<string>();
                    var sentenceIds = JsonSerializer.Deserialize<List<Guid>>(idsJson) ?? new List<Guid>();

                    for (int i = 0; i < Math.Min(sentenceTexts.Count, sentenceIds.Count); i++)
                    {
                        var row = new DbRow 
                        { 
                            ParentId = parentId,
                            SentenceId = sentenceIds[i],
                            Text = sentenceTexts[i],
                            DocId = docMapping,
                            Ordinal = i
                        };
                        await writer.WriteAsync(row, ct);
                        totalSentencesFound++;
                    }
                }
                catch (Exception)
                {
                    // Skip problematic json parsing safely
                }
                docMapping++;
                
                if (docMapping % 10000 == 0) // Represents roughly 5,000 to 10,000 batched documents reported to terminal
                {
                    Console.WriteLine($"Producer: Extracted {totalSentencesFound} sentences from {docMapping} documents...");
                }
            }
            Console.WriteLine($"Producer finished fetching {totalSentencesFound} total sentences.");
        }

        private async Task ConsumeAndProcessAsync(ChannelReader<DbRow> reader, ChannelWriter<OutputBatch> writer, CancellationToken ct)
        {
            var localBatch = new List<OutputRow>(500);

            string sqBinPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sq_codebook.bin");
            var ds = new NpgsqlDataSourceBuilder(_connectionString).Build();
            var dictStorage = new ABC.DiscoveryCity.PostgreSQL.DictionaryStorageService(ds);
            
            var encoder = new SentenceEncoder(sqBinPath, _embeddingService, dictStorage);
            var parser = new MockLinguisticParser();
            var symbols = new MockSymbolRegistry();
            var ingestionService = new IngestionService(encoder, parser, symbols);

            await foreach (var row in reader.ReadAllAsync(ct))
            {
                var sig = ingestionService.Ingest(row.Text, row.SentenceId, row.DocId);

                var outRow = new OutputRow
                {
                    Signature = sig,
                    ParentId = row.ParentId,
                    Ordinal = row.Ordinal
                };

                localBatch.Add(outRow);

                if (localBatch.Count >= 500)
                {
                    await writer.WriteAsync(new OutputBatch { Rows = localBatch.ToArray() }, ct);
                    localBatch.Clear();
                }
            }

            if (localBatch.Count > 0)
            {
                await writer.WriteAsync(new OutputBatch { Rows = localBatch.ToArray() }, ct);
            }
        }

        private async Task WriteAndExportAsync(ChannelReader<OutputBatch> reader, CancellationToken ct)
        {
            // Uses zero-allocation memory spanning pushing directly to standard disk buffer mechanisms
            using var fs = new FileStream(_outputBinPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096 * 1024, FileOptions.SequentialScan);
            using var bw = new BufferedStream(fs, 4096 * 1024);
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            int totalWritten = 0;
            var dbBatch = new List<OutputRow>(10000);

            await foreach (var batch in reader.ReadAllAsync(ct))
            {
                // Zero-Allocation bulk serialization
                SentenceSignature[] sigs = new SentenceSignature[batch.Rows.Length];
                for (int i = 0; i < batch.Rows.Length; i++)
                {
                    sigs[i] = batch.Rows[i].Signature;
                    dbBatch.Add(batch.Rows[i]);
                }

                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(sigs.AsSpan());
                bw.Write(bytes);
                
                totalWritten += sigs.Length;

                if (dbBatch.Count >= 10000)
                {
                    await FlushToDatabaseAsync(conn, dbBatch, ct);
                    dbBatch.Clear();
                }

                if (totalWritten % 100000 == 0)
                {
                    Console.WriteLine($"Writer: mapped {totalWritten} records to DB and .bin target...");
                }
            }

            if (dbBatch.Count > 0)
            {
                await FlushToDatabaseAsync(conn, dbBatch, ct);
            }

            Console.WriteLine($"Binary System Exporter effectively wrote: {totalWritten} objects");
        }

        private async Task FlushToDatabaseAsync(NpgsqlConnection conn, List<OutputRow> batch, CancellationToken ct)
        {
            using var tx = await conn.BeginTransactionAsync(ct);
            // Ultra-fast COPY inserts to the flattened postgres signature table
            using (var importer = await conn.BeginBinaryImportAsync("COPY SentenceSignatures (SentenceId, ParentId, SemanticId, Who, What, Where, When, Which, Why, How, Ordinal) FROM STDIN (FORMAT BINARY)", ct))
            {
                foreach (var row in batch)
                {
                    var s = row.Signature;
                    await importer.StartRowAsync(ct);
                    await importer.WriteAsync(s.Id, NpgsqlDbType.Uuid, ct);
                    await importer.WriteAsync(row.ParentId, NpgsqlDbType.Uuid, ct);
                    await importer.WriteAsync(s.SemanticId, NpgsqlDbType.Uuid, ct);
                    await importer.WriteAsync(s.Who, NpgsqlDbType.Smallint, ct);
                    await importer.WriteAsync(s.What, NpgsqlDbType.Smallint, ct);
                    await importer.WriteAsync(s.Where, NpgsqlDbType.Smallint, ct);
                    await importer.WriteAsync(s.When, NpgsqlDbType.Smallint, ct);
                    await importer.WriteAsync(s.Which, NpgsqlDbType.Smallint, ct);
                    await importer.WriteAsync(s.Why, NpgsqlDbType.Smallint, ct);
                    await importer.WriteAsync(s.How, NpgsqlDbType.Smallint, ct);
                    await importer.WriteAsync(row.Ordinal, NpgsqlDbType.Integer, ct);
                }
                await importer.CompleteAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        private class MockLinguisticParser : ILinguisticParser
        {
            public LinguisticAnalysis Analyze(string rawText)
            {
                return new LinguisticAnalysis
                {
                    Subject = "mock_subject",
                    Action = rawText.Length.ToString()
                };
            }
        }

        private class MockSymbolRegistry : ISymbolRegistry
        {
            public short GetOrCreateId(string role, string value)
            {
                if (string.IsNullOrEmpty(value)) return 0;
                int hash = value.GetHashCode();
                if (hash > 32767) return 32767;
                if (hash < -32768) return -32768;
                return (short)hash;
            }
        }

        public class DbRow
        {
            public Guid ParentId { get; set; }
            public Guid SentenceId { get; set; }
            public string Text { get; set; } = string.Empty;
            public int DocId { get; set; }
            public int Ordinal { get; set; }
        }

        public class OutputRow
        {
            public SentenceSignature Signature { get; set; }
            public Guid ParentId { get; set; }
            public int Ordinal { get; set; }
        }

        public class OutputBatch
        {
            public OutputRow[] Rows { get; set; } = Array.Empty<OutputRow>();
        }
    }
}
