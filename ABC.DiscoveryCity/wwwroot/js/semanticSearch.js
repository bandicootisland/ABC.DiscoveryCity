// semanticSearch.js — Client-side semantic search via Transformers.js + PQ codebook
// Generates mxbai-embed-large-v1 embeddings in the browser, quantizes to 16-byte GUID
// matching the server-side SentenceQuantizer format.

(function () {
    'use strict';

    let _extractor = null;    // Transformers.js pipeline
    let _codebook = null;     // Float32Array[16][256][64] flattened
    let _initPromise = null;
    let _modelReady = false;
    let _codebookReady = false;

    // PQ geometry (must match server SentenceQuantizer)
    const SUBSPACES = 16;
    const CENTROIDS = 256;
    const SUB_DIM = 64;
    const TOTAL_DIM = SUBSPACES * SUB_DIM; // 1024

    // -----------------------------------------------------------------------
    // Codebook loader — parses the binary SQ file format
    // -----------------------------------------------------------------------
    async function loadCodebook(url) {
        const response = await fetch(url);
        if (!response.ok) throw new Error(`Failed to load codebook: ${response.status}`);
        const buffer = await response.arrayBuffer();
        const view = new DataView(buffer);

        // Header: magic(4) + subspaces(4) + centroids(4) + subdim(4)
        const magic = view.getInt32(0, true); // little-endian
        if (magic !== 0x01535101) throw new Error('Invalid SQ codebook magic header');

        const nSub = view.getInt32(4, true);
        const nCent = view.getInt32(8, true);
        const nDim = view.getInt32(12, true);

        if (nSub !== SUBSPACES || nCent !== CENTROIDS || nDim !== SUB_DIM) {
            throw new Error(`Codebook geometry mismatch: ${nSub}x${nCent}x${nDim}, expected ${SUBSPACES}x${CENTROIDS}x${SUB_DIM}`);
        }

        // Floats start at offset 16
        _codebook = new Float32Array(buffer, 16);
        _codebookReady = true;
        console.log(`[SemanticSearch] Codebook loaded: ${nSub}x${nCent}x${nDim} (${(_codebook.length * 4 / 1024).toFixed(0)} KB)`);
    }

    // -----------------------------------------------------------------------
    // Model loader — lazy-loads Transformers.js and the ONNX model
    // -----------------------------------------------------------------------
    async function loadModel() {
        console.log('[SemanticSearch] Loading Transformers.js and mxbai-embed-large-v1...');
        const { pipeline } = await import('https://cdn.jsdelivr.net/npm/@huggingface/transformers@3');

        _extractor = await pipeline('feature-extraction', 'mixedbread-ai/mxbai-embed-large-v1', {
            quantized: true,
            progress_callback: (progress) => {
                if (progress.status === 'progress' && progress.progress) {
                    console.log(`[SemanticSearch] Model download: ${progress.progress.toFixed(1)}%`);
                }
            }
        });

        _modelReady = true;
        console.log('[SemanticSearch] Model ready.');
    }

    // -----------------------------------------------------------------------
    // PQ Quantization — mirrors SentenceQuantizer.Quantize() in C#
    // -----------------------------------------------------------------------
    function quantize(embedding) {
        if (!_codebookReady) throw new Error('Codebook not loaded');
        if (embedding.length !== TOTAL_DIM) {
            throw new Error(`Expected ${TOTAL_DIM}-dim embedding, got ${embedding.length}`);
        }

        const bytes = new Uint8Array(SUBSPACES);

        for (let s = 0; s < SUBSPACES; s++) {
            let bestCentroid = 0;
            let bestDist = Infinity;
            const vecOffset = s * SUB_DIM;
            const cbBase = s * CENTROIDS * SUB_DIM;

            for (let c = 0; c < CENTROIDS; c++) {
                let dist = 0;
                const centOffset = cbBase + c * SUB_DIM;

                for (let d = 0; d < SUB_DIM; d++) {
                    const diff = embedding[vecOffset + d] - _codebook[centOffset + d];
                    dist += diff * diff;
                }

                if (dist < bestDist) {
                    bestDist = dist;
                    bestCentroid = c;
                }
            }
            bytes[s] = bestCentroid;
        }

        return bytesToDotNetGuid(bytes);
    }

    // -----------------------------------------------------------------------
    // GUID formatting — matches .NET's new Guid(byte[16]) layout
    // -----------------------------------------------------------------------
    function bytesToDotNetGuid(bytes) {
        const h = b => b.toString(16).padStart(2, '0');

        // .NET Guid(byte[]) interprets:
        //   bytes[0..3] as int32 LE → display big-endian hex
        //   bytes[4..5] as int16 LE → display big-endian hex
        //   bytes[6..7] as int16 LE → display big-endian hex
        //   bytes[8..15] as-is
        const a = h(bytes[3]) + h(bytes[2]) + h(bytes[1]) + h(bytes[0]);
        const b = h(bytes[5]) + h(bytes[4]);
        const c = h(bytes[7]) + h(bytes[6]);
        const d = h(bytes[8]) + h(bytes[9]);
        const e = h(bytes[10]) + h(bytes[11]) + h(bytes[12]) + h(bytes[13]) + h(bytes[14]) + h(bytes[15]);

        return `${a}-${b}-${c}-${d}-${e}`;
    }

    // -----------------------------------------------------------------------
    // Public API (exposed on window.semanticSearch)
    // -----------------------------------------------------------------------
    window.semanticSearch = {

        /** Initialize model + codebook. Safe to call multiple times (idempotent). */
        init: function (codebookUrl) {
            if (_initPromise) return _initPromise;
            codebookUrl = codebookUrl || 'data/sq_codebook.bin';

            _initPromise = Promise.all([
                loadModel(),
                loadCodebook(codebookUrl)
            ]).then(() => {
                console.log('[SemanticSearch] Initialization complete.');
                return true;
            }).catch(err => {
                console.error('[SemanticSearch] Initialization failed:', err);
                _initPromise = null; // allow retry
                throw err;
            });

            return _initPromise;
        },

        /** Check if both model and codebook are loaded. */
        isReady: function () {
            return _modelReady && _codebookReady;
        },

        /** Embed text and quantize to a .NET-compatible GUID string. */
        getSemanticGuid: async function (text) {
            if (!_modelReady || !_codebookReady) {
                throw new Error('SemanticSearch not initialized. Call init() first.');
            }

            // Truncate to ~512 tokens (same as server)
            if (text.length > 2000) text = text.substring(0, 2000);

            // Generate embedding (CLS pooling, normalized — mxbai requires this)
            const output = await _extractor(text, { pooling: 'cls', normalize: true });
            const embedding = Array.from(output.data);

            // Quantize to GUID
            return quantize(embedding);
        },

        /** Embed text and return raw float array (for debugging / future use). */
        getEmbedding: async function (text) {
            if (!_modelReady) {
                throw new Error('Model not initialized. Call init() first.');
            }
            if (text.length > 2000) text = text.substring(0, 2000);
            const output = await _extractor(text, { pooling: 'cls', normalize: true });
            return Array.from(output.data);
        }
    };
})();
