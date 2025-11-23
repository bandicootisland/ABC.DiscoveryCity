
window.fileSystem = {
    dirHandle: null,
    dataDirHandle: null,

    selectDirectory: async function () {
        try {
            this.dirHandle = await window.showDirectoryPicker();
            return this.dirHandle.name;
        } catch (e) {
            console.error(e);
            return null;
        }
    },

    selectDataDirectory: async function () {
        try {
            this.dataDirHandle = await window.showDirectoryPicker();
            return this.dataDirHandle.name;
        } catch (e) {
            console.error(e);
            return null;
        }
    },

    listFiles: async function () {
        if (!this.dirHandle) return [];
        
        const files = [];
        for await (const entry of this.dirHandle.values()) {
            if (entry.kind === 'file') {
                const file = await entry.getFile();
                files.push({
                    name: entry.name,
                    size: file.size,
                    lastModified: file.lastModified
                });
            }
        }
        return files;
    },

    readHistoryFile: async function () {
        if (!this.dataDirHandle) return null;
        try {
            const fileHandle = await this.dataDirHandle.getFileHandle('import_history.json');
            const file = await fileHandle.getFile();
            const text = await file.text();
            return text;
        } catch (e) {
            console.error("History file not found or readable", e);
            return null;
        }
    },

    readFileHeader: async function (fileName, bytesToRead) {
        if (!this.dirHandle) return null;

        try {
            const fileHandle = await this.dirHandle.getFileHandle(fileName);
            const file = await fileHandle.getFile();
            
            // Slice the file to get just the header
            const blob = file.slice(0, bytesToRead);
            const buffer = await blob.arrayBuffer();
            return new Uint8Array(buffer);
        } catch (e) {
            console.error(e);
            return null;
        }
    },

    readFileChunk: async function (fileName, start, length) {
        if (!this.dirHandle) return null;
        try {
            const fileHandle = await this.dirHandle.getFileHandle(fileName);
            const file = await fileHandle.getFile();
            
            // Handle negative start (read from end)
            let startPos = start;
            if (start < 0) {
                startPos = file.size + start;
                if (startPos < 0) startPos = 0;
            }

            const blob = file.slice(startPos, startPos + length);
            const buffer = await blob.arrayBuffer();
            return new Uint8Array(buffer);
        } catch (e) {
            console.error(e);
            return null;
        }
    }
};
